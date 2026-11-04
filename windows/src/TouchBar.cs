// Touch Bar contextuelle : boutons de raccourcis adaptés à l'application active du PC, envoyés à l'iPad.
// Touches au format de Native.Combo (« ctrl+shift+s », « f5 »…) ; « @swap » et « @numpad » sont des actions de bSideDesk.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace bSideDesk
{
    public static class TouchBar
    {
        // nom du programme (sans .exe, en minuscules) -> (nom affiché, boutons « libellé|touches »)
        static readonly Dictionary<string, Tuple<string, string[]>> Profiles = new Dictionary<string, Tuple<string, string[]>>();
        static void P(string names, string title, params string[] buttons) { foreach (var n in names.Split(',')) Profiles[n] = Tuple.Create(title, buttons); }

        static TouchBar()
        {
            P("mspaint", "Paint", "Annuler|ctrl+z", "Rétablir|ctrl+y", "Taille +|ctrl+plus", "Taille −|ctrl+minus", "Zoom +|ctrl+pgup", "Zoom −|ctrl+pgdn",
              "Tout sélectionner|ctrl+a", "Enregistrer|ctrl+s", "Nouveau|ctrl+n");
            P("photoshop", "Photoshop", "Pinceau|b", "Gomme|e", "Déplacer|v", "Lasso|l", "Pipette|i", "Main|h", "Inverser couleurs|x",
              "Zoom +|ctrl+plus", "Zoom −|ctrl+minus", "Annuler|ctrl+z", "Rétablir|ctrl+shift+z", "Nouveau calque|ctrl+shift+n");
            // Mode présentateur : Alt+F5 lance le diaporama avec le « mode Présentateur » de PowerPoint, qu'on place sur
            // l'écran de l'iPad (notes, diapo suivante, chrono) pendant que le public voit l'écran principal / le projecteur
            P("powerpnt", "PowerPoint", "Présentateur|alt+f5", "Diaporama|f5", "◀ Précédente|left", "Suivante ▶|right", "Laser|ctrl+l",
              "Stylo|ctrl+p", "Effacer l'encre|e", "Écran noir|b", "Écran blanc|w", "Quitter|esc");
            P("code", "VS Code", "Exécuter|ctrl+f5", "Déboguer|f5", "Pas à pas|f10", "Entrer|f11", "Arrêter|shift+f5", "Point d'arrêt|f9",
              "Commandes|ctrl+shift+p", "Rechercher|ctrl+shift+f", "Formater|shift+alt+f", "Enregistrer|ctrl+s");
            P("chrome,brave,msedge,firefox,opera", "Navigateur", "◀ Précédent|alt+left", "Suivant ▶|alt+right", "Recharger|f5", "Adresse|ctrl+l",
              "Nouvel onglet|ctrl+t", "Fermer onglet|ctrl+w", "Onglet suivant|ctrl+tab", "Zoom +|ctrl+plus", "Zoom −|ctrl+minus", "Plein écran|f11");
            P("winword", "Word", "Gras|ctrl+g", "Italique|ctrl+i", "Souligné|ctrl+u", "Annuler|ctrl+z", "Rétablir|ctrl+y", "Copier la forme|ctrl+shift+c",
              "Coller la forme|ctrl+shift+v", "Enregistrer|ctrl+s", "Imprimer|ctrl+p");
            P("excel", "Excel", "Pavé numérique|@numpad", "Modifier cellule|f2", "Copier|ctrl+c", "Coller|ctrl+v", "Annuler|ctrl+z", "Rétablir|ctrl+y",
              "Insérer|ctrl+plus", "Supprimer|ctrl+minus", "Enregistrer|ctrl+s");
            P("explorer", "Explorateur", "◀ Précédent|alt+left", "Dossier parent|alt+up", "Nouveau dossier|ctrl+shift+n", "Renommer|f2",
              "Copier|ctrl+c", "Coller|ctrl+v", "Supprimer|del", "Propriétés|alt+enter", "Adresse|ctrl+l");
            P("teams,ms-teams", "Teams", "Micro|ctrl+shift+m", "Caméra|ctrl+shift+o", "Partager|ctrl+shift+e", "Lever la main|ctrl+shift+k",
              "Raccrocher|ctrl+shift+h", "Conversations|ctrl+2", "Calendrier|ctrl+4");
            P("whatsapp,whatsapp.root", "WhatsApp", "Clavier|@keyboard", "Envoyer|enter", "Emoji|win+period", "Rechercher|ctrl+f",
              "Nouvelle discussion|ctrl+n", "Discussion suivante|ctrl+tab", "Copier|ctrl+c", "Coller|ctrl+v");
            P("vlc,wmplayer,spotify,music.ui", "Lecteur", "⏮|prev", "⏯|playpause", "⏭|next", "Volume −|voldown", "Volume +|volup", "Muet|mute");
        }

        static readonly string[] Default = { "Fenêtre ⇄ iPad|@swap", "Clavier|@keyboard", "Copier|ctrl+c", "Coller|ctrl+v", "Annuler|ctrl+z", "Rétablir|ctrl+y",
                                             "Rechercher|ctrl+f", "Bureau|win+d", "Vue des tâches|win+tab", "Capture|win+shift+s", "Pavé numérique|@numpad" };

        // ligne « TB <titre> <bouton> <bouton>… » pour le programme au premier plan
        // otherScreen : le PC a un autre écran que l'iPad (sinon « Fenêtre ⇄ iPad » ne servirait à rien)
        public static string LineFor(string process, bool otherScreen = true)
        {
            Tuple<string, string[]> p;
            string title; IEnumerable<string> buttons;
            if (process != null && Profiles.TryGetValue(process.ToLowerInvariant(), out p))
            {
                title = p.Item1;
                buttons = new[] { "Fenêtre ⇄ iPad|@swap" }.Concat(p.Item2);
            }
            else { title = "Bureau"; buttons = Default; }
            if (!otherScreen) buttons = buttons.Where(b => !b.EndsWith("|@swap"));
            return "TB " + Uri.EscapeDataString(title) + " " + string.Join(" ", buttons.Select(Uri.EscapeDataString));
        }

        // programme de la fenêtre au premier plan (nom sans .exe), ou null
        public static string ForegroundProcess(out IntPtr hwnd)
        {
            hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            uint pid; GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == (uint)Process.GetCurrentProcess().Id) return null;
            try { using (var pr = Process.GetProcessById((int)pid)) return pr.ProcessName; } catch { return null; }
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }

    // Envoi de la fenêtre active vers l'écran de l'iPad (agrandie), ou retour sur l'écran principal
    public static class WindowMover
    {
        public static string Swap(System.Drawing.Rectangle ipad)
        {
            IntPtr h = GetForegroundWindow();
            if (h == IntPtr.Zero || ipad.Width <= 0) return null;
            var cls = new StringBuilder(64); GetClassName(h, cls, 64);
            var c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return null;   // bureau / barre des tâches
            var cur = System.Windows.Forms.Screen.FromHandle(h);
            bool onIPad = cur.Bounds.IntersectsWith(ipad) && cur.Bounds.Contains(ipad.Left + ipad.Width / 2, ipad.Top + ipad.Height / 2);
            var target = onIPad ? System.Windows.Forms.Screen.PrimaryScreen : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.Bounds == ipad);
            if (target == null) return null;
            var wa = target.WorkingArea;
            ShowWindow(h, 9);   // SW_RESTORE : sinon une fenêtre agrandie ne change pas d'écran
            SetWindowPos(h, IntPtr.Zero, wa.Left + 40, wa.Top + 40, Math.Max(400, wa.Width - 80), Math.Max(300, wa.Height - 80), 0x0004 /* NOZORDER */);
            ShowWindow(h, 3);   // SW_MAXIMIZE
            SetForegroundWindow(h);
            return onIPad ? "écran principal" : "iPad";
        }

        // ---- Avant de débrancher l'écran de l'iPad : ses fenêtres sont rangées sur l'écran principal ----
        // Sans ça, Windows les déplace lui-même mais garde leur taille iPad : une fenêtre agrandie ou plein écran
        // dépasse alors en haut de l'écran et sa barre de titre (boutons fermer / réduire) devient inaccessible.
        public class Parked { public IntPtr Hwnd; public bool Maximized; public float Rx, Ry, Rw, Rh; public System.Drawing.Rectangle PlacedAt; }

        public static List<Parked> Evacuate(System.Drawing.Rectangle ipad)
        {
            var list = new List<Parked>();
            if (ipad.Width <= 0) return list;
            var wa = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            var hwnds = new List<IntPtr>();
            EnumWindows((h, l) => { hwnds.Add(h); return true; }, IntPtr.Zero);
            foreach (var h in hwnds)
            {
                if (!IsWindowVisible(h) || IsIconic(h)) continue;
                long ex = GetWindowLongPtr(h, -20).ToInt64();   // GWL_EXSTYLE
                if ((ex & 0x80) != 0) continue;                   // WS_EX_TOOLWINDOW (bulles, barres flottantes)
                int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) continue;   // fenêtres UWP cachées
                var cls = new StringBuilder(64); GetClassName(h, cls, 64);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") continue;
                RECT r; if (!GetWindowRect(h, out r)) continue;
                int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
                if (!ipad.Contains(cx, cy)) continue;
                bool max = IsZoomed(h);
                if (max) ShowWindow(h, 4 /* SW_SHOWNOACTIVATE : taille normale */);
                if (!GetWindowRect(h, out r)) continue;
                int w = Math.Min(r.Right - r.Left, wa.Width - 40), hh = Math.Min(r.Bottom - r.Top, wa.Height - 40);
                // même position relative que sur l'iPad, mais toujours entièrement visible (barre de titre comprise)
                int nx = wa.Left + (int)((r.Left - ipad.Left) / (double)ipad.Width * wa.Width);
                int ny = wa.Top + (int)((r.Top - ipad.Top) / (double)ipad.Height * wa.Height);
                nx = Math.Max(wa.Left, Math.Min(nx, wa.Right - w)); ny = Math.Max(wa.Top, Math.Min(ny, wa.Bottom - hh));
                SetWindowPos(h, IntPtr.Zero, nx, ny, w, hh, 0x0004 | 0x0010 /* NOZORDER | NOACTIVATE */);
                if (max) ShowWindow(h, 3 /* SW_MAXIMIZE, sur l'écran principal */);
                RECT now; GetWindowRect(h, out now);
                list.Add(new Parked
                {
                    Hwnd = h, Maximized = max,
                    Rx = (r.Left - ipad.Left) / (float)ipad.Width, Ry = (r.Top - ipad.Top) / (float)ipad.Height,
                    Rw = (r.Right - r.Left) / (float)ipad.Width, Rh = (r.Bottom - r.Top) / (float)ipad.Height,
                    PlacedAt = System.Drawing.Rectangle.FromLTRB(now.Left, now.Top, now.Right, now.Bottom)
                });
            }
            return list;
        }

        // Retour des fenêtres rangées sur l'écran de l'iPad, s'il est rebranché et qu'on ne les a pas déplacées entre-temps
        public static int Return(List<Parked> list, System.Drawing.Rectangle ipad)
        {
            int n = 0;
            if (list == null || ipad.Width <= 0) return 0;
            foreach (var p in list)
            {
                if (!IsWindow(p.Hwnd) || IsIconic(p.Hwnd)) continue;
                RECT r; if (!GetWindowRect(p.Hwnd, out r)) continue;
                if (System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) != p.PlacedAt) continue;   // déplacée par l'utilisateur : on n'y touche pas
                if (p.Maximized) ShowWindow(p.Hwnd, 4);
                int w = Math.Max(300, (int)(p.Rw * ipad.Width)), h = Math.Max(200, (int)(p.Rh * ipad.Height));
                int x = ipad.Left + (int)(p.Rx * ipad.Width), y = ipad.Top + (int)(p.Ry * ipad.Height);
                x = Math.Max(ipad.Left, Math.Min(x, ipad.Right - w)); y = Math.Max(ipad.Top, Math.Min(y, ipad.Bottom - h));
                SetWindowPos(p.Hwnd, IntPtr.Zero, x, y, Math.Min(w, ipad.Width), Math.Min(h, ipad.Height), 0x0004 | 0x0010);
                if (p.Maximized) ShowWindow(p.Hwnd, 3);
                n++;
            }
            return n;
        }

        // Après un changement de définition de l'écran de l'iPad (rotation, Touch Bar) : certaines applis
        // (VS Code et autres applis Electron / Chromium) ne suivent pas la nouvelle taille quand elles sont agrandies :
        // il reste des bandes blanches ou des morceaux de l'ancienne image. On les remet à la taille de l'écran
        // (rétablir puis agrandir) et on force Windows à tout redessiner.
        public static int RefitMaximized(System.Drawing.Rectangle ipad)
        {
            int n = 0;
            if (ipad.Width <= 0) return 0;
            var hwnds = new List<IntPtr>();
            EnumWindows((h, l) => { hwnds.Add(h); return true; }, IntPtr.Zero);
            foreach (var h in hwnds)
            {
                if (!IsWindowVisible(h) || IsIconic(h)) continue;
                long ex = GetWindowLongPtr(h, -20).ToInt64(); if ((ex & 0x80) != 0) continue;
                int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
                RECT r; if (!GetWindowRect(h, out r)) continue;
                if (!ipad.Contains((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) continue;
                if (IsZoomed(h))
                {
                    var wa = System.Windows.Forms.Screen.FromHandle(h).WorkingArea;
                    // une fenêtre agrandie couvre la zone de travail (bordures invisibles de 8 px au plus en plus)
                    bool fits = r.Left <= wa.Left && r.Top <= wa.Top + 1 && r.Right >= wa.Right && r.Bottom >= wa.Bottom
                                && r.Left >= wa.Left - 16 && r.Right <= wa.Right + 16 && r.Bottom <= wa.Bottom + 16;
                    if (!fits)
                    {
                        ShowWindow(h, 4);   // SW_SHOWNOACTIVATE : taille normale
                        ShowWindow(h, 3);   // SW_MAXIMIZE : agrandie sur la nouvelle taille de l'écran
                        n++;
                    }
                }
                // nouveau cadre + tout redessiner (efface les restes de l'ancienne image)
                SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
                RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0400 | 0x0100);   // INVALIDATE|ERASE|ALLCHILDREN|FRAME|UPDATENOW
            }
            return n;
        }
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);

        // Filet de sécurité : toute fenêtre dont la barre de titre est hors de tous les écrans est ramenée en vue
        public static int Rescue()
        {
            int n = 0;
            var hwnds = new List<IntPtr>();
            EnumWindows((h, l) => { hwnds.Add(h); return true; }, IntPtr.Zero);
            var screens = System.Windows.Forms.Screen.AllScreens;
            foreach (var h in hwnds)
            {
                if (!IsWindowVisible(h) || IsIconic(h) || IsZoomed(h)) continue;
                long ex = GetWindowLongPtr(h, -20).ToInt64(); if ((ex & 0x80) != 0) continue;
                int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
                RECT r; if (!GetWindowRect(h, out r) || r.Right - r.Left < 120 || r.Bottom - r.Top < 60) continue;
                // la barre de titre (30 px sous le haut de la fenêtre, au milieu) doit être sur un écran
                if (screens.Any(sc => sc.WorkingArea.Contains((r.Left + r.Right) / 2, r.Top + 15))) continue;
                var sc0 = System.Windows.Forms.Screen.FromRectangle(System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).WorkingArea;
                int w = Math.Min(r.Right - r.Left, sc0.Width), hh = Math.Min(r.Bottom - r.Top, sc0.Height);
                int x = Math.Max(sc0.Left, Math.Min(r.Left, sc0.Right - w)), y = Math.Max(sc0.Top, Math.Min(r.Top, sc0.Bottom - hh));
                SetWindowPos(h, IntPtr.Zero, x, y, w, hh, 0x0004 | 0x0010);
                n++;
            }
            return n;
        }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hgt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    }

    // Raccourci clavier global Ctrl+Alt+I : envoie la fenêtre active sur l'iPad (ou la ramène)
    public class HotkeyWindow : System.Windows.Forms.NativeWindow, IDisposable
    {
        public Action Pressed;
        public HotkeyWindow()
        {
            CreateHandle(new System.Windows.Forms.CreateParams());
            if (!RegisterHotKey(Handle, 1, 0x0001 | 0x0002 | 0x4000 /* ALT | CTRL | NOREPEAT */, 0x49 /* I */))
                Log.Write("raccourci Ctrl+Alt+I indisponible (déjà utilisé par un autre logiciel)");
        }
        protected override void WndProc(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == 0x0312 /* WM_HOTKEY */ && Pressed != null) Pressed();
            base.WndProc(ref m);
        }
        public void Dispose() { try { UnregisterHotKey(Handle, 1); DestroyHandle(); } catch { } }
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    }
}
