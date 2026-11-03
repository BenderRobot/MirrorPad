// Application de la zone de notification : connexion automatique, état, menu, mises à jour, mode administrateur.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace bSideDesk
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
        readonly System.Windows.Forms.Timer fgTimer = new System.Windows.Forms.Timer { Interval = 400 };   // Touch Bar : application active
        HotkeyWindow hotkey;
        string lastFg = "";
        bool sleepRequested, proxLocked;   // verrouillage par proximité
        ToolStripMenuItem statusItem, versionItem, toggleItem, touchItem, startupItem, adminItem;
        Icon iconIdle, iconWaiting, iconConnected;
        Image baseImage;

        readonly string appDir = Path.GetDirectoryName(Application.ExecutablePath);
        readonly string localVersion;
        readonly string ffmpeg;
        volatile bool wanted;
        string lastRoute = "";
        volatile bool restartRequested;   // ffmpeg arrêté exprès (rotation, qualité) : pas un échec
        DateTime lastWake = DateTime.MinValue;
        bool extendTried, wakeFailLogged;   // « Étendre » tenté pour cette connexion ; échec déjà signalé
        DateTime holdUntil = DateTime.MinValue;   // pas de capture avant cette heure (bureau en cours de réorganisation)
        volatile Session session;
        string state = "";
        DateTime lostAt = DateTime.MinValue;
        DateTime lastAlive = DateTime.Now;
        readonly object displayLock = new object();
        readonly CameraBridge camera = new CameraBridge();
        readonly FocusWatcher focus = new FocusWatcher();
        readonly PcAudio sound = new PcAudio();
        readonly Dashboard dashboard = new Dashboard();
        readonly FileTransfer files = new FileTransfer();
        FileSystemWatcher outboxWatcher;
        CameraViewer viewer;   // fenêtre « Caméras de l'iPad »
        readonly MicPlayer mic = new MicPlayer();
        ClipboardWatcher clip;
        int ticks;
        readonly bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        static string ProgramsLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "bSideDesk.lnk"); } }
        static string StartupLnk { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "bSideDesk.lnk"); } }
        static string UpdatedFlag { get { return Path.Combine(Config.Dir, "updated.txt"); } }

        public App()
        {
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            var vf = Path.Combine(appDir, "version.txt");
            localVersion = File.Exists(vf) ? File.ReadAllText(vf).Trim() : "0.0.0";
            foreach (var old in Directory.GetFiles(appDir, "*.old")) { try { File.Delete(old); } catch { } }
            Log.Write("bSideDesk " + localVersion + " démarré" + (elevated ? " (administrateur)" : ""));

            try { Log.Write("écrans : " + Displays.Describe()); } catch { }
            streamer = new Streamer(cfg);
            input = new InputHandler(cfg);
            input.RefreshBounds();
            input.RestartStream = () => { restartRequested = true; streamer.Stop(); };
            input.Placed = PlaceVirtual;
            // numéro de sortie à capturer : recalculé à chaque lancement de ffmpeg (il change quand on branche un écran)
            streamer.OutputIndex = () => { var d = input.TargetDevice; int i = string.IsNullOrEmpty(d) ? -1 : Displays.CaptureIndex(d); return i >= 0 ? i : cfg.Output; };
            input.AfterDriverRestart = () => { WakeVirtual(); input.RefreshBounds(); PlaceVirtual(); };
            input.HoldStream = ms => { holdUntil = DateTime.Now.AddMilliseconds(ms); if (ms > 0) { restartRequested = true; streamer.Stop(); } };
            input.Sleep = () => { Log.Write("iPad verrouillé"); sleepRequested = true; lastAlive = DateTime.MinValue; var s = session; if (s != null) s.Close(); streamer.Stop(); SleepVirtual(); };
            ffmpeg = Streamer.FindFfmpeg();

            BuildIcons(); BuildMenu();
            camera.Notify = msg => NotifyBoth(msg);
            camera.RunElevated = code => { try { return elevated ? RunPowerShell(code) : RunElevated(code); } catch { return -1; } };
            mic.Notify = msg => NotifyBoth(msg);
            input.Media = HandleMedia;
            clip = new ClipboardWatcher();
            hotkey = new HotkeyWindow { Pressed = SwapWindow };
            files.Received = path => Post(() => Balloon("Fichier reçu de l'iPad : " + Path.GetFileName(path) + " (Téléchargements\\bSideDesk)", ToolTipIcon.Info));
            dashboard.Checks = () => cfg.DashboardChecks;
            try
            {
                Directory.CreateDirectory(Outbox.Dir);
                outboxWatcher = new FileSystemWatcher(Outbox.Dir, "*.txt") { EnableRaisingEvents = true };
                outboxWatcher.Created += (s, e) => ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(300); FlushOutbox(); });
                MakeSendTo();
            }
            catch (Exception e) { Log.Write("envoi vers l'iPad : " + e.Message); }
            fgTimer.Tick += (s, e) => ForegroundTick(); fgTimer.Start();
            clip.TextCopied = t => { var s = session; if (cfg.ShareClipboard && s != null && !s.IsClosed) s.Send("CB " + Uri.EscapeDataString(t)); };
            clip.ImageCopied = b => { var s = session; if (cfg.ShareClipboard && s != null && !s.IsClosed) s.SendFrame(Session.ClipImage, b, 0, b.Length); };
            notify.Icon = iconIdle; notify.Visible = true;
            notify.MouseDoubleClick += (s, e) => Toggle();
            notify.BalloonTipClicked += (s, e) => { if (Directory.Exists(Inbox.Dir)) Process.Start(Inbox.Dir); };
            SetState("idle");
            try { MakeShortcuts(); } catch (Exception e) { Log.Write("raccourcis : " + e.Message); }

            if (Program.Migrated)
            {
                Log.Write("réglages repris de MirrorPad (ancien nom de l'app)");
                CleanupMirrorPad();
                Balloon("MirrorPad s'appelle maintenant bSideDesk. Vos réglages et votre iPad appairé sont conservés.", ToolTipIcon.Info);
                if (Program.ReEnableAdmin) Post(() => SetAdminMode(true));   // tâche planifiée recréée sous le nouveau nom
            }

            if (File.Exists(UpdatedFlag))
            {
                Balloon("Mis à jour en version " + localVersion, ToolTipIcon.Info);
                try { File.Delete(UpdatedFlag); } catch { }
            }
            if (ffmpeg == null)
            {
                MessageBox.Show("ffmpeg est introuvable.\n\nInstallez-le avec : winget install Gyan.FFmpeg", "bSideDesk");
            }
            timer.Tick += (s, e) => Tick();
            timer.Start();
            new Thread(ConnectionLoop) { IsBackground = true, Name = "connexion" }.Start();
            if (cfg.AutoConnect && ffmpeg != null) Connect();
        }

        // ---------- icônes ----------
        void BuildIcons()
        {
            var png = Path.Combine(appDir, "bSideDesk.png");
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
                new ToolStripMenuItem("Voir les caméras de l'iPad…", null, (s, e) => ShowCameras()),
                new ToolStripMenuItem("Envoyer un fichier vers l'iPad…", null, (s, e) => PickFilesToSend()),
                new ToolStripMenuItem("Repérer les écrans…", null, (s, e) => FindScreens()),
                new ToolStripMenuItem("Installer les composants manquants…", null, (s, e) => InstallDrivers()),
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

        void Balloon(string text, ToolTipIcon icon) { notify.ShowBalloonTip(3000, "bSideDesk", text, icon); }
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
            var tip = "bSideDesk – " + statusItem.Text; notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (prev == "connected" && s != "connected") lostAt = DateTime.Now;
            if (s == "connected" && prev != "connected" && (DateTime.Now - lostAt).TotalSeconds > 15)
                Balloon("iPad connecté : " + target, ToolTipIcon.Info);
        }

        void Tick()
        {
            ticks++;
            if (cfg.AutoUpdate && (ticks == 10 || ticks % 120 == 0)) ThreadPool.QueueUserWorkItem(_ => CheckUpdate(false));
            try { input.RefreshBounds(); } catch { }
            // écran branché / débranché pendant la diffusion : la sortie capturée a peut-être changé de numéro
            if (ticks % 2 == 0 && streamer.Running && session != null)
                try
                {
                    int now = streamer.OutputIndex();
                    if (now != streamer.CapturedOutput)
                    {
                        Log.Write("écrans modifiés : capture de la sortie " + now + " au lieu de " + streamer.CapturedOutput);
                        restartRequested = true; streamer.Stop();
                    }
                }
                catch { }
            var s = session;
            if (s != null && !s.IsClosed) { lastAlive = DateTime.Now; proxLocked = false; }
            // verrouillage par proximité : l'iPad a disparu du réseau (emporté) sans s'être mis en veille
            else if (cfg.ProximityLock && wanted && !sleepRequested && !proxLocked && lastAlive != DateTime.MinValue && (DateTime.Now - lastAlive).TotalSeconds > 12)
            { proxLocked = true; Log.Write("iPad hors de portée : PC verrouillé"); LockWorkStation(); }
            // iPad absent depuis 20 s (éteint, hors Wi-Fi…) : on débranche l'écran virtuel
            else if (cfg.SleepWithIPad && !cfg.VirtualDetached && (DateTime.Now - lastAlive).TotalSeconds > 20 && lastAlive != DateTime.MinValue)
            { lastAlive = DateTime.MinValue; ThreadPool.QueueUserWorkItem(_ => SleepVirtual(true)); }
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
                    // câble USB d'abord (si l'iPad est branché et le service Apple présent), sinon Wi-Fi
                    TcpClient usb = cfg.PreferUsb ? Usbmux.Connect(Config.Port, 1500) : null;
                    if (usb != null)
                    {
                        try { s = Session.Over(usb, "USB", cfg); }
                        catch { usb.Close(); s = null; }
                    }
                    if (s == null) s = Session.Connect(cfg.IPad, cfg, 3000);
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
                if (s.Host != lastRoute) { Log.Write("connexion par " + (s.Host == "USB" ? "câble USB" : "Wi-Fi (" + s.Host + ")")); lastRoute = s.Host; }
                session = s;              // la session compte comme « vivante » dès maintenant…
                lastAlive = DateTime.Now; // …pour que la règle des 20 s ne retire pas l'écran pendant qu'on le rebranche
                extendTried = false;
                WakeVirtual();   // l'écran virtuel revient avant l'image
                PlaceVirtual();
                ReturnParked();
                input.Reply = line => s.Send(line);
                s.LineReceived += input.Handle;
                s.BinaryReceived += HandleBinary;
                input.ResetDiagnostics();
                sleepRequested = false; lastFg = "";   // la Touch Bar est renvoyée à la nouvelle session
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2000); FlushOutbox(); });   // fichiers en attente
                if (viewer != null) ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(1500); var v = viewer; if (v != null) v.Request("START front"); });   // fenêtre caméra encore ouverte
                streamer.ResetAdapt();
                streamer.Stats = (pct, mb) => s.Send("Q " + pct + " " + mb);   // saturation et débit, pour le panneau d'infos de l'iPad
                if (cfg.AutoKeyboard) { focus.ScreenBounds = () => input.Bounds; focus.Send = line => s.Send(line); input.Tapped = focus.Tapped; focus.Start(); }
                new Thread(s.ReadLoop) { IsBackground = true, Name = "lecture" }.Start();
                s.Send("N " + Environment.MachineName);
                s.Send("SC " + string.Join(" ", cfg.Shortcuts.Select(x => Uri.EscapeDataString(x.Label + "|" + x.Keys))));

                // diffusion : on relance ffmpeg tant que la session vit (rotation, qualité, encodeur en échec…)
                while (wanted && !s.IsClosed)
                {
                    while (DateTime.Now < holdUntil && !s.IsClosed) Thread.Sleep(40);   // rotation : on laisse Windows finir
                    if (s.IsClosed) break;
                    restartRequested = false;
                    input.RefreshBounds();
                    if (input.VirtualMissing)
                    {
                        // écran virtuel pas (encore) sur le bureau : on ne capture surtout pas un autre écran à sa place
                        if ((DateTime.Now - lastWake).TotalSeconds > 10) { lastWake = DateTime.Now; WakeVirtual(); PlaceVirtual(); ReturnParked(); }
                        Thread.Sleep(500);
                        continue;
                    }
                    streamer.ActiveEncoder = streamer.PickEncoder(ffmpeg, msg => Post(() => Balloon(msg, ToolTipIcon.Info)));
                    s.Send("C " + cfg.Fps + " " + cfg.Mbps + " " + Uri.EscapeDataString(Streamer.EncoderNames[streamer.ActiveEncoder]));
                    var t0 = DateTime.Now;
                    try { streamer.Run(ffmpeg, s); } catch (Exception e) { Log.Write("ffmpeg : " + e.Message); }
                    if ((DateTime.Now - t0).TotalSeconds < 3 && !s.IsClosed && !restartRequested && !streamer.AdaptRestart) { Log.Write("ffmpeg s'est arrêté aussitôt : vérification de l'écran virtuel"); WakeVirtual(); Thread.Sleep(3000); }   // échec immédiat : écran virtuel absent ?
                }
                streamer.Stop(); input.ReleaseAll(); camera.Stop(); mic.Stop(); focus.Stop(); sound.Stop(); dashboard.Stop();
                s.Close();
                session = null;
                Thread.Sleep(1000);
            }
        }

        // ---------- écran virtuel : débranché quand l'iPad dort, rebranché à son retour ----------
        // ---------- caméras de l'iPad sur le PC (webcam sans fil) ----------
        void ShowCameras()
        {
            var s = session;
            if (s == null || s.IsClosed) { Balloon("Connectez d'abord l'iPad (app bSideDesk ouverte).", ToolTipIcon.Warning); return; }
            if (viewer != null && !viewer.IsDisposed) { viewer.Activate(); return; }
            viewer = new CameraViewer(ffmpeg) { SendLine = line => { var cs = session; if (cs != null) cs.Send(line); } };
            viewer.FormClosed += (o, e) => { viewer = null; streamer.SetCap(0); };
            streamer.SetCap(6);   // le Wi-Fi doit aussi porter la caméra dans l'autre sens
            viewer.Show();
            viewer.Request("START front");
        }

        // ---------- fichiers vers l'iPad ----------
        void PickFilesToSend()
        {
            using (var d = new OpenFileDialog { Multiselect = true, Title = "Envoyer vers l'iPad" })
                if (d.ShowDialog() == DialogResult.OK) Outbox.Queue(d.FileNames);
        }

        // demandes déposées par le menu, « Envoyer vers » ou une 2e instance : envoyées dès que l'iPad est connecté
        readonly object outboxLock = new object();
        void FlushOutbox()
        {
            lock (outboxLock)
            {
                var s = session;
                if (s == null || s.IsClosed) return;
                foreach (var req in Directory.GetFiles(Outbox.Dir, "*.txt"))
                {
                    string[] paths;
                    try { paths = File.ReadAllLines(req); File.Delete(req); } catch { continue; }
                    foreach (var path in paths.Where(File.Exists))
                    {
                        try { FileTransfer.Send(s, path); Post(() => Balloon("Envoyé à l'iPad : " + Path.GetFileName(path), ToolTipIcon.Info)); }
                        catch (Exception e) { Log.Write("envoi de " + path + " : " + e.Message); }
                    }
                }
            }
        }

        // raccourci « iPad (bSideDesk) » dans le menu Envoyer vers de l'Explorateur
        // Restes de l'ancien nom : raccourcis et tâche planifiée « MirrorPad »
        void CleanupMirrorPad()
        {
            foreach (var lnk in new[] {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "MirrorPad.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "MirrorPad.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "iPad (MirrorPad).lnk") })
                try { if (File.Exists(lnk)) File.Delete(lnk); } catch { }
            if (elevated)
                try { using (var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/delete /tn MirrorPad /f") { CreateNoWindow = true, UseShellExecute = false })) p.WaitForExit(10000); } catch { }
        }

        void MakeSendTo()
        {
            var lnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "iPad (bSideDesk).lnk");
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic l = sh.CreateShortcut(lnk);
            l.TargetPath = Application.ExecutablePath; l.Arguments = "--send"; l.WorkingDirectory = appDir;
            var ico = Path.Combine(appDir, "bSideDesk.ico");
            l.IconLocation = (File.Exists(ico) ? ico : Application.ExecutablePath) + ",0";
            l.Description = "Envoyer le fichier sur l'iPad";
            l.Save();
        }

        // ---------- Touch Bar contextuelle ----------
        void ForegroundTick()
        {
            var s = session;
            if (s == null || s.IsClosed) return;
            IntPtr h; var fg = TouchBar.ForegroundProcess(out h);
            if (fg == null || fg.Equals("explorer", StringComparison.OrdinalIgnoreCase) && lastFg.Length > 0 && IsShell(h)) return;   // clic sur le bureau : on garde la barre
            if (fg == lastFg) return;
            lastFg = fg;
            s.Send(TouchBar.LineFor(fg));
        }
        static bool IsShell(IntPtr h)
        {
            var c = new StringBuilder(64); GetClassName(h, c, 64);
            return c.ToString() == "Shell_TrayWnd" || c.ToString() == "Progman" || c.ToString() == "WorkerW";
        }

        // Fenêtre active -> écran de l'iPad (agrandie), ou retour (geste 3 doigts vers le haut, Touch Bar, Ctrl+Alt+I)
        void SwapWindow()
        {
            var where = WindowMover.Swap(input.Bounds);
            if (where != null) Log.Write("fenêtre envoyée sur l'" + (where == "iPad" ? "iPad" : where));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool LockWorkStation();
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        // Place l'écran de l'iPad à gauche, à droite ou au-dessus de l'écran principal (réglage « Position »)
        void PlaceVirtual()
        {
            var side = cfg.ScreenSide ?? "";
            if (side == "") return;
            lock (displayLock)
            {
                input.RefreshBounds();
                var dev = input.TargetDevice;
                int x, y, w, h;
                if (string.IsNullOrEmpty(dev) || !Displays.IsVirtual(dev) || !Native.GetDisplay(dev, out x, out y, out w, out h)) return;
                var main = Screen.PrimaryScreen.Bounds;
                // tous les autres écrans (portable + écrans externes) : l'iPad se met à côté de l'ensemble, sans chevaucher
                var others = Screen.AllScreens.Where(sc => !string.Equals(sc.DeviceName, dev, StringComparison.OrdinalIgnoreCase)).Select(sc => sc.Bounds).ToList();
                int minX = others.Count > 0 ? others.Min(b => b.Left) : main.Left, maxX = others.Count > 0 ? others.Max(b => b.Right) : main.Right;
                int minY = others.Count > 0 ? others.Min(b => b.Top) : main.Top;
                int nx, ny;
                switch (side)
                {
                    case "left": nx = minX - w; ny = main.Top + (main.Height - h) / 2; break;
                    case "above": nx = main.Left + (main.Width - w) / 2; ny = minY - h; break;
                    default: nx = maxX; ny = main.Top + (main.Height - h) / 2; break;
                }
                if (nx == x && ny == y) return;
                int r = Native.SetPosition(dev, nx, ny);
                Log.Write(r == 0 ? "écran de l'iPad placé " + (side == "left" ? "à gauche" : side == "above" ? "au-dessus" : "à droite") + " (" + nx + "," + ny + ")"
                                 : "impossible de placer l'écran de l'iPad (code " + r + ")");
                input.RefreshBounds();
            }
        }

        System.Collections.Generic.List<WindowMover.Parked> parked;   // fenêtres rangées sur l'écran principal au débranchement de l'iPad
        void ReturnParked()
        {
            if (parked == null || cfg.VirtualDetached) return;   // écran pas encore revenu : on garde la liste
            var list = parked; parked = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(2500);   // le temps que l'écran de l'iPad soit en place
                try { input.RefreshBounds(); if (string.IsNullOrEmpty(input.TargetDevice)) return; int n = WindowMover.Return(list, input.Bounds); if (n > 0) Log.Write(n + " fenêtre(s) ramenée(s) sur l'iPad"); }
                catch (Exception e) { Log.Write("retour des fenêtres : " + e.Message); }
            });
        }

        void SleepVirtual(bool onlyIfAbsent = false)
        {
            if (!cfg.SleepWithIPad) return;
            lock (displayLock)
            {
                if (cfg.VirtualDetached) return;
                var cur = session;
                if (onlyIfAbsent && cur != null && !cur.IsClosed) return;   // l'iPad est revenu entre-temps : on garde l'écran
                input.RefreshBounds();
                var dev = input.TargetDevice;
                int x, y, w, h;
                if (string.IsNullOrEmpty(dev) || !Displays.IsVirtual(dev) || !Native.GetDisplay(dev, out x, out y, out w, out h)) return;
                // ses fenêtres d'abord : rangées entières sur l'écran principal (et ramenées au retour de l'iPad)
                try
                {
                    var moved = WindowMover.Evacuate(new Rectangle(x, y, w, h));
                    if (moved.Count > 0) { parked = moved; Log.Write(moved.Count + " fenêtre(s) de l'iPad rangée(s) sur l'écran principal"); Thread.Sleep(300); }
                }
                catch (Exception ex) { Log.Write("rangement des fenêtres : " + ex.Message); }
                int r = Native.DetachDisplay(dev);
                if (r != 0) { Log.Write("impossible de retirer l'écran virtuel (code " + r + ")"); return; }
                cfg.VirtualDevice = dev; cfg.VirtualX = x; cfg.VirtualY = y; cfg.VirtualW = w; cfg.VirtualH = h; cfg.VirtualDetached = true;
                cfg.Save();
                Log.Write("écran virtuel retiré (" + dev + ", " + w + "x" + h + " en " + x + "," + y + ")");
                ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(2000); try { int n = WindowMover.Rescue(); if (n > 0) Log.Write(n + " fenêtre(s) hors écran ramenée(s) en vue"); } catch { } });
            }
        }

        // Rebranche l'écran virtuel s'il manque (après un verrouillage de l'iPad ou un débranchement manuel)
        void WakeVirtual()
        {
            lock (displayLock)
            {
                // les numéros \\.\DISPLAYn changent quand on branche un écran externe : on retrouve l'écran virtuel par son pilote
                bool attachedNow; var found = Displays.Virtual(out attachedNow);
                var dev = found ?? cfg.VirtualDevice;
                if (string.IsNullOrEmpty(dev)) return;
                if (found != null && found != cfg.VirtualDevice) { Log.Write("écran virtuel : " + cfg.VirtualDevice + " est devenu " + found); cfg.VirtualDevice = found; cfg.Save(); }
                int x, y, w, h;
                if (Native.GetDisplay(dev, out x, out y, out w, out h)) { if (cfg.VirtualDetached) { cfg.VirtualDetached = false; cfg.Save(); } input.RefreshBounds(); return; }
                // 1. activation du seul écran virtuel (les autres écrans ne bougent pas)
                int r = Displays.Enable(dev);
                Thread.Sleep(800);
                var again = Displays.Virtual(out attachedNow); if (again != null) dev = again;
                bool ok = Native.GetDisplay(dev, out x, out y, out w, out h);
                // 2. ancienne méthode : rebranchement dans une définition proposée par le pilote
                int a = 0;
                if (!ok)
                    foreach (var sz in new[] { new[] { cfg.VirtualW, cfg.VirtualH }, new[] { 2048, 1536 }, new[] { 1536, 2048 } })
                    {
                        if (sz[0] <= 0) continue;
                        a = Native.AttachDisplay(dev, cfg.VirtualX, cfg.VirtualY, sz[0], sz[1]);
                        if (a == 0 && Native.GetDisplay(dev, out x, out y, out w, out h)) { ok = true; break; }
                    }
                // 3. dernier recours, une seule fois par connexion : Windows + P > « Étendre » (fait clignoter tous les écrans)
                int e = 0;
                if (!ok && !extendTried)
                {
                    extendTried = true;
                    e = Native.ExtendAll();
                    Thread.Sleep(1500);
                    again = Displays.Virtual(out attachedNow); if (again != null) dev = again;
                    ok = Native.GetDisplay(dev, out x, out y, out w, out h);
                }
                if (!ok)
                {
                    if (!wakeFailLogged) Log.Write("impossible de rebrancher l'écran virtuel " + dev + " (codes " + r + " / " + a + " / " + e + ") : Paramètres > Affichage > écran 3 > « Étendre le bureau à cet écran »");
                    wakeFailLogged = true;
                    return;
                }
                wakeFailLogged = false;
                cfg.VirtualDevice = dev; cfg.VirtualDetached = false; cfg.Save();
                Log.Write("écran virtuel rebranché (" + dev + ", " + w + "x" + h + ")");
                Thread.Sleep(1500);   // le temps que Windows et la capture voient l'écran
                input.RefreshBounds();
            }
        }

        // Tous les composants Windows (écran virtuel, webcam, micro, câble USB…) s'installent par bSideDesk-Setup.exe :
        // on télécharge celui de la dernière version et on le lance (il ne réinstalle que ce qui manque).
        void InstallDrivers()
        {
            Balloon("Téléchargement de l'installateur bSideDesk…", ToolTipIcon.Info);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var json = HttpGet("https://api.github.com/repos/" + cfg.UpdateRepo + "/releases/latest", false, null);
                    GhRelease rel;
                    using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        rel = (GhRelease)new DataContractJsonSerializer(typeof(GhRelease)).ReadObject(ms);
                    var asset = (rel.assets ?? new GhAsset[0]).FirstOrDefault(a => a.name == "bSideDesk-Setup.exe");
                    if (asset == null) throw new Exception("installateur absent de la version " + rel.tag_name);
                    var exe = Path.Combine(Path.GetTempPath(), "bSideDesk-Setup.exe");
                    if (string.IsNullOrEmpty(cfg.GitHubToken)) HttpGet(asset.browser_download_url, true, exe);
                    else HttpGet(asset.url, true, exe);
                    Log.Write("installateur " + rel.tag_name + " téléchargé, lancement");
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                }
                catch (Exception e)
                {
                    Log.Write("installateur : " + e.Message);
                    Post(() => Balloon("Impossible de lancer l'installateur : " + e.Message, ToolTipIcon.Error));
                }
            });
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
            if (p[0] == "WIN") { Post(SwapWindow); return; }
            if (p[0] == "DB")
            {
                var ds = session;
                if (ds != null && p.Length >= 2 && p[1] == "START") { dashboard.Send = line => ds.Send(line); dashboard.Start(); }
                else dashboard.Stop();
                return;
            }
            if (p[0] == "SND")
            {
                // son du PC vers les haut-parleurs de l'iPad
                var s = session;
                if (s == null) return;
                if (p.Length >= 2 && p[1] == "START")
                {
                    sound.Format = rate => s.Send("SND FMT " + rate + " 2");
                    sound.Packet = pcm => s.SendFrame(Session.PcSound, pcm, 0, pcm.Length);
                    sound.Start();
                }
                else sound.Stop();
                return;
            }
            if (p[0] == "CAM" && viewer != null)
            {
                // caméra demandée par la fenêtre du PC : la webcam OBS ne démarre que si elle est installée (pas d'alerte inutile)
                if (p.Length >= 2 && p[1] == "START") { if (CameraBridge.ObsCameraInstalled()) ThreadPool.QueueUserWorkItem(_ => camera.Start(ffmpeg)); }
                else { camera.Stop(); var v = viewer; if (v != null) Post(v.CameraStopped); }
                return;
            }
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
                case Session.Camera: camera.Feed(data); { var v = viewer; if (v != null) v.Feed(data); } break;
                case Session.Audio: mic.Play(data); break;
                case Session.ClipImage: if (cfg.ShareClipboard) Post(() => clip.SetImage(data)); break;
                case FileTransfer.FrameType:
                    try { files.Receive(data); } catch (Exception e) { Log.Write("réception de fichier : " + e.Message); }
                    break;
                case Session.File:
                    try
                    {
                        var path = Inbox.Save(data);
                        if (path != null) Post(() => Balloon("Photo reçue de l'iPad : " + Path.GetFileName(path) + " (Téléchargements\\bSideDesk)", ToolTipIcon.Info));
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
            using (var f = new SettingsForm(cfg, localVersion, statusItem.Text) { InstallMissing = InstallDrivers })
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
            MessageBox.Show("Une image par écran a été enregistrée (ecran-0.png, ecran-1.png…).\n\nRepérez celle de l'écran virtuel puis indiquez son numéro dans Paramètres.", "bSideDesk");
        }

        // ---------- raccourcis, démarrage, mode administrateur ----------
        void MakeShortcut(string path)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic sh = Activator.CreateInstance(t);
            dynamic l = sh.CreateShortcut(path);
            if (cfg.AdminMode) { l.TargetPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe"); l.Arguments = "/run /tn bSideDesk"; l.WindowStyle = 7; }
            else { l.TargetPath = Application.ExecutablePath; l.Arguments = ""; }
            l.WorkingDirectory = appDir;
            var ico = Path.Combine(appDir, "bSideDesk.ico");
            l.IconLocation = (File.Exists(ico) ? ico : Application.ExecutablePath) + ",0";
            l.Description = "bSideDesk : iPad en écran supplémentaire";
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
                  "Register-ScheduledTask -TaskName 'bSideDesk' -Action $a -Trigger $t -Principal $p -Settings $s -Force | Out-Null"
                : "Unregister-ScheduledTask -TaskName 'bSideDesk' -Confirm:$false -ErrorAction SilentlyContinue";
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
                wc.Headers["User-Agent"] = "bSideDesk";
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
                var asset = (rel.assets ?? new GhAsset[0]).FirstOrDefault(a => a.name == "bSideDesk-Windows.zip");
                if (asset == null) return;
                var tmp = Path.Combine(Path.GetTempPath(), "bsidedesk-update");
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                var zip = Path.Combine(tmp, "update.zip");
                if (string.IsNullOrEmpty(cfg.GitHubToken)) HttpGet(asset.browser_download_url, true, zip);
                else HttpGet(asset.url, true, zip);
                var x = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zip, x);
                if (!File.Exists(Path.Combine(x, "bSideDesk.exe"))) throw new Exception("archive incomplète");
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
            if (cfg.AdminMode) Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/run /tn bSideDesk") { CreateNoWindow = true, UseShellExecute = false });
            else Process.Start(Path.Combine(appDir, "bSideDesk.exe"));
            ExitThread();
        }

        void Quit() { Shutdown(); ExitThread(); }
    }
}
