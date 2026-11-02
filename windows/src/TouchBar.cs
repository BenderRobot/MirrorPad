// Touch Bar contextuelle : boutons de raccourcis adaptés à l'application active du PC, envoyés à l'iPad.
// Touches au format de Native.Combo (« ctrl+shift+s », « f5 »…) ; « @swap » et « @numpad » sont des actions de MirrorPad.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace MirrorPad
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
            P("powerpnt", "PowerPoint", "Diaporama|f5", "Depuis ici|shift+f5", "◀ Précédente|left", "Suivante ▶|right", "Écran noir|b", "Écran blanc|w",
              "Quitter|esc", "Nouvelle diapo|ctrl+m", "Enregistrer|ctrl+s");
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
            P("vlc,wmplayer,spotify,music.ui", "Lecteur", "⏮|prev", "⏯|playpause", "⏭|next", "Volume −|voldown", "Volume +|volup", "Muet|mute");
        }

        static readonly string[] Default = { "Fenêtre ⇄ iPad|@swap", "Copier|ctrl+c", "Coller|ctrl+v", "Annuler|ctrl+z", "Rétablir|ctrl+y",
                                             "Rechercher|ctrl+f", "Bureau|win+d", "Vue des tâches|win+tab", "Capture|win+shift+s", "Pavé numérique|@numpad" };

        // ligne « TB <titre> <bouton> <bouton>… » pour le programme au premier plan
        public static string LineFor(string process)
        {
            Tuple<string, string[]> p;
            string title; IEnumerable<string> buttons;
            if (process != null && Profiles.TryGetValue(process.ToLowerInvariant(), out p))
            {
                title = p.Item1;
                buttons = new[] { "Fenêtre ⇄ iPad|@swap" }.Concat(p.Item2);
            }
            else { title = "Bureau"; buttons = Default; }
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
