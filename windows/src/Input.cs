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

        public InputHandler(Config c) { cfg = c; }

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
            int n;
            switch (p[0])
            {
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
