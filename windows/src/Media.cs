// Capteurs de l'iPad côté PC : webcam virtuelle (caméra d'OBS Studio), micro (VB-CABLE), presse-papiers, photos reçues.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MirrorPad
{
    // ---------- Webcam : H.264 de l'iPad -> ffmpeg (décodage NV12) -> « OBS Virtual Camera » ----------
    // La caméra virtuelle d'OBS Studio (logiciel signé, très répandu) lit ses images dans une mémoire partagée
    // nommée « OBSVirtualCamVideo » : on y écrit nous-mêmes les images de l'iPad, sans lancer OBS.
    // Format (plugins/win-dshow/shared-memory-queue.c d'OBS) : en-tête puis 3 tampons [horodatage 32 o][image NV12].
    public class CameraBridge
    {
        public const int W = 1920, H = 1080, Fps = 30;   // format par défaut annoncé par la caméra OBS
        const int FrameSize = W * H * 3 / 2, FrameHeader = 32, HeaderSize = 80;
        const uint StateStarting = 1, StateReady = 2, StateStopping = 3;
        Process decoder;
        Thread pump;
        IntPtr mapping = IntPtr.Zero, view = IntPtr.Zero;
        readonly int[] offsets = new int[3];
        public Action<string> Notify;
        public Func<string, int> RunElevated;   // conservé pour compatibilité (plus utilisé)

        public static bool ObsCameraInstalled()
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramW6432") })
            {
                if (string.IsNullOrEmpty(root)) continue;
                if (File.Exists(Path.Combine(root, @"obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll"))) return true;
            }
            using (var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"CLSID\{A3FCE0F5-3493-419F-958A-ABA1250EC20B}"))
                return k != null;
        }

        static int Align(int v) { return (v + 31) & ~31; }

        bool OpenQueue()
        {
            int size = Align(HeaderSize);
            for (int i = 0; i < 3; i++) { offsets[i] = size; size = Align(size + FrameSize + FrameHeader); }
            // accessible aux applis non administrateur (Teams, Zoom…) même si MirrorPad tourne en administrateur
            IntPtr sd; uint sdLen;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)S:(ML;;NW;;;LW)", 1, out sd, out sdLen)) sd = IntPtr.Zero;
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd };
            mapping = CreateFileMapping(new IntPtr(-1), ref sa, 0x04 /* PAGE_READWRITE */, 0, (uint)size, "OBSVirtualCamVideo");
            int err = Marshal.GetLastWin32Error();
            if (sd != IntPtr.Zero) LocalFree(sd);
            if (mapping == IntPtr.Zero) return false;
            if (err == 183 /* déjà existante : OBS diffuse déjà sa propre caméra */) { CloseHandle(mapping); mapping = IntPtr.Zero; return false; }
            view = MapViewOfFile(mapping, 0xF001F, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; return false; }
            // en-tête : write_idx, read_idx, state, offsets[3], type, cx, cy, (pad), interval (u64), reserved[8]
            for (int i = 0; i < HeaderSize; i += 4) Marshal.WriteInt32(view, i, 0);
            Marshal.WriteInt32(view, 8, (int)StateStarting);
            for (int i = 0; i < 3; i++) Marshal.WriteInt32(view, 12 + i * 4, offsets[i]);
            Marshal.WriteInt32(view, 24, 0);   // type vidéo
            Marshal.WriteInt32(view, 28, W);
            Marshal.WriteInt32(view, 32, H);
            Marshal.WriteInt64(view, 40, 10000000L / Fps);   // intervalle en unités de 100 ns
            return true;
        }

        void WriteFrame(byte[] nv12)
        {
            int inc = Marshal.ReadInt32(view, 0) + 1;
            int idx = inc % 3;
            Marshal.Copy(nv12, 0, view + offsets[idx] + FrameHeader, FrameSize);
            Marshal.WriteInt64(view + offsets[idx], Stopwatch.GetTimestamp() * (1000000000L / Stopwatch.Frequency));
            Marshal.WriteInt32(view, 0, inc);   // write_idx
            Marshal.WriteInt32(view, 4, inc);   // read_idx
            Marshal.WriteInt32(view, 8, (int)StateReady);
        }

        void CloseQueue()
        {
            if (view != IntPtr.Zero) { try { Marshal.WriteInt32(view, 8, (int)StateStopping); } catch { } UnmapViewOfFile(view); view = IntPtr.Zero; }
            if (mapping != IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; }
        }

        public void Start(string ffmpeg)
        {
            Stop();
            if (!ObsCameraInstalled()) { Notify("Webcam : composant manquant. Menu MirrorPad > « Installer les composants manquants »."); return; }
            try
            {
                if (!OpenQueue()) { Notify("Webcam : la caméra OBS est déjà utilisée (fermez la caméra virtuelle d'OBS)."); return; }
                decoder = Process.Start(new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -fflags nobuffer -flags low_delay -f h264 -i pipe:0 " +
                    "-vf scale=" + W + ":" + H + ":force_original_aspect_ratio=decrease,pad=" + W + ":" + H + ":(ow-iw)/2:(oh-ih)/2,format=nv12 -f rawvideo pipe:1")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true });
                var d = decoder;
                pump = new Thread(() =>
                {
                    var buf = new byte[FrameSize];
                    try
                    {
                        var src = d.StandardOutput.BaseStream;
                        while (true)
                        {
                            int got = 0;
                            while (got < FrameSize) { int n = src.Read(buf, got, FrameSize - got); if (n <= 0) return; got += n; }
                            if (view == IntPtr.Zero) return;
                            WriteFrame(buf);
                        }
                    }
                    catch { }
                }) { IsBackground = true, Name = "webcam" };
                pump.Start();
                Log.Write("webcam MirrorPad active (caméra « OBS Virtual Camera »)");
            }
            catch (Exception e) { Notify("Webcam : " + e.Message); Stop(); }
        }

        public void Feed(byte[] h264)
        {
            var d = decoder;
            if (d == null) return;
            try { d.StandardInput.BaseStream.Write(h264, 0, h264.Length); d.StandardInput.BaseStream.Flush(); } catch { }
        }

        public void Stop()
        {
            var d = decoder; decoder = null;
            if (d != null)
            {
                try { d.StandardInput.Close(); } catch { }
                try { if (!d.WaitForExit(500)) d.Kill(); } catch { }
                Log.Write("webcam MirrorPad arrêtée");
            }
            var t = pump; pump = null;
            if (t != null) try { t.Join(1000); } catch { }
            CloseQueue();
        }

        [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint rev, out IntPtr sd, out uint len);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateFileMapping(IntPtr file, ref SECURITY_ATTRIBUTES sa, uint protect, uint sizeHigh, uint sizeLow, string name);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll")] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }

    // ---------- Micro : PCM 16 bits de l'iPad joué dans « CABLE Input » (VB-CABLE) ----------
    public class MicPlayer
    {
        [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }
        [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; public uint dwSupport; }
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(UIntPtr id, ref WAVEOUTCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr h, UIntPtr id, ref WAVEFORMATEX f, IntPtr cb, IntPtr inst, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr h);

        IntPtr dev = IntPtr.Zero;
        readonly List<IntPtr> pending = new List<IntPtr>();
        static readonly int HdrSize = Marshal.SizeOf(typeof(WAVEHDR));
        public Action<string> Notify;

        public static int FindCable()
        {
            uint n = waveOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var c = new WAVEOUTCAPS();
                if (waveOutGetDevCaps(new UIntPtr(i), ref c, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0 && c.szPname != null && c.szPname.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return (int)i;
            }
            return -1;
        }

        public void Start(int rate, int channels)
        {
            Stop();
            int id = FindCable();
            if (id < 0) { Notify("Micro : composant manquant. Menu MirrorPad > « Installer les composants manquants »."); return; }
            var f = new WAVEFORMATEX { wFormatTag = 1, nChannels = (ushort)channels, nSamplesPerSec = (uint)rate, wBitsPerSample = 16 };
            f.nBlockAlign = (ushort)(channels * 2); f.nAvgBytesPerSec = (uint)(rate * f.nBlockAlign);
            if (waveOutOpen(out dev, new UIntPtr((uint)id), ref f, IntPtr.Zero, IntPtr.Zero, 0) != 0) { dev = IntPtr.Zero; Notify("Micro : impossible d'ouvrir VB-CABLE."); return; }
            Log.Write("micro de l'iPad -> CABLE Input (" + rate + " Hz)");
        }

        public void Play(byte[] pcm)
        {
            if (dev == IntPtr.Zero || pcm.Length == 0) return;
            lock (pending)
            {
                // libère les tampons déjà joués ; au-delà de ~300 ms d'attente on jette (latence minimale)
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    var hd = (WAVEHDR)Marshal.PtrToStructure(pending[i], typeof(WAVEHDR));
                    if ((hd.dwFlags & 1) != 0) { Free(pending[i]); pending.RemoveAt(i); }
                }
                if (pending.Count > 15) return;
                var data = Marshal.AllocHGlobal(pcm.Length);
                Marshal.Copy(pcm, 0, data, pcm.Length);
                var hdr = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(new WAVEHDR { lpData = data, dwBufferLength = (uint)pcm.Length }, hdr, false);
                waveOutPrepareHeader(dev, hdr, HdrSize);
                waveOutWrite(dev, hdr, HdrSize);
                pending.Add(hdr);
            }
        }

        void Free(IntPtr hdr)
        {
            waveOutUnprepareHeader(dev, hdr, HdrSize);
            var hd = (WAVEHDR)Marshal.PtrToStructure(hdr, typeof(WAVEHDR));
            Marshal.FreeHGlobal(hd.lpData); Marshal.FreeHGlobal(hdr);
        }

        public void Stop()
        {
            if (dev == IntPtr.Zero) return;
            lock (pending)
            {
                waveOutReset(dev);
                foreach (var h in pending) Free(h);
                pending.Clear();
                waveOutClose(dev);
                dev = IntPtr.Zero;
            }
            Log.Write("micro de l'iPad arrêté");
        }
    }

    // ---------- Presse-papiers : Windows signale chaque copie (WM_CLIPBOARDUPDATE) ----------
    public class ClipboardWatcher : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
        public Action<string> TextCopied;
        public Action<byte[]> ImageCopied;
        string lastHash = "";
        public bool Enabled = true;

        public ClipboardWatcher() { CreateHandle(new CreateParams()); AddClipboardFormatListener(Handle); }

        static string Hash(byte[] b) { using (var h = SHA1.Create()) return Convert.ToBase64String(h.ComputeHash(b)); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x031D && Enabled) Changed();   // WM_CLIPBOARDUPDATE
            base.WndProc(ref m);
        }

        void Changed()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var t = Clipboard.GetText();
                    var h = Hash(Encoding.UTF8.GetBytes(t));
                    if (h == lastHash || t.Length > 500000) return;
                    lastHash = h; if (TextCopied != null) TextCopied(t);
                }
                else if (Clipboard.ContainsImage())
                {
                    using (var img = Clipboard.GetImage())
                    using (var ms = new MemoryStream())
                    {
                        if (img == null) return;
                        img.Save(ms, ImageFormat.Png);
                        var b = ms.ToArray(); var h = Hash(b);
                        if (h == lastHash || b.Length > 15 * 1024 * 1024) return;
                        lastHash = h; if (ImageCopied != null) ImageCopied(b);
                    }
                }
            }
            catch (Exception e) { Log.Write("presse-papiers : " + e.Message); }
        }

        // Contenu venant de l'iPad : on le mémorise pour ne pas le renvoyer en écho
        public void SetText(string t)
        {
            lastHash = Hash(Encoding.UTF8.GetBytes(t));
            try { Clipboard.SetText(t); } catch (Exception e) { Log.Write("presse-papiers : " + e.Message); }
        }

        public void SetImage(byte[] png)
        {
            try
            {
                using (var ms = new MemoryStream(png))
                using (var img = Image.FromStream(ms))
                {
                    Clipboard.SetImage(img);
                    using (var o = new MemoryStream()) { Clipboard.GetImage().Save(o, ImageFormat.Png); lastHash = Hash(o.ToArray()); }
                }
            }
            catch (Exception e) { Log.Write("presse-papiers (image) : " + e.Message); }
        }

        public void Dispose() { try { RemoveClipboardFormatListener(Handle); DestroyHandle(); } catch { } }
    }

    // ---------- Photos envoyées par l'iPad : rangées dans Téléchargements\MirrorPad ----------
    public static class Inbox
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MirrorPad");

        // Trame : [longueur du nom sur 2 octets][nom UTF-8][contenu]
        public static string Save(byte[] frame)
        {
            if (frame.Length < 2) return null;
            int n = frame[0] << 8 | frame[1];
            if (frame.Length < 2 + n) return null;
            var name = Encoding.UTF8.GetString(frame, 2, n);
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "photo.jpg";
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, name);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(Dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            using (var f = File.Create(path)) f.Write(frame, 2 + n, frame.Length - 2 - n);
            Log.Write("fichier reçu de l'iPad : " + path);
            return path;
        }
    }
}
