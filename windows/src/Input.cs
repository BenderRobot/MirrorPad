// Rejoue sur Windows les gestes et la frappe reçus de l'iPad.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace MirrorPad
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
        public Action Placed;                       // après une rotation : replacer l'écran (gauche / droite / dessus)
        public Action<int> HoldStream;              // couper ffmpeg et attendre n ms avant de le relancer (rotation)
        public Action Sleep;                        // l'iPad se verrouille
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
                        emuWheelAcc += (int)Math.Round(dy * 3);
                        int steps = emuWheelAcc / 40;
                        if (steps != 0) { Native.mouse_event(Native.Wheel, 0, 0, steps * 40, UIntPtr.Zero); emuWheelAcc -= steps * 40; }
                    }
                }
                emuLastCy = cy; emuLastSpread = spread;
                return;
            }
            emuLastCy = double.NaN; emuLastSpread = double.NaN; emuWheelAcc = 0; emuZoomAcc = 0;
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
                        }
                        emuPrimary = null; emuDragging = false;
                    }
                }
            }
        }
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
                else if (p[0] == "PU") ReleaseLeft();
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

        // Écran qui reçoit le tactile : celui choisi, sinon l'écran 2048×1536 / 1536×2048, sinon le premier écran secondaire
        public void RefreshBounds()
        {
            var screens = Screen.AllScreens;
            var s = screens.FirstOrDefault(x => !string.IsNullOrEmpty(cfg.TouchScreen) && x.DeviceName == cfg.TouchScreen)
                 ?? screens.FirstOrDefault(x => (x.Bounds.Width == 2048 && x.Bounds.Height == 1536) || (x.Bounds.Width == 1536 && x.Bounds.Height == 2048))
                 ?? screens.FirstOrDefault(x => !x.Primary)
                 ?? Screen.PrimaryScreen;
            lock (this) bounds = s.Bounds;
            TargetDevice = s.Primary ? "" : s.DeviceName;
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
                    case "U": ReleaseLeft(); break;
                    case "R": Native.mouse_event(Native.RightDown, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.RightUp, 0, 0, 0, UIntPtr.Zero); break;
                }
                return;
            }
            int n, dx, dy;
            switch (p[0])
            {
                case "F": TouchFrame(p); break;
                case "SLEEP": if (Sleep != null) Sleep(); break;
                case "CAM": case "MIC": case "CB": if (Media != null) Media(p); break;
                case "PD": case "PM": case "PU": Pen(p); break;
                case "RM": if (p.Length >= 3 && int.TryParse(p[1], out dx) && int.TryParse(p[2], out dy)) Native.MoveRelative(dx, dy); break;
                case "CL": Native.mouse_event(Native.LeftDown, 0, 0, 0, UIntPtr.Zero); Native.mouse_event(Native.LeftUp, 0, 0, 0, UIntPtr.Zero); break;
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
            var b = Bounds; var dev = TargetDevice;
            if ((b.Height > b.Width) == portrait || string.IsNullOrEmpty(dev)) return;
            // on coupe l'image AVANT de basculer : l'iPad ne voit pas Windows réorganiser le bureau,
            // puis la capture reprend une fois le fond d'écran et les icônes en place
            if (HoldStream != null) HoldStream(1200);
            int r = Native.SetResolution(dev, b.Height, b.Width);
            if (r == 0)
            {
                Log.Write("écran virtuel en " + (portrait ? "portrait" : "paysage") + " (" + b.Height + "x" + b.Width + ")");
                RefreshBounds();
                if (Placed != null) Placed();
                RefreshBounds();
                if (HoldStream != null) HoldStream(6000);   // plafond : la capture reprend dès que le bureau est stable
                var nb = Bounds;
                if ((nb.Height > nb.Width) != portrait) nb = new Rectangle(nb.X, nb.Y, nb.Height, nb.Width);   // Windows pas encore à jour
                int seq = Interlocked.Increment(ref rotSeq);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    int ms = WaitStable(nb, 5000);
                    if (seq != rotSeq) return;   // une autre rotation a suivi : c'est elle qui relancera l'image
                    Log.Write("bureau stable après " + ms + " ms");
                    if (HoldStream != null) HoldStream(0);
                });
            }
            else
            {
                if (HoldStream != null) HoldStream(0);
                Log.Write("rotation impossible (code " + r + ") : lancez Ajouter-Portrait.ps1 en administrateur");
            }
        }
    }
}
