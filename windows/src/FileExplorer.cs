// Fenêtre « Fichiers PC ⇄ iPad » : deux explorateurs côte à côte. À gauche ce PC, à droite le dossier bSideDesk
// de l'iPad. On choisit le dossier de destination de chaque côté, puis on envoie (boutons, glisser d'un volet à
// l'autre, ou glisser depuis l'Explorateur Windows vers le volet iPad).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace bSideDesk
{
    public class FileExplorer : Form
    {
        public Func<Session> GetSession;
        public Action<string> SendLine;   // ligne de contrôle vers l'iPad

        static readonly Color Ink = Color.FromArgb(24, 28, 48), Muted = Color.FromArgb(110, 116, 140), Blue = Color.FromArgb(59, 91, 255), Line = Color.FromArgb(226, 229, 240);
        readonly FileList pcList, padList;
        readonly ComboBox pcPath;
        readonly Label padPath, status;
        readonly ProgressBar bar;
        readonly Button bSend, bGet;
        readonly ImageList icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(16, 16) };
        readonly Dictionary<string, int> iconIndex = new Dictionary<string, int>();
        string pcDir = "";            // "" = « Ce PC » (lecteurs)
        string padDir = "/";
        int listId;
        volatile bool busy;
        long doneBytes, totalBytes;

        public FileExplorer()
        {
            Text = "bSideDesk – Fichiers PC ⇄ iPad"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1080, 640);
            MinimumSize = new Size(760, 420); Font = new Font("Segoe UI", 9.5f); BackColor = Color.White; AutoScaleMode = AutoScaleMode.Dpi; KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // bandeau dégradé aux couleurs de l'app
            var head = new Panel { Dock = DockStyle.Top, Height = 56 };
            head.Paint += (s, e) =>
            {
                using (var br = new LinearGradientBrush(head.ClientRectangle, Color.Black, Color.Black, 0f))
                {
                    br.InterpolationColors = new ColorBlend { Positions = new[] { 0f, .5f, 1f }, Colors = new[] { Color.FromArgb(22, 198, 255), Color.FromArgb(59, 91, 255), Color.FromArgb(122, 46, 246) } };
                    e.Graphics.FillRectangle(br, head.ClientRectangle);
                }
                TextRenderer.DrawText(e.Graphics, "Fichiers PC ⇄ iPad", new Font("Segoe UI Semibold", 14), new Point(16, 8), Color.White);
                TextRenderer.DrawText(e.Graphics, "Choisissez le dossier de destination de chaque côté, puis envoyez ou faites glisser.", Font, new Point(18, 33), Color.FromArgb(230, 236, 255));
            };
            head.Resize += (s, e) => head.Invalidate();

            icons.Images.Add(SysIcon("dossier", true)); iconIndex["/dir"] = 0;
            icons.Images.Add(SysIcon("C:\\", true, drive: true)); iconIndex["/drive"] = 1;

            // ---- volet PC ----
            var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 6, 0) };
            pcPath = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var q in Places()) pcPath.Items.Add(q);
            pcPath.SelectedIndexChanged += (s, e) => { var q = pcPath.SelectedItem as Place; if (q != null) ShowPc(q.Path); };
            pcPath.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ShowPc(pcPath.Text.Trim()); } };
            pcList = NewList();
            pcList.ItemActivate += (s, e) => OpenPc();
            var pcBar = Toolbar("Ce PC", Btn("↑", () => ShowPc(ParentOf(pcDir)), 34), pcPath, Btn("⟳", () => ShowPc(pcDir), 34), Btn("Nouveau dossier", NewPcFolder, 0));
            left.Controls.Add(pcList); left.Controls.Add(pcBar);

            // ---- volet iPad ----
            var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 10, 12, 0) };
            padPath = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ink, BackColor = Color.FromArgb(244, 246, 252), Padding = new Padding(6, 0, 0, 0) };
            padList = NewList();
            padList.ItemActivate += (s, e) => { var it = Sel(padList).FirstOrDefault(); if (it != null && ((Entry)it.Tag).Dir) ListPad(Join(padDir, ((Entry)it.Tag).Name)); };
            padList.FilesDropped = files => SendToPad(files);   // glisser depuis l'Explorateur Windows
            var padBar = Toolbar("iPad › bSideDesk", Btn("↑", () => ListPad(ParentPad(padDir)), 34), padPath, Btn("⟳", () => ListPad(padDir), 34), Btn("Nouveau dossier", NewPadFolder, 0));
            right.Controls.Add(padList); right.Controls.Add(padBar);

            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, BackColor = Line };
            split.Panel1.BackColor = Color.White; split.Panel2.BackColor = Color.White;
            split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
            Load += (s, e) => { try { split.SplitterDistance = split.Width / 2; } catch { } };

            // ---- barre d'actions ----
            var foot = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12, 10, 12, 10), BackColor = Color.FromArgb(248, 249, 253) };
            bSend = Btn("Envoyer vers l'iPad  ▶", () => SendToPad(Sel(pcList).Select(i => ((Entry)i.Tag).Full).ToArray()), 0, true);
            bGet = Btn("◀  Copier sur le PC", GetFromPad, 0, true);
            var bRen = Btn("Renommer", RenamePad, 0);
            var bDel = Btn("Supprimer de l'iPad", DeletePad, 0);
            status = new Label { AutoSize = false, Width = 330, Height = 36, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted, Text = "" };
            bar = new ProgressBar { Width = 180, Height = 10, Maximum = 1000, Visible = false, Margin = new Padding(8, 13, 8, 0) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            flow.Controls.AddRange(new Control[] { bSend, bGet, bRen, bDel, bar, status });
            foot.Controls.Add(flow);

            Controls.Add(split); Controls.Add(foot); Controls.Add(head);

            // glisser d'un volet à l'autre (géré à la main : fonctionne aussi quand bSideDesk tourne en administrateur)
            InternalDrag(pcList, padList, () => SendToPad(Sel(pcList).Select(i => ((Entry)i.Tag).Full).ToArray()));
            InternalDrag(padList, pcList, GetFromPad);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F5) { ShowPc(pcDir); ListPad(padDir); }
                else if (e.KeyCode == Keys.Back && ActiveControl == pcList) ShowPc(ParentOf(pcDir));
                else if (e.KeyCode == Keys.Back && ActiveControl == padList) ListPad(ParentPad(padDir));
                else if (e.KeyCode == Keys.Delete && ActiveControl == padList) DeletePad();
                else if (e.KeyCode == Keys.F2 && ActiveControl == padList) RenamePad();
            };

            ShowPc(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            Shown += (s, e) => ListPad("/");
        }

        // ================= volet PC =================
        class Place { public string Name, Path; public override string ToString() { return Name; } }
        static IEnumerable<Place> Places()
        {
            yield return new Place { Name = "Ce PC", Path = "" };
            yield return new Place { Name = "Bureau", Path = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
            yield return new Place { Name = "Documents", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            yield return new Place { Name = "Téléchargements", Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") };
            yield return new Place { Name = "Images", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) };
            yield return new Place { Name = "Vidéos", Path = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
            foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady)) yield return new Place { Name = d.Name, Path = d.Name };
        }

        void ShowPc(string dir)
        {
            if (dir == null) return;
            if (dir != "" && !Directory.Exists(dir)) { Status("Dossier introuvable : " + dir, true); pcPath.Text = pcDir; return; }
            pcDir = dir;
            pcPath.Text = dir == "" ? "Ce PC" : dir;
            pcList.BeginUpdate(); pcList.Items.Clear();
            try
            {
                if (dir == "")
                    foreach (var d in DriveInfo.GetDrives().Where(x => x.IsReady))
                        pcList.Items.Add(Item(new Entry { Name = d.Name, Full = d.Name, Dir = true }, Label(d), 1));
                else
                {
                    var di = new DirectoryInfo(dir);
                    foreach (var d in di.EnumerateDirectories().Where(Visible).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                        pcList.Items.Add(Item(new Entry { Name = d.Name, Full = d.FullName, Dir = true, Date = d.LastWriteTime }, d.Name, 0));
                    foreach (var f in di.EnumerateFiles().Where(Visible).OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                        pcList.Items.Add(Item(new Entry { Name = f.Name, Full = f.FullName, Size = f.Length, Date = f.LastWriteTime }, f.Name, IconFor(f.Name)));
                }
            }
            catch (Exception e) { Status("Accès refusé : " + e.Message, true); }
            pcList.EndUpdate();
        }
        static string Label(DriveInfo d) { try { return (d.VolumeLabel.Length > 0 ? d.VolumeLabel + " " : "") + "(" + d.Name.TrimEnd('\\') + ")"; } catch { return d.Name; } }
        static bool Visible(FileSystemInfo f) { return (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0; }
        static string ParentOf(string dir) { if (string.IsNullOrEmpty(dir)) return ""; var p = Directory.GetParent(dir.TrimEnd('\\') + (dir.EndsWith(":\\") ? "\\" : "")); return p == null || dir.Length <= 3 ? "" : p.FullName; }

        void OpenPc()
        {
            var it = Sel(pcList).FirstOrDefault(); if (it == null) return;
            var en = (Entry)it.Tag;
            if (en.Dir) ShowPc(en.Full);
            else try { Process.Start(new ProcessStartInfo(en.Full) { UseShellExecute = true }); } catch { }
        }

        void NewPcFolder()
        {
            if (pcDir == "") return;
            var name = Ask("Nouveau dossier sur le PC", "Nom du dossier :", "Nouveau dossier");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { Directory.CreateDirectory(Path.Combine(pcDir, name.Trim())); ShowPc(pcDir); } catch (Exception e) { Status(e.Message, true); }
        }

        // ================= volet iPad =================
        class Entry { public string Name, Full; public bool Dir; public long Size; public DateTime Date; }

        void ListPad(string dir)
        {
            if (SendLine == null) return;
            padDir = dir;
            padPath.Text = "iPad › bSideDesk" + (dir == "/" ? "" : dir.Replace("/", " › "));
            int id = Interlocked.Increment(ref listId);
            SendLine("FS LIST " + id + " " + Uri.EscapeDataString(dir));
        }

        // lignes « FS … » de l'iPad (appelé depuis la connexion : on repasse sur la fenêtre)
        public void OnLine(string[] p)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((Action)(() => HandleLine(p)));
        }

        void HandleLine(string[] p)
        {
            if (p.Length < 2) return;
            switch (p[1])
            {
                case "ITEMS":
                    int id; if (p.Length < 4 || !int.TryParse(p[2], out id) || id != listId) return;
                    padDir = Uri.UnescapeDataString(p[3]);
                    padPath.Text = "iPad › bSideDesk" + (padDir == "/" ? "" : padDir.Replace("/", " › "));
                    var items = new List<ListViewItem>();
                    foreach (var raw in p.Skip(4))
                    {
                        var f = Uri.UnescapeDataString(raw).Split('|');
                        if (f.Length < 4) continue;
                        var en = new Entry { Name = f[0], Dir = f[1] == "d" };
                        long.TryParse(f[2], out en.Size);
                        double t; if (double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out t)) en.Date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime();
                        items.Add(Item(en, en.Name, en.Dir ? 0 : IconFor(en.Name)));
                    }
                    padList.BeginUpdate(); padList.Items.Clear();
                    padList.Items.AddRange(items.Where(i => ((Entry)i.Tag).Dir).Concat(items.Where(i => !((Entry)i.Tag).Dir)).ToArray());
                    padList.EndUpdate();
                    if (!busy) Status(items.Count + " élément(s) sur l'iPad", false);
                    break;
                case "OK": ListPad(padDir); break;
                case "ERR": Status("iPad : " + (p.Length > 2 ? Uri.UnescapeDataString(p[2]) : "erreur"), true); ListPad(padDir); break;
                case "DONE":   // un fichier envoyé est arrivé sur l'iPad
                    var rel = p.Length > 2 ? Uri.UnescapeDataString(p[2]) : "";
                    var parent = rel.Contains("/") ? rel.Substring(0, rel.LastIndexOf('/')) : "";
                    if ((parent.Length == 0 ? "/" : parent) == padDir) { if (busy) refreshPending = true; else ListPad(padDir); }
                    break;
            }
        }
        bool refreshPending;

        void NewPadFolder()
        {
            var name = Ask("Nouveau dossier sur l'iPad", "Nom du dossier :", "Nouveau dossier");
            if (string.IsNullOrWhiteSpace(name) || SendLine == null) return;
            SendLine("FS MKDIR " + Uri.EscapeDataString(Join(padDir, name.Trim())));
        }

        void RenamePad()
        {
            var it = Sel(padList).FirstOrDefault(); if (it == null || SendLine == null) return;
            var en = (Entry)it.Tag;
            var name = Ask("Renommer sur l'iPad", "Nouveau nom :", en.Name);
            if (string.IsNullOrWhiteSpace(name) || name == en.Name) return;
            SendLine("FS REN " + Uri.EscapeDataString(Join(padDir, en.Name)) + " " + Uri.EscapeDataString(name.Trim()));
        }

        void DeletePad()
        {
            var sel = Sel(padList).Select(i => (Entry)i.Tag).ToList();
            if (sel.Count == 0 || SendLine == null) return;
            var what = sel.Count == 1 ? "« " + sel[0].Name + " »" : sel.Count + " éléments";
            if (MessageBox.Show(this, "Supprimer " + what + " de l'iPad ? Cette action est définitive.", "Supprimer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var en in sel) SendLine("FS DEL " + Uri.EscapeDataString(Join(padDir, en.Name)));
        }

        static string Join(string dir, string name) { return (dir.TrimEnd('/') + "/" + name).Replace("//", "/"); }
        static string ParentPad(string dir) { var d = dir.TrimEnd('/'); int i = d.LastIndexOf('/'); return i <= 0 ? "/" : d.Substring(0, i); }

        // ================= transferts =================
        void SendToPad(string[] paths)
        {
            paths = (paths ?? new string[0]).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            if (paths.Length == 0) { Status("Sélectionnez des fichiers ou dossiers dans le volet « Ce PC ».", true); return; }
            var s = GetSession != null ? GetSession() : null;
            if (s == null || s.IsClosed) { Status("L'iPad n'est pas connecté.", true); return; }
            if (busy) { Status("Un transfert est déjà en cours.", true); return; }
            var dest = padDir;
            Begin(paths.Sum(p => FileTransfer.SizeOf(p)), "Envoi vers l'iPad › " + (dest == "/" ? "bSideDesk" : dest.Trim('/')) + "…");
            new Thread(() =>
            {
                string err = null;
                try { foreach (var p in paths) FileTransfer.SendTree(s, p, dest, Progress); }
                catch (Exception e) { err = e.Message; }
                Thread.Sleep(600);   // le dernier morceau arrive sur l'iPad
                End(err == null ? paths.Length + " élément(s) envoyé(s) sur l'iPad" : "Envoi interrompu : " + err, err != null);
                Post(() => ListPad(padDir));
            }) { IsBackground = true, Name = "envoi vers l'iPad" }.Start();
        }

        void GetFromPad()
        {
            var sel = Sel(padList).Select(i => (Entry)i.Tag).ToList();
            if (sel.Count == 0) { Status("Sélectionnez des fichiers ou dossiers dans le volet iPad.", true); return; }
            if (pcDir == "") { Status("Choisissez un dossier de destination dans le volet « Ce PC ».", true); return; }
            if (SendLine == null) return;
            if (busy) { Status("Un transfert est déjà en cours.", true); return; }
            var token = Guid.NewGuid().ToString("N").Substring(0, 12);
            var dest = pcDir;
            long total = sel.Sum(x => x.Size);
            Begin(total, "Copie sur le PC › " + dest + "…");
            int files = 0;
            DateTime last = DateTime.Now;
            var dl = new FileTransfer.Download { Dir = dest };
            dl.Progress = n => { last = DateTime.Now; Progress(n); };
            dl.FileDone = path => { Interlocked.Increment(ref files); last = DateTime.Now; };
            lock (FileTransfer.Downloads) FileTransfer.Downloads[token] = dl;
            foreach (var en in sel) SendLine("FS GET " + token + " " + Uri.EscapeDataString(Join(padDir, en.Name)));
            // fin : tout reçu (taille connue), ou plus rien depuis 4 s (dossiers : taille inconnue d'avance)
            new Thread(() =>
            {
                while (!IsDisposed)
                {
                    Thread.Sleep(300);
                    bool allBytes = total > 0 && Interlocked.Read(ref doneBytes) >= total && sel.All(x => !x.Dir);
                    if (allBytes || (DateTime.Now - last).TotalSeconds > (files == 0 ? 15 : 4)) break;
                }
                lock (FileTransfer.Downloads) FileTransfer.Downloads.Remove(token);
                End(files > 0 ? files + " fichier(s) copié(s) dans " + dest : "Rien reçu de l'iPad (connexion ?)", files == 0);
                Post(() => { if (pcDir == dest) ShowPc(pcDir); });
            }) { IsBackground = true, Name = "copie depuis l'iPad" }.Start();
        }

        void Begin(long total, string text)
        {
            busy = true; doneBytes = 0; totalBytes = total;
            Post(() =>
            {
                bSend.Enabled = bGet.Enabled = false;
                bar.Visible = true; bar.Style = total > 0 ? ProgressBarStyle.Continuous : ProgressBarStyle.Marquee; bar.Value = 0;
                Status(text, false);
            });
        }
        DateTime lastUi;
        void Progress(long n)
        {
            long d = Interlocked.Add(ref doneBytes, n);
            if ((DateTime.Now - lastUi).TotalMilliseconds < 150) return;
            lastUi = DateTime.Now;
            Post(() =>
            {
                if (totalBytes > 0) bar.Value = (int)Math.Min(1000, d * 1000 / totalBytes);
                status.Text = Size(d) + (totalBytes > 0 ? " / " + Size(totalBytes) : "");
            });
        }
        void End(string text, bool error)
        {
            busy = false;
            Post(() => { bSend.Enabled = bGet.Enabled = true; bar.Visible = false; Status(text, error); if (refreshPending) { refreshPending = false; ListPad(padDir); } });
        }

        // ================= outils d'affichage =================
        void Post(Action a) { try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { } }
        void Status(string t, bool error) { status.ForeColor = error ? Color.FromArgb(200, 40, 60) : Muted; status.Text = t; }

        static List<ListViewItem> Sel(ListView l) { return l.SelectedItems.Cast<ListViewItem>().ToList(); }

        ListViewItem Item(Entry en, string text, int icon)
        {
            var it = new ListViewItem(text, icon) { Tag = en };
            it.SubItems.Add(en.Dir ? "" : Size(en.Size));
            it.SubItems.Add(en.Date == DateTime.MinValue ? "" : en.Date.ToString("dd/MM/yyyy HH:mm"));
            return it;
        }

        static string Size(long b)
        {
            if (b < 1024) return b + " o";
            if (b < 1048576) return (b / 1024.0).ToString("0.#") + " Ko";
            if (b < 1073741824) return (b / 1048576.0).ToString("0.#") + " Mo";
            return (b / 1073741824.0).ToString("0.##") + " Go";
        }

        FileList NewList()
        {
            var l = new FileList { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, BorderStyle = BorderStyle.None, SmallImageList = icons, ForeColor = Ink };
            l.Columns.Add("Nom", 260); l.Columns.Add("Taille", 80, HorizontalAlignment.Right); l.Columns.Add("Modifié", 120);
            l.Resize += (s, e) => { int w = l.ClientSize.Width - 80 - 120 - 4; if (w > 120) l.Columns[0].Width = w; };
            return l;
        }

        Panel Toolbar(string title, Button up, Control path, Button refresh, Button extra)
        {
            var p = new Panel { Dock = DockStyle.Top, Height = 70 };
            p.Controls.Add(new Label { Text = title, Font = new Font("Segoe UI Semibold", 11), ForeColor = Ink, Location = new Point(0, 2), AutoSize = true });
            var row = new TableLayoutPanel { Location = new Point(0, 30), Height = 32, ColumnCount = 4, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40)); row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(up, 0, 0); row.Controls.Add(path, 1, 0); row.Controls.Add(refresh, 2, 0); row.Controls.Add(extra, 3, 0);
            p.Resize += (s, e) => row.Width = p.ClientSize.Width;
            p.Controls.Add(row);
            return p;
        }

        Button Btn(string text, Action a, int width, bool primary = false)
        {
            var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Height = 32, Margin = new Padding(0, 0, 8, 0), Cursor = Cursors.Hand };
            if (width > 0) b.Width = width; else b.AutoSize = true;
            b.BackColor = primary ? Blue : Color.FromArgb(240, 242, 250); b.ForeColor = primary ? Color.White : Ink;
            b.FlatAppearance.BorderSize = 0;
            if (width > 0) b.Dock = DockStyle.Fill;
            b.Click += (s, e) => a();
            return b;
        }

        static string Ask(string title, string prompt, string value)
        {
            using (var f = new Form { Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(360, 124), MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 9.5f), AutoScaleMode = AutoScaleMode.Dpi })
            {
                var t = new TextBox { Text = value, Location = new Point(14, 36), Width = 332 };
                var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(190, 80), Size = new Size(76, 30) };
                var no = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(270, 80), Size = new Size(76, 30) };
                f.Controls.AddRange(new Control[] { new Label { Text = prompt, Location = new Point(12, 12), AutoSize = true }, t, ok, no });
                f.AcceptButton = ok; f.CancelButton = no;
                f.Shown += (s, e) => t.SelectAll();
                return f.ShowDialog() == DialogResult.OK ? t.Text : null;
            }
        }

        int IconFor(string name)
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext == "") ext = ".__";
            int i;
            if (iconIndex.TryGetValue(ext, out i)) return i;
            icons.Images.Add(SysIcon("x" + ext, false));
            i = icons.Images.Count - 1; iconIndex[ext] = i;
            return i;
        }

        static Image SysIcon(string path, bool folder, bool drive = false)
        {
            var info = new SHFILEINFO();
            uint flags = 0x100 | 0x1 | (drive ? 0u : 0x10u);   // SHGFI_ICON | SMALLICON | USEFILEATTRIBUTES
            try { SHGetFileInfo(path, folder ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), flags); } catch { }
            if (info.hIcon == IntPtr.Zero) return new Bitmap(16, 16);
            try { using (var ic = Icon.FromHandle(info.hIcon)) return ic.ToBitmap(); }
            finally { DestroyIcon(info.hIcon); }
        }

        // Glisser d'un volet à l'autre : suivi de la souris après le début du glisser, dépôt quand on relâche sur l'autre volet
        static void InternalDrag(ListView from, ListView to, Action drop)
        {
            from.ItemDrag += (s, e) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 30 };
                from.Cursor = Cursors.Hand;
                t.Tick += (o, a) =>
                {
                    if ((Control.MouseButtons & MouseButtons.Left) != 0) { Cursor.Current = to.RectangleToScreen(to.ClientRectangle).Contains(Cursor.Position) ? Cursors.Hand : Cursors.No; return; }
                    t.Stop(); t.Dispose(); from.Cursor = Cursors.Default;
                    if (to.RectangleToScreen(to.ClientRectangle).Contains(Cursor.Position)) drop();
                };
                t.Start();
            };
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }

    // Liste qui accepte les fichiers glissés depuis l'Explorateur Windows, même quand bSideDesk tourne en
    // administrateur (le glisser-déposer classique est bloqué entre un programme normal et un programme administrateur)
    public class FileList : ListView
    {
        public Action<string[]> FilesDropped;
        public FileList() { DoubleBuffered = true; }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                foreach (uint msg in new uint[] { 0x0233 /* WM_DROPFILES */, 0x004A /* WM_COPYDATA */, 0x0049 /* WM_COPYGLOBALDATA */ })
                    ChangeWindowMessageFilterEx(Handle, msg, 1, IntPtr.Zero);
                DragAcceptFiles(Handle, true);
            }
            catch { }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0233)
            {
                var files = new List<string>();
                uint n = DragQueryFile(m.WParam, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder(1024);
                    DragQueryFile(m.WParam, i, sb, 1024);
                    files.Add(sb.ToString());
                }
                DragFinish(m.WParam);
                if (FilesDropped != null && files.Count > 0) FilesDropped(files.ToArray());
                return;
            }
            base.WndProc(ref m);
        }
        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
        [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr h, bool accept);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, StringBuilder file, int size);
        [DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
    }
}
