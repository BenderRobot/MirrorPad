// Fenêtres : appairage et paramètres.
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MirrorPad
{
    // Demande d'appairage : le même code doit s'afficher sur l'iPad
    public class PairingForm : Form
    {
        public PairingForm(string ipadName, string code)
        {
            Text = "MirrorPad – Appairage"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = true;
            Font = new Font("Segoe UI", 10); ClientSize = new Size(420, 250); AutoScaleMode = AutoScaleMode.Dpi;
            var intro = new Label { Text = "« " + ipadName + " » demande à se connecter à ce PC.\nVérifiez que l'iPad affiche exactement ce code :", Location = new Point(20, 18), Size = new Size(380, 48) };
            var codeLbl = new Label { Text = code.Substring(0, 3) + " " + code.Substring(3), Font = new Font("Segoe UI", 32, FontStyle.Bold), ForeColor = Color.FromArgb(59, 91, 255), Location = new Point(20, 70), Size = new Size(380, 70), TextAlign = ContentAlignment.MiddleCenter };
            var warn = new Label { Text = "Si les codes sont différents, refusez : un autre appareil tente de s'interposer.", ForeColor = Color.DimGray, Location = new Point(20, 148), Size = new Size(380, 40) };
            var ok = new Button { Text = "Accepter", DialogResult = DialogResult.OK, Location = new Point(196, 200), Size = new Size(100, 32) };
            var no = new Button { Text = "Refuser", DialogResult = DialogResult.Cancel, Location = new Point(304, 200), Size = new Size(96, 32) };
            Controls.AddRange(new Control[] { intro, codeLbl, warn, ok, no });
            CancelButton = no;
            var t = new Timer { Interval = 120000 }; t.Tick += (s, e) => { t.Stop(); DialogResult = DialogResult.Cancel; }; t.Start();   // refus après 2 min
        }
    }

    public class SettingsForm : Form
    {
        readonly Config cfg;
        CheckBox disc, auto, sleep, clip; TextBox ip; NumericUpDown output, mbps; ComboBox fps, enc; ListBox paired;
        readonly string[] encKeys;

        public SettingsForm(Config c)
        {
            cfg = c;
            Text = "MirrorPad – Paramètres"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen; TopMost = true; Font = new Font("Segoe UI", 10);
            ClientSize = new Size(420, 470); AutoScaleMode = AutoScaleMode.Dpi;
            int y = 16;
            disc = new CheckBox { Text = "Trouver l'iPad automatiquement", Checked = c.AutoDiscover, Location = new Point(16, y), AutoSize = true }; Controls.Add(disc); y += 32;
            ip = new TextBox { Text = c.IPad }; Row("Adresse de l'iPad", ip, ref y);
            output = new NumericUpDown { Minimum = 0, Maximum = 9, Value = c.Output }; Row("Numéro d'écran", output, ref y);
            fps = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; fps.Items.AddRange(new object[] { "30", "60" }); fps.SelectedItem = c.Fps.ToString(); if (fps.SelectedIndex < 0) fps.SelectedIndex = 0; Row("Images / seconde", fps, ref y);
            mbps = new NumericUpDown { Minimum = 2, Maximum = 50, Value = c.Mbps }; Row("Débit (Mb/s)", mbps, ref y);
            encKeys = new[] { "auto" }.Concat(Streamer.EncoderOrder).ToArray();
            enc = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            enc.Items.Add("Automatique" + (string.IsNullOrEmpty(c.DetectedEncoder) ? "" : " (" + Streamer.EncoderNames[c.DetectedEncoder] + ")"));
            foreach (var k in Streamer.EncoderOrder) enc.Items.Add(Streamer.EncoderNames[k]);
            enc.SelectedIndex = Math.Max(0, Array.IndexOf(encKeys, c.Encoder)); Row("Encodage", enc, ref y);
            auto = new CheckBox { Text = "Se connecter au lancement", Checked = c.AutoConnect, Location = new Point(16, y), AutoSize = true }; Controls.Add(auto); y += 30;
            sleep = new CheckBox { Text = "Retirer l'écran virtuel quand l'iPad est verrouillé", Checked = c.SleepWithIPad, Location = new Point(16, y), AutoSize = true }; Controls.Add(sleep); y += 30;
            clip = new CheckBox { Text = "Partager le presse-papiers avec l'iPad", Checked = c.ShareClipboard, Location = new Point(16, y), AutoSize = true }; Controls.Add(clip); y += 40;

            Controls.Add(new Label { Text = "iPad appairés", Font = new Font("Segoe UI", 10, FontStyle.Bold), Location = new Point(16, y), AutoSize = true }); y += 26;
            paired = new ListBox { Location = new Point(16, y), Size = new Size(280, 90) };
            lock (c.Paired) foreach (var d in c.Paired) paired.Items.Add(d.Name + "  (" + d.Added + ")");
            var forget = new Button { Text = "Oublier", Location = new Point(306, y), Size = new Size(98, 30) };
            forget.Click += (s, e) =>
            {
                int i = paired.SelectedIndex; if (i < 0) return;
                lock (cfg.Paired) cfg.Paired.RemoveAt(i);
                paired.Items.RemoveAt(i); cfg.Save();
            };
            Controls.Add(paired); Controls.Add(forget); y += 104;

            var ok = new Button { Text = "Enregistrer", DialogResult = DialogResult.OK, Location = new Point(196, y), Size = new Size(110, 32) };
            var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(314, y), Size = new Size(90, 32) };
            Controls.Add(ok); Controls.Add(cancel); AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(420, y + 48);
        }

        void Row(string label, Control ctrl, ref int y)
        {
            Controls.Add(new Label { Text = label, Location = new Point(16, y + 4), AutoSize = true });
            ctrl.Location = new Point(200, y); ctrl.Width = 200; Controls.Add(ctrl); y += 36;
        }

        // Applique les réglages ; renvoie false si l'adresse est invalide
        public bool Apply()
        {
            System.Net.IPAddress a;
            if ((ip.Text.Trim().Length > 0 || !disc.Checked) && !System.Net.IPAddress.TryParse(ip.Text.Trim(), out a))
            {
                MessageBox.Show("Adresse IP invalide.", "MirrorPad"); return false;
            }
            cfg.AutoDiscover = disc.Checked; cfg.IPad = ip.Text.Trim(); cfg.Output = (int)output.Value;
            cfg.Fps = int.Parse((string)fps.SelectedItem); cfg.Mbps = (int)mbps.Value;
            cfg.Encoder = encKeys[enc.SelectedIndex]; cfg.DetectedEncoder = ""; cfg.AutoConnect = auto.Checked; cfg.SleepWithIPad = sleep.Checked; cfg.ShareClipboard = clip.Checked;
            cfg.Save();
            return true;
        }
    }
}
