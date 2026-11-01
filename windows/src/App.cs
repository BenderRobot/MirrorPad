// Application de la zone de notification : connexion automatique, état, menu, mises à jour, mode administrateur.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MirrorPad
{
    [DataContract] class GhAsset { [DataMember] public string name; [DataMember] public string url; [DataMember] public string browser_download_url; }
    [DataContract] class GhRelease { [DataMember] public string tag_name; [DataMember] public GhAsset[] assets; }

    public class App : ApplicationContext
    {
        readonly Config cfg = Config.Load();
        readonly Streamer streamer;
        readonly InputHandler input;
        readonly SynchronizationContext ui;
        readonly NotifyIcon notify = new NotifyIcon();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        ToolStripMenuItem statusItem, versionItem, toggleItem, touchItem, startupItem, adminItem;
        Icon iconIdle, iconWaiting, iconConnected;
        Image baseImage;

        readonly string appDir = Path.GetDirectoryName(Application.ExecutablePath);
        readonly string localVersion;
        readonly string ffmpeg;
        volatile bool wanted;
        volatile Session session;
        string state = "";
        DateTime lostAt = DateTime.MinValue;
        DateTime lastAlive = DateTime.Now;
        readonly object displayLock = new object();
        readonly CameraBridge camera = new CameraBridge();
        readonly MicPlayer mic = new MicPlayer();
        ClipboardWatcher clip;
        int ticks;
        readonly bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        static string ProgramsLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MirrorPad.lnk"); } }
        static string StartupLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MirrorPad.lnk"); } }
        static string UpdatedFlag { get { return Path.Combine(Config.Dir, "updated.txt"); } }

        public App()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var vf = Path.Combine(appDir, "version.txt");
            localVersion = File.Exists(vf) ? File.ReadAllText(vf).Trim() : "0.0.0";
            foreach (var old in Directory.GetFiles(appDir, "*.old")) { try { File.Delete(old); } catch { } }
            Log.Write("MirrorPad " + localVersion + " démarré" + (elevated ? " (administrateur)" : ""));

            streamer = new Streamer(cfg);
            input = new InputHandler(cfg);
            input.RefreshBounds();
            input.RestartStream = () => streamer.Stop();
            input.Sleep = () => { Log.Write("iPad verrouillé"); var s = session; if (s != null) s.Close(); streamer.Stop(); SleepVirtual(); };
            ffmpeg = Streamer.FindFfmpeg();

            BuildIcons(); BuildMenu();
            camera.Notify = msg => NotifyBoth(msg);
            camera.RunElevated = code => { try { return elevated ? RunPowerShell(code) : RunElevated(code); } catch { return -1; } };
            mic.Notify = msg => NotifyBoth(msg);
            input.Media = HandleMedia;
            clip = new ClipboardWatcher();
            clip.TextCopied = t => { var s = session; if (cfg.ShareClipboard && s != null && !s.IsClosed) s.Send("CB " + Uri.EscapeDataString(t)); };
            clip.ImageCopied = b => { var s = session; if (cfg.ShareClipboard && s != null && !s.IsClosed) s.SendFrame(Session.ClipImage, b, 0, b.Length); };
            notify.Icon = iconIdle; notify.Visible = true;
            notify.MouseDoubleClick += (s, e) => Toggle();
            notify.BalloonTipClicked += (s, e) => { if (Directory.Exists(Inbox.Dir)) Process.Start(Inbox.Dir); };
            SetState("idle");
            try { MakeShortcuts(); } catch (Exception e) { Log.Write("raccourcis : " + e.Message); }

            if (File.Exists(UpdatedFlag))
            {
                Balloon("Mis à jour en version " + localVersion, ToolTipIcon.Info);
                try { File.Delete(UpdatedFlag); } catch { }
            }
            if (ffmpeg == null)
            {
                MessageBox.Show("ffmpeg est introuvable.\n\nInstallez-le avec : winget install Gyan.FFmpeg", "MirrorPad");
            }
            timer.Tick += (s, e) => Tick();
            timer.Start();
            new Thread(ConnectionLoop) { IsBackground = true, Name = "connexion" }.Start();
            if (cfg.AutoConnect && ffmpeg != null) Connect();
        }

        // ---------- icônes ----------
        void BuildIcons()
        {
            var png = Path.Combine(appDir, "MirrorPad.png");
            if (File.Exists(png)) baseImage = Image.FromStream(new MemoryStream(File.ReadAllBytes(png)));
            iconIdle = MakeIcon(Color.FromArgb(120, 120, 120));
            iconWaiting = MakeIcon(Color.FromArgb(230, 150, 30));
            iconConnected = MakeIcon(Color.FromArgb(40, 170, 90));
        }

        Icon MakeIcon(Color dot)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (baseImage != null) g.DrawImage(baseImage, 0, 0, 32, 32);
                else g.FillRectangle(new SolidBrush(Color.FromArgb(59, 91, 255)), 3, 6, 26, 20);
                g.FillEllipse(Brushes.White, 18, 18, 14, 14);
                using (var b = new SolidBrush(dot)) g.FillEllipse(b, 20, 20, 10, 10);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        // ---------- menu ----------
        void BuildMenu()
        {
            var m = new ContextMenuStrip();
            statusItem = new ToolStripMenuItem("Arrêté") { Enabled = false };
            versionItem = new ToolStripMenuItem("Version " + localVersion + (elevated ? " · administrateur" : "")) { Enabled = false };
            toggleItem = new ToolStripMenuItem("Connecter l'iPad", null, (s, e) => Toggle());
            toggleItem.Font = new Font(toggleItem.Font, FontStyle.Bold);
            touchItem = new ToolStripMenuItem("Tactile (le doigt pilote la souris)", null, (s, e) => { cfg.Touch = !cfg.Touch; touchItem.Checked = cfg.Touch; cfg.Save(); }) { Checked = cfg.Touch };
            startupItem = new ToolStripMenuItem("Lancer au démarrage de Windows", null, (s, e) => ToggleStartup());
            adminItem = new ToolStripMenuItem("Mode administrateur (saisie dans les fenêtres admin)", null, (s, e) => SetAdminMode(!cfg.AdminMode)) { Checked = cfg.AdminMode };
            m.Items.AddRange(new ToolStripItem[] {
                statusItem, versionItem, new ToolStripSeparator(), toggleItem,
                new ToolStripMenuItem("Repérer les écrans…", null, (s, e) => FindScreens()),
                new ToolStripMenuItem("Paramètres…", null, (s, e) => ShowSettings()),
                touchItem, startupItem, adminItem,
                new ToolStripMenuItem("Rechercher une mise à jour", null, (s, e) => ThreadPool.QueueUserWorkItem(_ => CheckUpdate(true))),
                new ToolStripMenuItem("Ouvrir le journal", null, (s, e) => { try { Process.Start(Log.FilePath); } catch { } }),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Quitter", null, (s, e) => Quit()) });
            notify.ContextMenuStrip = m;
            RefreshStartupItem();
        }

        void RefreshStartupItem()
        {
            if (cfg.AdminMode) { startupItem.Checked = true; startupItem.Enabled = false; startupItem.Text = "Lancé au démarrage (mode administrateur)"; }
            else startupItem.Checked = File.Exists(StartupLnk);
        }

        void Balloon(string text, ToolTipIcon icon) { notify.ShowBalloonTip(3000, "MirrorPad", text, icon); }
        void Post(Action a) { ui.Post(_ => a(), null); }

        // ---------- état ----------
        void SetState(string s)
        {
            if (state == s) return;
            var prev = state; state = s;
            string target = session != null ? (session.IPadName ?? session.Host) : cfg.IPad;
            string enc = Streamer.EncoderNames.ContainsKey(streamer.ActiveEncoder ?? "") ? Streamer.EncoderNames[streamer.ActiveEncoder] : "";
            switch (s)
            {
                case "idle": notify.Icon = iconIdle; statusItem.Text = "Arrêté"; toggleItem.Text = "Connecter l'iPad"; break;
                case "waiting": notify.Icon = iconWaiting; statusItem.Text = "Recherche de l'iPad…"; toggleItem.Text = "Déconnecter"; break;
                case "connected": notify.Icon = iconConnected; statusItem.Text = "Connecté : " + target + (enc != "" ? " · " + enc : "") + " · chiffré"; toggleItem.Text = "Déconnecter"; break;
            }
            var tip = "MirrorPad – " + statusItem.Text; notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (prev == "connected" && s != "connected") lostAt = DateTime.Now;
            if (s == "connected" && prev != "connected" && (DateTime.Now - lostAt).TotalSeconds > 15)
                Balloon("iPad connecté : " + target, ToolTipIcon.Info);
        }

        void Tick()
        {
            ticks++;
            if (cfg.AutoUpdate && (ticks == 10 || ticks % 120 == 0)) ThreadPool.QueueUserWorkItem(_ => CheckUpdate(false));
            try { input.RefreshBounds(); } catch { }
            var s = session;
            if (s != null && !s.IsClosed) lastAlive = DateTime.Now;
            // iPad absent depuis 20 s (éteint, hors Wi-Fi…) : on débranche l'écran virtuel
            else if (cfg.SleepWithIPad && !cfg.VirtualDetached && (DateTime.Now - lastAlive).TotalSeconds > 20 && lastAlive != DateTime.MinValue)
            { lastAlive = DateTime.MinValue; ThreadPool.QueueUserWorkItem(_ => SleepVirtual()); }
            if (!wanted) { SetState("idle"); return; }
            SetState(s != null && !s.IsClosed && streamer.Running && (DateTime.Now - streamer.StartedAt).TotalSeconds >= 2 ? "connected" : "waiting");
        }

        void Connect() { wanted = true; SetState("waiting"); }
        void Disconnect()
        {
            wanted = false;
            var s = session; if (s != null) s.Close();
            streamer.Stop(); input.ReleaseAll();
            SetState("idle");
        }
        void Toggle() { if (wanted) Disconnect(); else Connect(); }

        // ---------- connexion (fil dédié) ----------
        void ConnectionLoop()
        {
            int fails = 0;
            while (true)
            {
                if (!wanted || ffmpeg == null) { Thread.Sleep(500); continue; }
                if (cfg.AutoDiscover && (string.IsNullOrEmpty(cfg.IPad) || fails >= 2))
                {
                    var ip = Discovery.FindIPad(1500);
                    if (ip != null && ip != cfg.IPad) { Log.Write("iPad trouvé : " + ip); cfg.IPad = ip; cfg.Save(); }
                    fails = 0;
                    if (string.IsNullOrEmpty(cfg.IPad)) { Thread.Sleep(3000); continue; }
                }
                if (string.IsNullOrEmpty(cfg.IPad)) { Thread.Sleep(2000); continue; }

                Session s = null;
                try
                {
                    s = Session.Connect(cfg.IPad, cfg, 3000);
                    s.AskPairing = AskPairing;
                    if (!s.Handshake()) { s.Close(); Thread.Sleep(5000); continue; }
                }
                catch (Exception e)
                {
                    if (s != null) s.Close();
                    fails++;
                    if (fails == 1) Log.Write("connexion impossible à " + cfg.IPad + " : " + e.Message);
                    Thread.Sleep(3000);
                    continue;
                }
                fails = 0;
                WakeVirtual();   // l'écran virtuel revient avant l'image
                session = s;
                input.Reply = line => s.Send(line);
                s.LineReceived += input.Handle;
                s.BinaryReceived += HandleBinary;
                new Thread(s.ReadLoop) { IsBackground = true, Name = "lecture" }.Start();
                s.Send("N " + Environment.MachineName);
                s.Send("SC " + string.Join(" ", cfg.Shortcuts.Select(x => Uri.EscapeDataString(x.Label + "|" + x.Keys))));

                // diffusion : on relance ffmpeg tant que la session vit (rotation, qualité, encodeur en échec…)
                while (wanted && !s.IsClosed)
                {
                    streamer.ActiveEncoder = streamer.PickEncoder(ffmpeg, msg => Post(() => Balloon(msg, ToolTipIcon.Info)));
                    s.Send("C " + cfg.Fps + " " + cfg.Mbps + " " + Uri.EscapeDataString(Streamer.EncoderNames[streamer.ActiveEncoder]));
                    var t0 = DateTime.Now;
                    try { streamer.Run(ffmpeg, s); } catch (Exception e) { Log.Write("ffmpeg : " + e.Message); }
                    if ((DateTime.Now - t0).TotalSeconds < 3 && !s.IsClosed) Thread.Sleep(3000);   // échec immédiat : on patiente
                }
                streamer.Stop(); input.ReleaseAll(); camera.Stop(); mic.Stop();
                s.Close();
                session = null;
                Thread.Sleep(1000);
            }
        }

        // ---------- écran virtuel : débranché quand l'iPad dort, rebranché à son retour ----------
        void SleepVirtual()
        {
            if (!cfg.SleepWithIPad) return;
            lock (displayLock)
            {
                if (cfg.VirtualDetached) return;
                input.RefreshBounds();
                var dev = input.TargetDevice;
                int x, y, w, h;
                if (string.IsNullOrEmpty(dev) || !Native.GetDisplay(dev, out x, out y, out w, out h)) return;
                int r = Native.DetachDisplay(dev);
                if (r != 0) { Log.Write("impossible de retirer l'écran virtuel (code " + r + ")"); return; }
                cfg.VirtualDevice = dev; cfg.VirtualX = x; cfg.VirtualY = y; cfg.VirtualW = w; cfg.VirtualH = h; cfg.VirtualDetached = true;
                cfg.Save();
                Log.Write("écran virtuel retiré (" + dev + ", " + w + "x" + h + " en " + x + "," + y + ")");
            }
        }

        void WakeVirtual()
        {
            lock (displayLock)
            {
                if (!cfg.VirtualDetached || string.IsNullOrEmpty(cfg.VirtualDevice)) return;
                int r = Native.AttachDisplay(cfg.VirtualDevice, cfg.VirtualX, cfg.VirtualY, cfg.VirtualW, cfg.VirtualH);
                cfg.VirtualDetached = false; cfg.Save();
                if (r != 0) { Log.Write("impossible de rebrancher l'écran virtuel (code " + r + ")"); return; }
                Log.Write("écran virtuel rebranché (" + cfg.VirtualW + "x" + cfg.VirtualH + ")");
                Thread.Sleep(1500);   // le temps que Windows et la capture voient l'écran
                input.RefreshBounds();
            }
        }

        // ---------- capteurs de l'iPad ----------
        void NotifyBoth(string msg)
        {
            Log.Write(msg);
            Post(() => Balloon(msg, ToolTipIcon.Warning));
            var s = session; if (s != null) s.Send("MSG " + Uri.EscapeDataString(msg));
        }

        void HandleMedia(string[] p)
        {
            if (p[0] == "CAM") { if (p.Length >= 2 && p[1] == "START") ThreadPool.QueueUserWorkItem(_ => camera.Start(ffmpeg)); else camera.Stop(); }
            else if (p[0] == "MIC")
            {
                int rate = 48000, ch = 1;
                if (p.Length >= 2 && p[1] == "START") { if (p.Length >= 4) { int.TryParse(p[2], out rate); int.TryParse(p[3], out ch); } mic.Start(rate, ch); }
                else mic.Stop();
            }
            else if (p[0] == "CB" && p.Length >= 2 && cfg.ShareClipboard)
            {
                var t = Uri.UnescapeDataString(p[1]);
                Post(() => clip.SetText(t));
            }
        }

        void HandleBinary(byte type, byte[] data)
        {
            switch (type)
            {
                case Session.Camera: camera.Feed(data); break;
                case Session.Audio: mic.Play(data); break;
                case Session.ClipImage: if (cfg.ShareClipboard) Post(() => clip.SetImage(data)); break;
                case Session.File:
                    try
                    {
                        var path = Inbox.Save(data);
                        if (path != null) Post(() => Balloon("Photo reçue de l'iPad : " + Path.GetFileName(path) + " (Téléchargements\\MirrorPad)", ToolTipIcon.Info));
                    }
                    catch (Exception e) { Log.Write("réception de fichier : " + e.Message); }
                    break;
            }
        }

        bool AskPairing(string name, string code)
        {
            bool ok = false;
            ui.Send(_ => { using (var f = new PairingForm(name, code)) ok = f.ShowDialog() == DialogResult.OK; }, null);
            return ok;
        }

        // ---------- fenêtres ----------
        void ShowSettings()
        {
            using (var f = new SettingsForm(cfg))
            {
                if (f.ShowDialog() == DialogResult.OK && f.Apply())
                {
                    input.RefreshBounds();
                    var s = session; if (s != null) s.Close();   // reconnexion avec les nouveaux réglages
                }
            }
        }

        void FindScreens()
        {
            if (ffmpeg == null) return;
            var dir = Path.Combine(Config.Dir, "ecrans");
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
            for (int i = 0; i < 6; i++)
            {
                var psi = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel quiet -y -f lavfi -i ddagrab=output_idx=" + i +
                    ":framerate=5 -frames:v 1 -vf hwdownload,format=bgra \"" + Path.Combine(dir, "ecran-" + i + ".png") + "\"") { UseShellExecute = false, CreateNoWindow = true };
                try { using (var p = Process.Start(psi)) p.WaitForExit(10000); } catch { }
            }
            Process.Start(dir);
            MessageBox.Show("Une image par écran a été enregistrée (ecran-0.png, ecran-1.png…).\n\nRepérez celle de l'écran virtuel puis indiquez son numéro dans Paramètres.", "MirrorPad");
        }

        // ---------- raccourcis, démarrage, mode administrateur ----------
        void MakeShortcut(string path)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic l = sh.CreateShortcut(path);
            if (cfg.AdminMode) { l.TargetPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe"); l.Arguments = "/run /tn MirrorPad"; l.WindowStyle = 7; }
            else { l.TargetPath = Application.ExecutablePath; l.Arguments = ""; }
            l.WorkingDirectory = appDir;
            var ico = Path.Combine(appDir, "MirrorPad.ico");
            l.IconLocation = (File.Exists(ico) ? ico : Application.ExecutablePath) + ",0";
            l.Description = "MirrorPad : iPad en écran supplémentaire";
            l.Save();
        }

        void MakeShortcuts()
        {
            MakeShortcut(ProgramsLnk);
            if (File.Exists(StartupLnk)) { if (cfg.AdminMode) File.Delete(StartupLnk); else MakeShortcut(StartupLnk); }
        }

        void ToggleStartup()
        {
            if (File.Exists(StartupLnk)) File.Delete(StartupLnk); else MakeShortcut(StartupLnk);
            RefreshStartupItem();
        }

        static int RunPowerShell(string psCode)
        {
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCode));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + enc) { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        static int RunElevated(string psCode)
        {
            var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(psCode));
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + enc) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; }
        }

        void SetAdminMode(bool on)
        {
            var user = WindowsIdentity.GetCurrent().Name;
            string code = on
                ? "$a = New-ScheduledTaskAction -Execute '" + Application.ExecutablePath + "'\n" +
                  "$t = New-ScheduledTaskTrigger -AtLogOn -User '" + user + "'\n" +
                  "$p = New-ScheduledTaskPrincipal -UserId '" + user + "' -LogonType Interactive -RunLevel Highest\n" +
                  "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew\n" +
                  "Register-ScheduledTask -TaskName 'MirrorPad' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"
                : "Unregister-ScheduledTask -TaskName 'MirrorPad' -Confirm:$false -ErrorAction SilentlyContinue";
            int rc;
            try { rc = RunElevated(code); } catch { return; }   // fenêtre UAC refusée
            if (rc != 0) { Balloon("Impossible de modifier la tâche planifiée (code " + rc + ").", ToolTipIcon.Error); return; }
            cfg.AdminMode = on; cfg.Save();
            try { MakeShortcuts(); } catch { }
            Restart();
        }

        // ---------- mises à jour (releases GitHub) ----------
        string HttpGet(string url, bool binary, string file)
        {
            using (var wc = new WebClient())
            {
                wc.Headers["User-Agent"] = "MirrorPad";
                if (!string.IsNullOrEmpty(cfg.GitHubToken)) wc.Headers["Authorization"] = "Bearer " + cfg.GitHubToken;
                wc.Headers["Accept"] = binary ? "application/octet-stream" : "application/vnd.github+json";
                if (file != null) { wc.DownloadFile(url, file); return null; }
                return Encoding.UTF8.GetString(wc.DownloadData(url));
            }
        }

        void CheckUpdate(bool manual)
        {
            try
            {
                var json = HttpGet("https://api.github.com/repos/" + cfg.UpdateRepo + "/releases/latest", false, null);
                GhRelease rel;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    rel = (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(ms);
                var remote = rel.tag_name.TrimStart('v');
                if (new Version(remote) <= new Version(localVersion))
                {
                    if (manual) Post(() => Balloon("Déjà à jour (version " + localVersion + ").", ToolTipIcon.Info));
                    return;
                }
                var asset = (rel.assets ?? new GhAsset[0]).FirstOrDefault(a => a.name == "MirrorPad-Windows.zip");
                if (asset == null) return;
                var tmp = Path.Combine(Path.GetTempPath(), "mirrorpad-update");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                var zip = Path.Combine(tmp, "update.zip");
                if (string.IsNullOrEmpty(cfg.GitHubToken)) HttpGet(asset.browser_download_url, true, zip);
                else HttpGet(asset.url, true, zip);
                var x = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zip, x);
                if (!File.Exists(Path.Combine(x, "MirrorPad.exe"))) throw new Exception("archive incomplète");
                Post(() => ApplyUpdate(x, remote));
            }
            catch (Exception e)
            {
                if (manual) Post(() => Balloon("Mise à jour impossible : " + e.Message, ToolTipIcon.Error));
            }
        }

        void ApplyUpdate(string dir, string version)
        {
            try
            {
                var exe = Application.ExecutablePath;
                var old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);   // Windows autorise à renommer un programme en cours d'exécution
                foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(appDir, Path.GetFileName(f)), true);
                File.WriteAllText(UpdatedFlag, version);
                Log.Write("mise à jour installée : " + version);
                Restart();
            }
            catch (Exception e) { Log.Write("mise à jour impossible : " + e.Message); }
        }

        // ---------- arrêt / relance ----------
        void Shutdown()
        {
            timer.Stop(); wanted = false;
            var s = session; if (s != null) s.Close();
            streamer.Stop(); input.ReleaseAll(); camera.Stop(); mic.Stop();
            if (clip != null) clip.Dispose();
            notify.Visible = false; notify.Dispose();
            Program.ReleaseMutex();
        }

        void Restart()
        {
            Shutdown();
            if (cfg.AdminMode) Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn MirrorPad") { CreateNoWindow = true, UseShellExecute = false });
            else Process.Start(Path.Combine(appDir, "MirrorPad.exe"));
            ExitThread();
        }

        void Quit() { Shutdown(); ExitThread(); }
    }
}
