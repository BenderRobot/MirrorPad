// Capture de l'écran virtuel avec ffmpeg (ddagrab) ; la vidéo sort par un tube et part chiffrée dans la session.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace MirrorPad
{
    public class Streamer
    {
        public static readonly Dictionary<string, string> EncoderNames = new Dictionary<string, string> {
            { "qsv", "Intel Quick Sync" }, { "qsv-sys", "Intel Quick Sync (compat.)" },
            { "nvenc", "NVIDIA NVENC" }, { "x264", "Processeur (x264)" } };
        public static readonly string[] EncoderOrder = { "qsv", "qsv-sys", "nvenc", "x264" };

        readonly Config cfg;
        Process proc;
        public string ActiveEncoder = "";
        public DateTime StartedAt;

        public Streamer(Config c) { cfg = c; }

        // ---- Débit adaptatif : si le réseau n'arrive plus à suivre, on baisse la qualité plutôt que de prendre du retard ----
        public int LiveMbps;                 // débit réellement utilisé (≤ réglage) ; 0 = réglage
        public volatile bool AdaptRestart;   // ffmpeg relancé par l'adaptation (pas un échec)
        public Action<int, int> Stats;       // chaque seconde : saturation réseau (%), débit utilisé (Mb/s)
        int congested, relaxed; DateTime lastDrop = DateTime.MinValue;
        int Mbps { get { return LiveMbps > 0 ? Math.Min(LiveMbps, cfg.Mbps) : cfg.Mbps; } }
        public void ResetAdapt() { LiveMbps = 0; congested = relaxed = 0; }

        // appelé chaque seconde avec la part du temps passée bloquée à envoyer (0..1)
        void Adapt(double blocked)
        {
            int cur = Mbps;
            if (Stats != null) Stats((int)Math.Round(blocked * 100), cur);
            if (blocked > 0.35) { congested++; relaxed = 0; }
            else if (blocked < 0.05) { relaxed++; congested = 0; }
            else { congested = 0; relaxed = 0; }
            if (congested >= 2 && cur > 3)
            {
                LiveMbps = Math.Max(3, (int)(cur * 0.7)); congested = 0; lastDrop = DateTime.Now;
                Log.Write("réseau saturé (" + (int)(blocked * 100) + " %) : débit " + cur + " → " + LiveMbps + " Mb/s");
                AdaptRestart = true; Stop();
            }
            else if (relaxed >= 15 && cur < cfg.Mbps && (DateTime.Now - lastDrop).TotalSeconds > 30)
            {
                LiveMbps = Math.Min(cfg.Mbps, (int)Math.Ceiling(cur * 1.25)); relaxed = 0;
                Log.Write("réseau dégagé : débit " + cur + " → " + LiveMbps + " Mb/s");
                AdaptRestart = true; Stop();
            }
        }

        public static string FindFfmpeg()
        {
            // ffmpeg livré par l'installateur, à côté de MirrorPad.exe
            var local = Path.Combine(Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath), "ffmpeg.exe");
            if (File.Exists(local)) return local;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                try { var p = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
            }
            var links = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe");
            return File.Exists(links) ? links : null;
        }

        public string EncoderArgs(string e)
        {
            int gop = cfg.Fps * 2;
            int mb = Mbps;
            // tampon de débit court (≈ 1/6 s) : les images clés restent petites, donc pas de pic de latence
            string rate = string.Format(System.Globalization.CultureInfo.InvariantCulture, "-b:v {0}M -maxrate {0}M -bufsize {1}k", mb, Math.Max(500, mb * 1000 / 6));
            string qsv = "-c:v h264_qsv -preset veryfast -look_ahead 0 -async_depth 1 -bf 0 -profile:v high -g " + gop + " " + rate;
            switch (e)
            {
                case "qsv": return "-vf hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12 " + qsv;
                case "qsv-sys": return "-vf hwdownload,format=bgra,format=nv12 " + qsv;
                case "nvenc": return "-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr -delay 0 -profile:v high -g " + gop + " -b:v " + mb + "M -bufsize " + Math.Max(500, mb * 1000 / 6) + "k";
                default: return "-vf hwdownload,format=bgra,format=yuv420p -c:v libx264 -preset ultrafast -tune zerolatency -profile:v high -g " + gop + " " + rate;
            }
        }

        // Essaie chaque encodeur sur 10 images et garde le premier qui fonctionne (résultat mémorisé)
        public string PickEncoder(string ffmpeg, Action<string> notify)
        {
            if (cfg.Encoder != "auto") return cfg.Encoder;
            if (!string.IsNullOrEmpty(cfg.DetectedEncoder)) return cfg.DetectedEncoder;
            if (notify != null) notify("Recherche du meilleur encodeur vidéo…");
            string found = "x264";
            foreach (var e in new[] { "qsv", "qsv-sys", "nvenc" })
            {
                var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel quiet -f lavfi -i ddagrab=output_idx=" + cfg.Output +
                    ":framerate=30 -frames:v 10 " + EncoderArgs(e) + " -f null -") { UseShellExecute = false, CreateNoWindow = true };
                try
                {
                    using (var p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } continue; }
                        if (p.ExitCode == 0) { found = e; break; }
                    }
                }
                catch { }
            }
            cfg.DetectedEncoder = found; cfg.Save();
            Log.Write("encodeur retenu : " + EncoderNames[found]);
            return found;
        }

        // Lance ffmpeg et pompe la vidéo vers la session jusqu'à l'arrêt de ffmpeg ou de la session
        public void Run(string ffmpeg, Session s)
        {
            var args = "-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=" + cfg.Output + ":framerate=" + cfg.Fps +
                       " " + EncoderArgs(ActiveEncoder) + " -f flv -flvflags no_duration_filesize -flush_packets 1 pipe:1";
            var psi = new ProcessStartInfo(ffmpeg, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            var rep = Path.Combine(Config.Dir, "ffmpeg.log").Replace('\\', '/').Replace(":", "\\:");
            psi.EnvironmentVariables["FFREPORT"] = "file=" + rep + ":level=16";
            proc = Process.Start(psi);
            StartedAt = DateTime.Now;
            s.Send("V NEW");
            var buf = new byte[64 * 1024];
            var stdout = proc.StandardOutput.BaseStream;
            AdaptRestart = false;
            var window = Stopwatch.StartNew(); long blockedTicks = 0;
            try
            {
                int n;
                while (!s.IsClosed && (n = stdout.Read(buf, 0, buf.Length)) > 0)
                {
                    long t = Stopwatch.GetTimestamp();
                    s.SendFrame(Session.Video, buf, 0, n);   // bloque quand le Wi-Fi ne suit plus
                    blockedTicks += Stopwatch.GetTimestamp() - t;
                    if (window.ElapsedMilliseconds >= 1000)
                    {
                        double blocked = (double)blockedTicks / Stopwatch.Frequency / window.Elapsed.TotalSeconds;
                        blockedTicks = 0; window.Restart();
                        if ((DateTime.Now - StartedAt).TotalSeconds > 2) Adapt(blocked);   // pas pendant le démarrage
                    }
                }
            }
            catch (Exception e) { Log.Write("flux vidéo interrompu : " + e.Message); }
            Stop();
            if (!s.IsClosed) s.Send("V END");
        }

        public void Stop()
        {
            var p = proc;
            if (p == null) return;
            try { if (!p.HasExited) p.Kill(); } catch { }
            proc = null;
        }

        public bool Running { get { var p = proc; try { return p != null && !p.HasExited; } catch { return false; } } }
    }
}
