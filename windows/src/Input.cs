// Rejoue sur Windows les gestes et la frappe reçus de l'iPad.
using System;
using System.Drawing;
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
        public Action<string> Reply;                // envoi d'une ligne à l'iPad
        public Action RestartStream;                // relancer ffmpeg (rotation, qualité)
        public Action Sleep;                        // l'iPad se verrouille

        public InputHandler(Config c) { cfg = c; }

        int ToX(double nx) { var b = Bounds; return (int)(b.Left + nx * (b.Width - 1)); }
        int ToY(double ny) { var b = Bounds; return (int)(b.Top + ny * (b.Height - 1)); }
        static bool Num(string s, out double v) { return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v); }

        // ---- Tactile Windows natif : « F id:x:y:état … » (d = posé, m = déplacé, u = levé, c = annulé), tous les doigts à chaque trame
        void TouchFrame(string[] p)
        {
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
            if (list.Count > 0 && !Native.InjectTouch(list.ToArray()) && !touchWarned)
            {
                touchWarned = true;
                Log.Write("tactile natif indisponible (erreur " + Native.PointerError + ") : Windows 10 1809 ou plus récent requis");
            }
        }
        bool touchWarned, penDown;

        // ---- Stylet : « PD x y pression [e] », « PM … », « PU x y » (e = gomme)
        void Pen(string[] p)
        {
            double nx, ny, pr = 0.5;
            if (p.Length < 3 || !Num(p[1], out nx) || !Num(p[2], out ny)) return;
            if (p.Length >= 4) Num(p[3], out pr);
            bool eraser = p.Length >= 5 && p[4] == "e";
            int x = ToX(nx), y = ToY(ny);
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
            if (!Native.InjectPen(t) && !touchWarned)
            {
                touchWarned = true;
                Log.Write("stylet indisponible (erreur " + Native.PointerError + ") : Windows 10 1809 ou plus récent requis");
            }
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
                Native.SetCursorPos((int)(b.Left + nx * (b.Width - 1)), (int)(b.Top + ny * (b.Height - 1)));
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

        void SetOrientation(bool portrait)
        {
            RefreshBounds();
            var b = Bounds; var dev = TargetDevice;
            if ((b.Height > b.Width) == portrait || string.IsNullOrEmpty(dev)) return;
            int r = Native.SetResolution(dev, b.Height, b.Width);
            if (r == 0)
            {
                Log.Write("écran virtuel en " + (portrait ? "portrait" : "paysage") + " (" + b.Height + "x" + b.Width + ")");
                RefreshBounds();
                if (RestartStream != null) RestartStream();
            }
            else Log.Write("rotation impossible (code " + r + ") : lancez Ajouter-Portrait.ps1 en administrateur");
        }
    }
}
