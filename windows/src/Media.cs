// Capteurs de l'iPad côté PC : webcam virtuelle (akvirtualcamera), micro (VB-CABLE), presse-papiers, photos reçues.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MirrorPad
{
    // ---------- Webcam : H.264 de l'iPad -> ffmpeg (décodage) -> AkVCamManager stream (webcam virtuelle) ----------
    public class CameraBridge
    {
        public const string DeviceId = "MirrorPadCamera";
        public const int W = 1280, H = 720, Fps = 30;
        Process decoder, sink;
        Thread pump;
        public Action<string> Notify;          // message pour l'utilisateur (bulle + iPad)
        public Func<string, int> RunElevated;  // exécute du PowerShell en administrateur (code de sortie)

        public static string FindManager()
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var d in new[] { "AkVirtualCamera", "akvirtualcamera" })
                {
                    var dir = Path.Combine(root, d);
                    if (!Directory.Exists(dir)) continue;
                    var all = Directory.GetFiles(dir, "AkVCamManager.exe", SearchOption.AllDirectories);
                    var x64 = all.FirstOrDefault(f => f.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0);
                    if (x64 != null) return x64;
                    if (all.Length > 0) return all[0];
                }
            }
            return null;
        }

        static string Run(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using (var p = Process.Start(psi)) { var o = p.StandardOutput.ReadToEnd(); p.WaitForExit(15000); return o; }
        }

        // Crée une fois la webcam « MirrorPad (iPad) » (droits administrateur nécessaires)
        bool EnsureDevice(string mgr)
        {
            var list = Run(mgr, "-p devices");
            if (list.Split('\n').Any(l => l.Trim() == DeviceId)) return true;
            Log.Write("création de la webcam virtuelle MirrorPad");
            var code = "& '" + mgr + "' add-device -i " + DeviceId + " 'MirrorPad (iPad)'\n" +
                       "& '" + mgr + "' add-format " + DeviceId + " RGB24 " + W + " " + H + " " + Fps + "\n" +
                       "& '" + mgr + "' update\nexit 0";
            int rc = RunElevated != null ? RunElevated(code) : -1;
            list = Run(mgr, "-p devices");
            return list.Split('\n').Any(l => l.Trim() == DeviceId);
        }

        public void Start(string ffmpeg)
        {
            Stop();
            var mgr = FindManager();
            if (mgr == null) { Notify("Webcam : installez d'abord akvirtualcamera (github.com/webcamoid/akvirtualcamera)."); return; }
            try
            {
                if (!EnsureDevice(mgr)) { Notify("Webcam : impossible de créer la webcam virtuelle (autorisation refusée ?)."); return; }
                sink = Process.Start(new ProcessStartInfo(mgr, "stream -f " + Fps + " " + DeviceId + " RGB24 " + W + " " + H) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
                decoder = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -f h264 -i pipe:0 " +
                    "-vf scale=" + W + ":" + H + ":force_original_aspect_ratio=decrease,pad=" + W + ":" + H + ":(ow-iw)/2:(oh-ih)/2,format=rgb24 -f rawvideo pipe:1")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
                var d = decoder; var s = sink;
                pump = new Thread(() =>
                {
                    var buf = new byte[W * H * 3];
                    try
                    {
                        var src = d.StandardOutput.BaseStream; var dst = s.StandardInput.BaseStream;
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) { dst.Write(buf, 0, n); dst.Flush(); }
                    }
                    catch { }
                }) { IsBackground = true, Name = "webcam" };
                pump.Start();
                Log.Write("webcam MirrorPad active");
            }
            catch (Exception e) { Notify("Webcam : " + e.Message); Stop(); }
        }

        public void Feed(byte[] h264)
        {
            var d = decoder;
            if (d == null) return;
            try { d.StandardInput.BaseStream.Write(h264, 0, h264.Length); d.StandardInput.BaseStream.Flush(); } catch { }
        }

        public void Stop()
        {
            foreach (var p in new[] { decoder, sink })
            {
                if (p == null) continue;
                try { p.StandardInput.Close(); } catch { }
                try { if (!p.WaitForExit(500)) p.Kill(); } catch { }
            }
            if (decoder != null) Log.Write("webcam MirrorPad arrêtée");
            decoder = null; sink = null;
        }
    }

    // ---------- Micro : PCM 16 bits de l'iPad joué dans « CABLE Input » (VB-CABLE) ----------
    public class MicPlayer
    {
        [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; public uint dwSupport; }
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(UIntPtr id, ref WAVEOUTCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, UIntPtr id, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        IntPtr dev = IntPtr.Zero;
        readonly List<IntPtr> pending = new List<IntPtr>();
        static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        public Action<string> Notify;

        public static int FindCable()
        {
            uint n = waveOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var c = new WAVEOUTCAPS();
                if (waveOutGetDevCaps(new UIntPtr(i), ref c, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0 && c.szPname != null && c.szPname.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return (int)i;
            }
            return -1;
        }

        public void Start(int rate, int channels)
        {
            Stop();
            int id = FindCable();
            if (id < 0) { Notify("Micro : installez VB-CABLE (vb-audio.com/Cable), puis choisissez « CABLE Output » comme micro dans vos applis."); return; }
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = 16 };
            f.nBlockAlign = (ushort)(channels * 2); f.nAvgBytesPerSec = (uint)(rate * f.nBlockAlign);
            if (waveOutOpen(out dev, new UIntPtr((uint)id), ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { dev = IntPtr.Zero; Notify("Micro : impossible d'ouvrir VB-CABLE."); return; }
            Log.Write("micro de l'iPad -> CABLE Input (" + rate + " Hz)");
        }

        public void Play(byte[] pcm)
        {
            if (dev == IntPtr.Zero || pcm.Length == 0) return;
            lock (pending)
            {
                // libère les tampons déjà joués ; au-delà de ~300 ms d'attente on jette (latence minimale)
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var hd = (WAVEHDR)Marshal.PtrToStructure(pending[i], typeof(WAVEHDR));
                    if ((hd.dwFlags & 1) != 0) { Free(pending[i]); pending.RemoveAt(i); }
                }
                if (pending.Count > 15) return;
                var data = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, data, pcm.Length);
                var hdr = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)pcm.Length }, hdr, false);
                waveOutPrepareHeader(dev, hdr, HdrSize);
                waveOutWrite(dev, hdr, HdrSize);
                pending.Add(hdr);
            }
        }

        void Free(IntPtr hdr)
        {
            waveOutUnprepareHeader(dev, hdr, HdrSize);
            var hd = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
            Marshal.FreeHGlobal(hd.lpData); Marshal.FreeHGlobal(hdr);
        }

        public void Stop()
        {
            if (dev == IntPtr.Zero) return;
            lock (pending)
            {
                waveOutReset(dev);
                foreach (var h in pending) Free(h);
                pending.Clear();
                waveOutClose(dev);
                dev = IntPtr.Zero;
            }
            Log.Write("micro de l'iPad arrêté");
        }
    }

    // ---------- Presse-papiers : Windows signale chaque copie (WM_CLIPBOARDUPDATE) ----------
    public class ClipboardWatcher : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
        public Action<string> TextCopied;
        public Action<byte[]> ImageCopied;
        string lastHash = "";
        public bool Enabled = true;

        public ClipboardWatcher() { CreateHandle(new CreateParams()); AddClipboardFormatListener(Handle); }

        static string Hash(byte[] b) { using (var h = SHA1.Create()) return Convert.ToBase64String(h.ComputeHash(b)); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D && Enabled) Changed();   // WM_CLIPBOARDUPDATE
            base.WndProc(ref m);
        }

        void Changed()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var t = Clipboard.GetText();
                    var h = Hash(Encoding.UTF8.GetBytes(t));
                    if (h == lastHash || t.Length > 500000) return;
                    lastHash = h; if (TextCopied != null) TextCopied(t);
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    using (var ms = new MemoryStream())
                    {
                        if (img == null) return;
                        img.Save(ms, ImageFormat.Png);
                        var b = ms.ToArray(); var h = Hash(b);
                        if (h == lastHash || b.Length > 15 * 1024 * 1024) return;
                        lastHash = h; if (ImageCopied != null) ImageCopied(b);
                    }
                }
            }
            catch (Exception e) { Log.Write("presse-papiers : " + e.Message); }
        }

        // Contenu venant de l'iPad : on le mémorise pour ne pas le renvoyer en écho
        public void SetText(string t)
        {
            lastHash = Hash(Encoding.UTF8.GetBytes(t));
            try { Clipboard.SetText(t); } catch (Exception e) { Log.Write("presse-papiers : " + e.Message); }
        }

        public void SetImage(byte[] png)
        {
            try
            {
                using (var ms = new MemoryStream(png))
                using (var img = Image.FromStream(ms))
                {
                    Clipboard.SetImage(img);
                    using (var o = new MemoryStream()) { Clipboard.GetImage().Save(o, ImageFormat.Png); lastHash = Hash(o.ToArray()); }
                }
            }
            catch (Exception e) { Log.Write("presse-papiers (image) : " + e.Message); }
        }

        public void Dispose() { try { RemoveClipboardFormatListener(Handle); DestroyHandle(); } catch { } }
    }

    // ---------- Photos envoyées par l'iPad : rangées dans Téléchargements\MirrorPad ----------
    public static class Inbox
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MirrorPad");

        // Trame : [longueur du nom sur 2 octets][nom UTF-8][contenu]
        public static string Save(byte[] frame)
        {
            if (frame.Length < 2) return null;
            int n = frame[0] << 8 | frame[1];
            if (frame.Length < 2 + n) return null;
            var name = Encoding.UTF8.GetString(frame, 2, n);
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "photo.jpg";
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, name);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(Dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            using (var f = File.Create(path)) f.Write(frame, 2 + n, frame.Length - 2 - n);
            Log.Write("fichier reçu de l'iPad : " + path);
            return path;
        }
    }
}
