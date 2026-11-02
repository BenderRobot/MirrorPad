// Fenêtre « Caméras de l'iPad » : l'image de la caméra avant ou arrière de l'iPad, en direct sur le PC (webcam sans fil),
// avec photo et enregistrement vidéo. Le flux H.264 de l'iPad est décodé par ffmpeg en images BGR.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MirrorPad
{
    public class CameraViewer : Form
    {
        const int W = 1280, H = 720;
        readonly string ffmpeg;
        public Action<string> SendLine;        // ligne de contrôle vers l'iPad
        Process decoder, recorder;
        readonly PictureBox pic;
        readonly Label status;
        readonly Button bFront, bBack, bSnap, bRec;
        Bitmap last; readonly object frameLock = new object();
        DateTime lastFrame = DateTime.MinValue;
        int painting;   // 1 = une image attend d'être affichée
        string recPath;
        bool full;
        readonly System.Windows.Forms.Timer watch = new System.Windows.Forms.Timer { Interval = 1000 };

        public CameraViewer(string ffmpegPath)
        {
            ffmpeg = ffmpegPath;
            Text = "Caméras de l'iPad"; StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(1024, 640);
            BackColor = Color.FromArgb(10, 12, 30); ForeColor = Color.White; Font = new Font("Segoe UI", 10); KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(10, 9, 10, 0), BackColor = Color.FromArgb(27, 30, 80) };
            bFront = Btn("Caméra avant", () => Request("START front"));
            bBack = Btn("Caméra arrière", () => Request("START back"));
            bSnap = Btn("Photo", Snapshot);
            bRec = Btn("● Enregistrer", ToggleRecord);
            var bFull = Btn("Plein écran", ToggleFull);
            status = new Label { AutoSize = true, ForeColor = Color.FromArgb(200, 210, 255), Margin = new Padding(14, 8, 0, 0), Text = "Connexion à la caméra…" };
            bar.Controls.AddRange(new Control[] { bFront, bBack, bSnap, bRec, bFull, status });
            pic = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            pic.DoubleClick += (s, e) => ToggleFull();
            Controls.Add(pic); Controls.Add(bar);
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape && full) ToggleFull(); else if (e.KeyCode == Keys.F11) ToggleFull(); };
            watch.Tick += (s, e) =>
            {
                var age = (DateTime.Now - lastFrame).TotalSeconds;
                if (lastFrame == DateTime.MinValue) status.Text = "Connexion à la caméra… (l'app MirrorPad doit être ouverte sur l'iPad)";
                else if (age > 3) status.Text = "Plus d'image depuis " + (int)age + " s (iPad verrouillé ou hors de portée ?)";
                else status.Text = recorder != null ? "● Enregistrement en cours : " + Path.GetFileName(recPath) : "En direct";
            };
            watch.Start();
            StartDecoder();
        }

        Button Btn(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(59, 91, 255), Margin = new Padding(0, 0, 8, 0), Height = 32 };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => a();
            return b;
        }

        public void Request(string what) { if (SendLine != null) SendLine("CAMREQ " + what); }

        void StartDecoder()
        {
            decoder = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -probesize 32768 -analyzeduration 0 -f h264 -i pipe:0 " +
                "-vf scale=" + W + ":" + H + ":force_original_aspect_ratio=decrease,pad=" + W + ":" + H + ":(ow-iw)/2:(oh-ih)/2 -fps_mode passthrough -pix_fmt bgr24 -f rawvideo pipe:1")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
            var d = decoder;
            d.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data) && errLines++ < 20) Log.Write("caméra (décodage) : " + e.Data); };
            d.BeginErrorReadLine();
            new Thread(() =>
            {
                var buf = new byte[W * H * 3];
                try
                {
                    var src = d.StandardOutput.BaseStream;
                    while (true)
                    {
                        int got = 0;
                        while (got < buf.Length) { int n = src.Read(buf, got, buf.Length - got); if (n <= 0) return; got += n; }
                        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
                        var bd = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                        for (int y = 0; y < H; y++) Marshal.Copy(buf, y * W * 3, bd.Scan0 + y * bd.Stride, W * 3);
                        bmp.UnlockBits(bd);
                        if (lastFrame == DateTime.MinValue && !firstImageLogged) { firstImageLogged = true; Log.Write("caméra de l'iPad : première image affichée"); }
                        lastFrame = DateTime.Now;
                        if (IsDisposed) { bmp.Dispose(); return; }
                        if (Interlocked.CompareExchange(ref painting, 1, 0) != 0) { bmp.Dispose(); continue; }   // affichage en retard : image sautée
                        BeginInvoke((Action)(() =>
                        {
                            Bitmap old;
                            lock (frameLock) { old = last; last = bmp; }
                            pic.Image = bmp;
                            if (old != null) old.Dispose();
                            Interlocked.Exchange(ref painting, 0);
                        }));
                    }
                }
                catch { }
            }) { IsBackground = true, Name = "caméra iPad" }.Start();
        }

        // flux H.264 de l'iPad (trame 3). Appelé par la boucle de réception : ne bloque jamais.
        // Les images passent par une file vidée par un fil à part ; si le décodage prend du retard, on jette tout
        // jusqu'à la prochaine image clé (l'iPad met SPS/PPS devant chacune : 00 00 00 01 67)
        int errLines; bool firstImageLogged; long fed; bool waitKey; int dropped;
        readonly System.Collections.Generic.Queue<byte[]> feedQ = new System.Collections.Generic.Queue<byte[]>();
        Thread feeder;
        public void Feed(byte[] h264)
        {
            if (fed == 0) Log.Write("caméra de l'iPad : premières données reçues (" + h264.Length + " octets, début " + BitConverter.ToString(h264, 0, Math.Min(8, h264.Length)) + ")");
            fed += h264.Length;
            bool key = h264.Length > 4 && h264[0] == 0 && h264[1] == 0 && h264[2] == 0 && h264[3] == 1 && (h264[4] & 0x1F) == 7;
            lock (feedQ)
            {
                if (feedQ.Count > 15) { feedQ.Clear(); waitKey = true; if (dropped++ < 5) Log.Write("caméra de l'iPad : décodage en retard, on saute jusqu'à la prochaine image clé"); }
                if (waitKey && !key) return;
                waitKey = false;
                feedQ.Enqueue(h264);
                Monitor.Pulse(feedQ);
            }
            if (feeder == null)
            {
                feeder = new Thread(() =>
                {
                    while (!IsDisposed)
                    {
                        byte[] f;
                        lock (feedQ) { while (feedQ.Count == 0) { Monitor.Wait(feedQ, 500); if (IsDisposed) return; } f = feedQ.Dequeue(); }
                        try { var d = decoder; if (d != null) { d.StandardInput.BaseStream.Write(f, 0, f.Length); d.StandardInput.BaseStream.Flush(); } } catch { }
                        try { var r = recorder; if (r != null) r.StandardInput.BaseStream.Write(f, 0, f.Length); } catch { }
                    }
                }) { IsBackground = true, Name = "caméra iPad (envoi au décodeur)" };
                feeder.Start();
            }
        }

        public void CameraStopped() { lastFrame = DateTime.MinValue; }

        void Snapshot()
        {
            Bitmap copy;
            lock (frameLock) copy = last != null ? new Bitmap(last) : null;
            if (copy == null) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MirrorPad");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "iPad_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".png");
            copy.Save(path, ImageFormat.Png); copy.Dispose();
            status.Text = "Photo enregistrée : " + path;
            Log.Write("photo de la caméra de l'iPad : " + path);
        }

        // enregistrement sans réencodage : le H.264 de l'iPad est rangé tel quel dans un MP4
        void ToggleRecord()
        {
            if (recorder != null)
            {
                var r = recorder; recorder = null;
                try { r.StandardInput.Close(); r.WaitForExit(5000); } catch { }
                bRec.Text = "● Enregistrer"; bRec.BackColor = Color.FromArgb(59, 91, 255);
                status.Text = "Vidéo enregistrée : " + recPath;
                Log.Write("vidéo de la caméra de l'iPad : " + recPath);
                return;
            }
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MirrorPad");
            Directory.CreateDirectory(dir);
            recPath = Path.Combine(dir, "iPad_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".mp4");
            recorder = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -fflags +genpts -r 30 -f h264 -i pipe:0 -c copy -movflags +faststart \"" + recPath + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
            bRec.Text = "■ Arrêter"; bRec.BackColor = Color.FromArgb(220, 60, 70);
        }

        void ToggleFull()
        {
            full = !full;
            if (full) { FormBorderStyle = FormBorderStyle.None; WindowState = FormWindowState.Normal; WindowState = FormWindowState.Maximized; Controls[1].Visible = false; }
            else { FormBorderStyle = FormBorderStyle.Sizable; WindowState = FormWindowState.Normal; Controls[1].Visible = true; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            watch.Stop();
            if (recorder != null) ToggleRecord();
            Request("STOP");
            var d = decoder; decoder = null;
            try { if (d != null) { d.StandardInput.Close(); if (!d.WaitForExit(500)) d.Kill(); } } catch { }
            lock (frameLock) { if (last != null) last.Dispose(); last = null; }
            base.OnFormClosed(e);
        }
    }
}
