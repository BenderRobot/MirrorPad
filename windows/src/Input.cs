// Rejoue sur Windows les gestes et la frappe reçus de l'iPad.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace bSideDesk
{
    public class InputHandler
    {
        readonly Config cfg;
        public volatile string TargetDevice = "";   // \\.\DISPLAYn de l'écran virtuel ("" = écran principal, jamais pivoté)
        Rectangle bounds = Screen.PrimaryScreen.Bounds;
        bool leftDown;
        int rotSeq;                                 // numéro de la dernière rotation (rotations rapprochées)
        public Action<string> Reply;                // envoi d'une ligne à l'iPad
        public Action RestartStream;                // relancer ffmpeg (rotation, qualité)
        public Action Placed;
        public Action<int, int> Tapped;             // appui / clic de l'utilisateur (détection des zones de saisie)                       // après une rotation : replacer l'écran (gauche / droite / dessus)
        public Action<int> HoldStream;              // couper ffmpeg et attendre n ms avant de le relancer (rotation)
        public Action Sleep;                        // l'iPad se verrouille
        public Action<bool> Standby;                // l'iPad affiche un autre PC (true) ou revient sur celui-ci (false)
        public Action<string[]> Media;              // CAM / MIC / CB : traités par l'application

        public InputHandler(Config c) { cfg = c; }

        int ToX(double nx) { var b = Bounds; return (int)(b.Left + nx * (b.Width - 1)); }
        int ToY(double ny) { var b = Bounds; return (int)(b.Top + ny * (b.Height - 1)); }
        static bool Num(string s, out double v) { return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        // ---- Tactile Windows natif : « F id:x:y:état … » (d = posé, m = déplacé, u = levé, c = annulé), tous les doigts à chaque trame
        void TouchFrame(string[] p)
        {
            if (emuTouch) { EmulateTouch(p); return; }
            var list = new System.Collections.Generic.List<Native.POINTER_TYPE_INFO>();
            bool primaryDone = false;
            for (int i = 1; i < p.Length; i++)
            {
                var f = p[i].Split(':'); uint id; double nx, ny;
                if (f.Length < 4 || !uint.TryParse(f[0], out id) || !Num(f[1], out nx) || !Num(f[2], out ny)) continue;
                int x = ToX(nx), y = ToY(ny), flags;
                switch (f[3])
                {
                    case "d": flags = Native.PF_INRANGE | Native.PF_INCONTACT | Native.PF_DOWN; break;
                    case "u": flags = Native.PF_UP; break;
                    case "c": flags = Native.PF_UP | Native.PF_CANCELED; break;
                    default: flags = Native.PF_INRANGE | Native.PF_INCONTACT | Native.PF_UPDATE; break;
                }
                if (!primaryDone) { flags |= Native.PF_PRIMARY; primaryDone = true; }
                var t = new Native.POINTER_TYPE_INFO { type = Native.PT_TOUCH };
                t.touch.pointerInfo.pointerType = Native.PT_TOUCH;
                t.touch.pointerInfo.pointerId = id;
                t.touch.pointerInfo.pointerFlags = flags | Native.PF_CONFIDENCE;
                t.touch.pointerInfo.ptPixelLocation = new Native.POINT { x = x, y = y };
                t.touch.touchMask = 1;   // zone de contact renseignée
                t.touch.rcContact = new Native.RECT { left = x - 4, top = y - 4, right = x + 4, bottom = y + 4 };
                list.Add(t);
            }
            if (list.Count == 0) return;
            bool ok = Native.InjectTouch(list.ToArray());
            var last = list[0].touch.pointerInfo;
            if (list.Count == 1 && (last.pointerFlags & Native.PF_UP) != 0 && Tapped != null) Tapped(last.ptPixelLocation.x, last.ptPixelLocation.y);
            if (ok && list.Count == 1 && (last.pointerFlags & Native.PF_UP) != 0) CheckNative(false, last.ptPixelLocation.x, last.ptPixelLocation.y);
            if (!touchSeen) { touchSeen = true; Log.Write("tactile natif : premier geste reçu (" + list.Count + " doigt(s) en " + list[0].touch.pointerInfo.ptPixelLocation.x + "," + list[0].touch.pointerInfo.ptPixelLocation.y + ", écran " + Bounds + ") → " + (ok ? "injecté" : "refusé, erreur " + Native.PointerError) + ", fenêtre « " + Native.ForegroundTitle() + " »"); }
            else if (!ok && !touchWarned) { touchWarned = true; Log.Write("tactile natif refusé (erreur " + Native.PointerError + "), fenêtre « " + Native.ForegroundTitle() + " »"); }
        }
        bool touchWarned, penWarned, touchSeen, penSeen, penDown;

        // Certains PC acceptent le tactile / stylet simulé sans rien en faire (aucune erreur, aucun effet).
        // Indice fiable : un appui tactile réel déplace le curseur de la souris à l'endroit touché.
        // Si le curseur n'a pas bougé après le premier appui, on passe en émulation souris pour toute la suite.
        volatile bool emuTouch, emuPen; bool checkedTouch, checkedPen;
        void CheckNative(bool pen, int x, int y)
        {
            if (pen ? checkedPen : checkedTouch) return;
            if (pen) checkedPen = true; else checkedTouch = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                System.Threading.Thread.Sleep(120);
                Native.POINT c;
                if (!Native.GetCursorPos(out c)) return;
                bool works = Math.Abs(c.x - x) <= 3 && Math.Abs(c.y - y) <= 3;
                Log.Write((pen ? "stylet" : "tactile") + " natif " + (works ? "actif" : "sans effet sur ce PC (curseur resté en " + c.x + "," + c.y + ") : émulation souris activée"));
                if (!works) { if (pen) emuPen = true; else emuTouch = true; }
            });
        }

        // Émulation souris du tactile : 1 doigt = clic / glisser (appui long = clic droit),
        // 2 doigts = défilement ou zoom (pincer), comme un pavé tactile
        uint? emuPrimary; double emuLastCy = double.NaN, emuLastSpread = double.NaN; int emuWheelAcc, emuZoomAcc;
        int emuDownX, emuDownY, emuLastX, emuLastY; DateTime emuDownAt; bool emuDragging, emuLongDone, emuTwo;
        System.Threading.Timer emuLongTimer;
        void EmulateTouch(string[] p)
        {
            var pts = new System.Collections.Generic.List<Tuple<uint, int, int, string>>();
            for (int i = 1; i < p.Length; i++)
            {
                var f = p[i].Split(':'); uint id; double nx, ny;
                if (f.Length < 4 || !uint.TryParse(f[0], out id) || !Num(f[1], out nx) || !Num(f[2], out ny)) continue;
                pts.Add(Tuple.Create(id, ToX(nx), ToY(ny), f[3]));
            }
            var active = pts.Where(t => t.Item4 == "d" || t.Item4 == "m").ToList();
            if (active.Count >= 2)
            {
                CancelLong();
                if (leftDown) ReleaseLeft();
                emuPrimary = null; emuTwo = true;
                double cy = (active[0].Item3 + active[1].Item3) / 2.0;
                double spread = Math.Sqrt(Math.Pow(active[0].Item2 - active[1].Item2, 2) + Math.Pow(active[0].Item3 - active[1].Item3, 2));
                if (!double.IsNaN(emuLastCy))
                {
                    double dSpread = spread - emuLastSpread, dy = cy - emuLastCy;
                    if (Math.Abs(dSpread) > Math.Abs(dy) * 1.5)
                    {
                        // pincer : Ctrl + molette (un cran tous les 60 px d'écartement)
                        emuZoomAcc += (int)Math.Round(dSpread);
                        int z = emuZoomAcc / 60;
                        if (z != 0) { Native.Zoom(z); emuZoomAcc -= z * 60; }
                    }
                    else
                    {
                        // défilement fin : petits crans à chaque mouvement (les navigateurs les lissent)
                        emuScroll += dy * ScrollGain;
                        int units = (int)emuScroll;
                        if (units != 0) { Native.mouse_event(Native.Wheel, 0, 0, units, UIntPtr.Zero); emuScroll -= units; }
                        var now = DateTime.Now; double dt = (now - emuLastMove).TotalSeconds;
                        if (dt > 0 && dt < 0.1) emuVelocity = emuVelocity * 0.6 + (dy * ScrollGain / dt) * 0.4;   // unités molette / s
                        emuLastMove = now;
                    }
                }
                emuLastCy = cy; emuLastSpread = spread;
                return;
            }
            emuLastCy = double.NaN; emuLastSpread = double.NaN; emuWheelAcc = 0; emuZoomAcc = 0;
            if (active.Count == 0 && emuTwo)
            {
                // doigts levés en plein mouvement : le défilement continue et ralentit (inertie)
                if (Math.Abs(emuVelocity) > 400 && (DateTime.Now - emuLastMove).TotalSeconds < 0.08) StartInertia(Math.Max(-2500, Math.Min(2500, emuVelocity)));
                emuVelocity = 0;
            }
            if (active.Count > 0) StopInertia();
            if (active.Count == 0) emuTwo = false;
            if (emuTwo) return;   // on attend que tous les doigts soient levés après un geste à deux doigts
            foreach (var t in pts)
            {
                if (emuPrimary == null && t.Item4 == "d")
                {
                    // le bouton n'est enfoncé qu'au premier déplacement : un appui long peut encore devenir un clic droit
                    emuPrimary = t.Item1; emuDownX = emuLastX = t.Item2; emuDownY = emuLastY = t.Item3; emuDownAt = DateTime.Now;
                    emuDragging = false; emuLongDone = false;
                    Native.MoveTo(t.Item2, t.Item3);
                    CancelLong();
                    emuLongTimer = new System.Threading.Timer(_ => LongPress(), null, 550, System.Threading.Timeout.Infinite);
                }
                else if (emuPrimary == t.Item1)
                {
                    emuLastX = t.Item2; emuLastY = t.Item3;
                    bool far = Math.Abs(t.Item2 - emuDownX) > 10 || Math.Abs(t.Item3 - emuDownY) > 10;
                    if (!emuDragging && !emuLongDone && far)
                    {
                        CancelLong(); emuDragging = true;
                        Native.MoveTo(emuDownX, emuDownY);
                        Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); leftDown = true;
                    }
                    if (emuDragging) Native.MoveTo(t.Item2, t.Item3);
                    if (t.Item4 == "u" || t.Item4 == "c")
                    {
                        CancelLong();
                        if (emuDragging) ReleaseLeft();
                        else if (!emuLongDone && t.Item4 == "u")
                        {
                            Native.MoveTo(emuDownX, emuDownY);
                            Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero);
                            Native.mouse_event(Native.LeftUp, 0, 0, 0, UIntPtr.Zero);
                            if (Tapped != null) Tapped(emuDownX, emuDownY);
                        }
                        emuPrimary = null; emuDragging = false;
                    }
                }
            }
        }
        const double ScrollGain = 1.2;
        double emuScroll, emuVelocity; DateTime emuLastMove;
        System.Threading.Timer inertia; double inertiaV;
        void StartInertia(double v)
        {
            StopInertia(); inertiaV = v;
            inertia = new System.Threading.Timer(_ =>
            {
                inertiaV *= 0.92;
                if (Math.Abs(inertiaV) < 150) { StopInertia(); return; }
                emuScroll += inertiaV / 60;
                int units = (int)emuScroll;
                if (units != 0) { Native.mouse_event(Native.Wheel, 0, 0, units, UIntPtr.Zero); emuScroll -= units; }
            }, null, 16, 16);
        }
        void StopInertia() { var t = inertia; inertia = null; if (t != null) t.Dispose(); }
        void CancelLong() { var t = emuLongTimer; emuLongTimer = null; if (t != null) t.Dispose(); }
        void LongPress()
        {
            if (emuPrimary == null || emuDragging || emuLongDone) return;
            emuLongDone = true;
            Native.MoveTo(emuDownX, emuDownY);
            Native.mouse_event(Native.RightDown, 0, 0, 0, UIntPtr.Zero);
            Native.mouse_event(Native.RightUp, 0, 0, 0, UIntPtr.Zero);
        }

        public void ResetDiagnostics() { touchSeen = penSeen = touchWarned = penWarned = false; }

        // ---- Stylet : « PD x y pression [e] », « PM … », « PU x y » (e = gomme)
        void Pen(string[] p)
        {
            double nx, ny, pr = 0.5;
            if (p.Length < 3 || !Num(p[1], out nx) || !Num(p[2], out ny)) return;
            if (p.Length >= 4) Num(p[3], out pr);
            bool eraser = p.Length >= 5 && p[4] == "e";
            int x = ToX(nx), y = ToY(ny);
            if (emuPen)
            {
                // émulation souris du stylet : le trait suit le bouton gauche (sans pression)
                Native.MoveTo(x, y);
                if (p[0] == "PD") { Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); leftDown = true; }
                else if (p[0] == "PU") { ReleaseLeft(); if (Tapped != null) Tapped(x, y); }
                return;
            }
            var t = new Native.POINTER_TYPE_INFO { type = Native.PT_PEN };
            t.pen.pointerInfo.pointerType = Native.PT_PEN;
            t.pen.pointerInfo.ptPixelLocation = new Native.POINT { x = x, y = y };
            t.pen.penMask = 1;   // pression renseignée
            t.pen.pressure = (uint)Math.Max(1, Math.Min(1024, pr * 1024));
            if (eraser) t.pen.penFlags = 2 | 4;   // inversé + gomme
            switch (p[0])
            {
                case "PD": t.pen.pointerInfo.pointerFlags = Native.PF_INRANGE | Native.PF_INCONTACT | Native.PF_FIRSTBUTTON | Native.PF_DOWN; penDown = true; break;
                case "PM": t.pen.pointerInfo.pointerFlags = Native.PF_INRANGE | Native.PF_INCONTACT | Native.PF_FIRSTBUTTON | Native.PF_UPDATE; break;
                default:
                    t.pen.pointerInfo.pointerFlags = Native.PF_INRANGE | Native.PF_UP; t.pen.pressure = 0;
                    Native.InjectPen(t);
                    t.pen.pointerInfo.pointerFlags = Native.PF_UPDATE;   // le stylet quitte la zone de détection
                    penDown = false; break;
            }
            bool ok = Native.InjectPen(t);
            if (ok && p[0] == "PU") CheckNative(true, x, y);
            if (!penSeen) { penSeen = true; Log.Write("stylet : premier trait reçu (" + x + "," + y + ", pression " + t.pen.pressure + ") → " + (ok ? "injecté" : "refusé, erreur " + Native.PointerError) + ", fenêtre « " + Native.ForegroundTitle() + " »"); }
            else if (!ok && !penWarned) { penWarned = true; Log.Write("stylet refusé (erreur " + Native.PointerError + "), fenêtre « " + Native.ForegroundTitle() + " »"); }
        }

        public void ReleaseAll()
        {
            ReleaseLeft();
            if (penDown) Pen(new[] { "PU", "0", "0" });
        }

        public Rectangle Bounds { get { lock (this) return bounds; } }

        // Écran de l'iPad : celui choisi dans les réglages, sinon l'écran virtuel de bSideDesk reconnu à son pilote.
        // Jamais « le premier écran secondaire » : avec un écran externe branché, ce serait lui qu'on pivoterait,
        // déplacerait ou débrancherait. Écran virtuel absent du bureau : TargetDevice = "" (aucune action sur les écrans).
        public volatile bool VirtualMissing;   // pilote présent mais écran virtuel débranché du bureau
        string lastTarget;
        public void RefreshBounds()
        {
            var screens = Screen.AllScreens;
            Screen s = null;
            if (!string.IsNullOrEmpty(cfg.TouchScreen)) s = screens.FirstOrDefault(x => x.DeviceName == cfg.TouchScreen);
            bool attached; var v = Displays.Virtual(out attached);
            if (s == null && v != null) s = screens.FirstOrDefault(x => string.Equals(x.DeviceName, v, StringComparison.OrdinalIgnoreCase));
            VirtualMissing = s == null && v != null;
            if (s == null && v == null)   // pilote non reconnu : écran secondaire aux dimensions de l'iPad
                s = screens.FirstOrDefault(x => !x.Primary && (x.Bounds.Width == 2048 || x.Bounds.Width == 1536) && (x.Bounds.Height >= 1400));
            if (s == null) s = Screen.PrimaryScreen;
            lock (this) bounds = s.Bounds;
            TargetDevice = s.Primary ? "" : s.DeviceName;
            if (TargetDevice != lastTarget)
            {
                lastTarget = TargetDevice;
                Log.Write("écran de l'iPad : " + (TargetDevice == "" ? (VirtualMissing ? "écran virtuel débranché" : "aucun écran virtuel trouvé") : TargetDevice + " " + s.Bounds.Width + "x" + s.Bounds.Height + " en " + s.Bounds.X + "," + s.Bounds.Y)
                          + " · écrans : " + Displays.Describe());
            }
        }

        public void ReleaseLeft()
        {
            if (leftDown) { Native.mouse_event(Native.LeftUp, 0, 0, 0, UIntPtr.Zero); leftDown = false; }
        }

        public void Handle(string line)
        {
            var p = line.Split(' ');
            if (p.Length >= 3 && "DMUR".IndexOf(p[0], StringComparison.Ordinal) >= 0 && p[0].Length == 1)
            {
                if (!cfg.Touch) return;
                double nx, ny;
                if (!double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out nx) ||
                    !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out ny)) return;
                var b = Bounds;
                Native.MoveTo((int)(b.Left + nx * (b.Width - 1)), (int)(b.Top + ny * (b.Height - 1)));
                switch (p[0])
                {
                    case "D": Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); leftDown = true; break;
                    case "U": ReleaseLeft(); if (Tapped != null) Tapped((int)(b.Left + nx * (b.Width - 1)), (int)(b.Top + ny * (b.Height - 1))); break;
                    case "R": Native.mouse_event(Native.RightDown, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.RightUp, 0, 0, 0, UIntPtr.Zero); break;
                }
                return;
            }
            int n, dx, dy;
            switch (p[0])
            {
                case "F": TouchFrame(p); break;
                case "SLEEP": if (Sleep != null) Sleep(); break;
                case "STANDBY": ReleaseAll(); if (Standby != null) Standby(true); break;
                case "ACTIVE": if (Standby != null) Standby(false); break;
                case "CAM": case "MIC": case "CB": case "SND": case "WIN": case "DB": case "FS": if (Media != null) Media(p); break;
                case "PD": case "PM": case "PU": Pen(p); break;
                case "RM": if (p.Length >= 3 && int.TryParse(p[1], out dx) && int.TryParse(p[2], out dy)) Native.MoveRelative(dx, dy); break;
                case "CL":
                    Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.LeftUp, 0, 0, 0, UIntPtr.Zero);
                    Native.POINT cp; if (Tapped != null && Native.GetCursorPos(out cp)) Tapped(cp.x, cp.y);
                    break;
                case "CR": Native.mouse_event(Native.RightDown, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.RightUp, 0, 0, 0, UIntPtr.Zero); break;
                case "DD": Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); leftDown = true; break;
                case "DU": ReleaseLeft(); break;
                case "KS": if (p.Length >= 2 && !Native.Combo(Uri.UnescapeDataString(p[1]))) Log.Write("raccourci inconnu : " + p[1]); break;
                case "W": if (cfg.Touch && p.Length >= 2 && int.TryParse(p[1], out n)) Native.mouse_event(Native.Wheel, 0, 0, n, UIntPtr.Zero); break;
                case "Z": if (cfg.Touch && p.Length >= 2 && int.TryParse(p[1], out n)) Native.Zoom(n); break;
                case "T":
                    if (p.Length >= 2)
                    {
                        Native.Failures = 0;
                        Native.Text(Uri.UnescapeDataString(p[1]));
                        if (Native.Failures > 0) Log.Write("frappe refusée par Windows (fenêtre « " + Native.ForegroundTitle() + " » en administrateur ?)");
                    }
                    break;
                case "K":
                    if (p.Length >= 2) Native.Press(p[1] == "ENTER" ? (ushort)0x0D : (ushort)0x08);
                    break;
                case "P": if (p.Length >= 2 && Reply != null) Reply("P " + p[1]); break;
                case "O": if (p.Length >= 2) SetOrientation(p[1] == "P"); break;
                case "RES": { int rw, rh; if (p.Length >= 3 && int.TryParse(p[1], out rw) && int.TryParse(p[2], out rh)) RequestResolution(rw, rh); break; }
                case "S":
                    int fps, mbps;
                    if (p.Length >= 3 && int.TryParse(p[1], out fps) && int.TryParse(p[2], out mbps) && (fps == 30 || fps == 60) && mbps >= 2 && mbps <= 50)
                    {
                        cfg.Fps = fps; cfg.Mbps = mbps; cfg.Save();
                        Log.Write("qualité demandée par l'iPad : " + fps + " i/s, " + mbps + " Mb/s");
                        if (RestartStream != null) RestartStream();
                    }
                    break;
            }
        }

        // Attend que Windows ait fini de redessiner l'écran (fond d'écran, icônes, barre des tâches) :
        // on compare une empreinte de l'image toutes les 150 ms ; stable = identique pendant 600 ms.
        static int WaitStable(Rectangle b, int maxMs)
        {
            var t0 = DateTime.Now; long last = 0; DateTime? since = null;
            while (true)
            {
                int el = (int)(DateTime.Now - t0).TotalMilliseconds;
                if (el >= maxMs) return el;
                long sig = Signature(b);
                if (sig != 0 && sig == last)
                {
                    if (since == null) since = DateTime.Now;
                    if ((DateTime.Now - since.Value).TotalMilliseconds >= 600 && el >= 800) return el;
                }
                else since = null;
                last = sig;
                Thread.Sleep(150);
            }
        }

        static long Signature(Rectangle b)
        {
            try
            {
                using (var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.X, b.Y, 0, 0, b.Size);
                    var d = bmp.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        long h = 17; int sx = Math.Max(1, b.Width / 48), sy = Math.Max(1, b.Height / 48);
                        for (int y = sy / 2; y < b.Height; y += sy)
                            for (int x = sx / 2; x < b.Width; x += sx)
                                h = h * 31 + (Marshal.ReadInt32(d.Scan0, y * d.Stride + x * 4) & 0xFFFFFF);
                        return h == 0 ? 1 : h;
                    }
                    finally { bmp.UnlockBits(d); }
                }
            }
            catch { return 0; }   // capture impossible pendant la bascule : pas encore stable
        }

        void SetOrientation(bool portrait)
        {
            RefreshBounds();
            var b = Bounds;
            if ((b.Height > b.Width) == portrait) return;
            ChangeResolution(b.Height, b.Width);
        }

        // « RES w h » : l'iPad demande la taille exacte de sa zone d'affichage (sans la Touch Bar),
        // pour que l'image remplisse l'écran sans bandes noires
        readonly System.Collections.Generic.HashSet<string> triedModes = new System.Collections.Generic.HashSet<string>();
        public Action AfterDriverRestart;   // le pilote d'écran vient de redémarrer : rebrancher l'écran virtuel
        void RequestResolution(int w, int h)
        {
            var dev = TargetDevice;
            if (string.IsNullOrEmpty(dev) || w < 320 || h < 320) return;
            if (!Native.SupportsMode(dev, w, h) && !triedModes.Contains(w + "x" + h))
            {
                // première demande de cette taille : bSideDesk l'ajoute lui-même au pilote d'écran (mode administrateur),
                // le pilote redémarre, l'écran se rebranche et l'iPad redemande sa taille
                triedModes.Add(w + "x" + h);
                Log.Write("mode " + w + "x" + h + " absent du pilote d'écran : ajout automatique");
                if (HoldStream != null) HoldStream(8000);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    bool ok = VirtualDriver.AddMode(w, h);
                    Thread.Sleep(3000);
                    if (ok && AfterDriverRestart != null) AfterDriverRestart();
                    RefreshBounds();
                    var d2 = TargetDevice;
                    if (!string.IsNullOrEmpty(d2) && Native.SupportsMode(d2, w, h)) ChangeResolution(w, h);
                    else { if (HoldStream != null) HoldStream(0); RequestResolution(w, h); }
                });
                return;
            }
            if (!Native.SupportsMode(dev, w, h))
            {
                Log.Write("mode " + w + "x" + h + " toujours absent du pilote d'écran (bSideDesk doit être en mode administrateur)");
                bool portrait = h > w;
                // repli : bonne orientation dans la taille iPad complète
                int fw = portrait ? 1536 : 2048, fh = portrait ? 2048 : 1536;
                if (Native.SupportsMode(dev, fw, fh)) ChangeResolution(fw, fh); else SetOrientation(portrait);
                return;
            }
            ChangeResolution(w, h);
        }

        void ChangeResolution(int w, int h)
        {
            RefreshBounds();
            var b = Bounds; var dev = TargetDevice;
            if (string.IsNullOrEmpty(dev) || (b.Width == w && b.Height == h)) return;
            if (!Displays.IsVirtual(dev)) { Log.Write("changement de définition refusé : " + dev + " n'est pas l'écran virtuel"); return; }
            // on coupe l'image AVANT de basculer : l'iPad ne voit pas Windows réorganiser le bureau,
            // puis la capture reprend une fois le fond d'écran et les icônes en place
            if (HoldStream != null) HoldStream(1200);
            int r = Native.SetResolution(dev, w, h);
            if (r == 0)
            {
                Log.Write("écran virtuel en " + w + "x" + h + (h > w ? " (portrait)" : " (paysage)"));
                RefreshBounds();
                if (Placed != null) Placed();
                RefreshBounds();
                if (HoldStream != null) HoldStream(6000);   // plafond : la capture reprend dès que le bureau est stable
                var nb = Bounds;
                if (nb.Width != w || nb.Height != h) nb = new Rectangle(nb.X, nb.Y, w, h);   // Windows pas encore à jour
                int seq = Interlocked.Increment(ref rotSeq);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int ms = WaitStable(nb, 5000);
                    if (seq != rotSeq) return;   // un autre changement a suivi : c'est lui qui relancera l'image
                    Log.Write("bureau stable après " + ms + " ms");
                    try
                    {
                        int n = WindowMover.RefitMaximized(nb);
                        if (n > 0) Log.Write(n + " fenêtre(s) agrandie(s) remise(s) à la taille de l'écran");
                    }
                    catch (Exception e) { Log.Write("recalage des fenêtres : " + e.Message); }
                    if (HoldStream != null) HoldStream(0);
                });
            }
            else
            {
                if (HoldStream != null) HoldStream(0);
                Log.Write("changement de résolution impossible (code " + r + ") : menu bSideDesk > « Installer les composants manquants… »");
            }
        }
    }
}
