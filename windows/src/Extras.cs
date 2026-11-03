// Tableau de bord (état du PC + contrôles personnalisés) et transfert de fichiers dans les deux sens.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

namespace bSideDesk
{
    // Contrôle personnalisé du tableau de bord : une adresse web (code HTTP) ou un nom d'hôte (ping)
    [DataContract]
    public class DashboardCheck
    {
        [DataMember] public string Name;
        [DataMember] public string Target;   // « https://zabbix.exemple.fr » ou « 192.168.1.41 »
    }

    // Mesures envoyées à l'iPad toutes les 2 s tant que son tableau de bord est ouvert
    public class Dashboard
    {
        public Action<string> Send;
        public Func<List<DashboardCheck>> Checks;
        Timer timer, checkTimer; ulong lastIdle, lastKernel, lastUser; long lastUp, lastDown; DateTime lastNet;
        string checkLine = "";

        public void Start()
        {
            Stop();
            timer = new Timer(_ => Tick(), null, 0, 2000);
            checkTimer = new Timer(_ => RunChecks(), null, 0, 15000);
        }
        public void Stop()
        {
            var t = timer; timer = null; if (t != null) t.Dispose();
            var c = checkTimer; checkTimer = null; if (c != null) c.Dispose();
        }

        void Tick()
        {
            try
            {
                // processeur : part du temps non inactif depuis la dernière mesure
                long idle, kernel, user; GetSystemTimes(out idle, out kernel, out user);
                ulong di = (ulong)idle - lastIdle, dk = (ulong)kernel - lastKernel, du = (ulong)user - lastUser;
                lastIdle = (ulong)idle; lastKernel = (ulong)kernel; lastUser = (ulong)user;
                int cpu = dk + du > 0 ? (int)Math.Round(100.0 * (dk + du - di) / (dk + du)) : 0;
                // mémoire
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                GlobalMemoryStatusEx(ref m);
                double totalGb = m.ullTotalPhys / 1073741824.0, usedGb = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
                // réseau (toutes les cartes actives)
                long up = 0, down = 0;
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    try { var s = n.GetIPv4Statistics(); up += s.BytesSent; down += s.BytesReceived; } catch { }
                }
                var now = DateTime.Now; double sec = (now - lastNet).TotalSeconds;
                double upK = lastNet == DateTime.MinValue || sec <= 0 ? 0 : (up - lastUp) / 1024.0 / sec, downK = lastNet == DateTime.MinValue || sec <= 0 ? 0 : (down - lastDown) / 1024.0 / sec;
                lastUp = up; lastDown = down; lastNet = now;
                // disque système
                var d = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory));
                int disk = (int)Math.Round(100.0 * (d.TotalSize - d.TotalFreeSpace) / Math.Max(1, d.TotalSize));
                long uptimeMin = EnvExt.TickCount64Safe() / 60000;
                var ci = CultureInfo.InvariantCulture;
                if (Send != null)
                {
                    Send(string.Format(ci, "DB {0} {1:0.0} {2:0.0} {3:0} {4:0} {5} {6} {7}", Math.Max(0, Math.Min(100, cpu)), usedGb, totalGb,
                        Math.Max(0, upK), Math.Max(0, downK), disk, uptimeMin, Uri.EscapeDataString(Environment.MachineName)));
                    if (checkLine.Length > 0) Send(checkLine);
                }
            }
            catch (Exception e) { Log.Write("tableau de bord : " + e.Message); }
        }

        void RunChecks()
        {
            var list = Checks != null ? Checks() : null;
            if (list == null || list.Count == 0) { checkLine = ""; return; }
            var parts = new List<string>();
            foreach (var c in list.Take(8))
            {
                string state, detail;
                try
                {
                    if (c.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        var rq = (HttpWebRequest)WebRequest.Create(c.Target); rq.Timeout = 4000; rq.Method = "GET"; rq.AllowAutoRedirect = true;
                        var t0 = DateTime.Now;
                        try { using (var rs = (HttpWebResponse)rq.GetResponse()) { state = (int)rs.StatusCode < 400 ? "ok" : "ko"; detail = (int)rs.StatusCode + " · " + (int)(DateTime.Now - t0).TotalMilliseconds + " ms"; } }
                        catch (WebException we) { var r = we.Response as HttpWebResponse; state = "ko"; detail = r != null ? ((int)r.StatusCode).ToString() : "injoignable"; }
                    }
                    else
                    {
                        using (var p = new Ping())
                        {
                            var r = p.Send(c.Target, 2000);
                            state = r.Status == IPStatus.Success ? "ok" : "ko";
                            detail = r.Status == IPStatus.Success ? r.RoundtripTime + " ms" : "pas de réponse";
                        }
                    }
                }
                catch { state = "ko"; detail = "erreur"; }
                parts.Add(Uri.EscapeDataString(c.Name + "|" + state + "|" + detail));
            }
            checkLine = "DBX " + string.Join(" ", parts);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
        [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    }

    static class EnvExt
    {
        [DllImport("kernel32.dll")] static extern ulong GetTickCount64();
        public static long TickCount64Safe() { return (long)GetTickCount64(); }
    }

    // ---- Fichiers en morceaux (trame type 8), dans les deux sens ----
    // [id 4 octets][drapeaux 1 octet : 1 = début, 2 = fin][si début : longueur du nom 2 octets + nom UTF-8][données]
    public class FileTransfer
    {
        public const byte FrameType = 8;
        const int Chunk = 256 * 1024;
        class Incoming { public FileStream Fs; public string Path; public Download Dl; }
        readonly Dictionary<uint, Incoming> incoming = new Dictionary<uint, Incoming>();
        public Action<string> Received;   // chemin d'un fichier reçu complet (envoi spontané de l'iPad)
        static int nextId = Environment.TickCount;

        // Téléchargement demandé par l'explorateur (« FS GET jeton chemin ») : l'iPad nomme ses trames « jeton|chemin »
        public class Download { public string Dir; public Action<long> Progress; public Action<string> FileDone; }
        public static readonly Dictionary<string, Download> Downloads = new Dictionary<string, Download>();

        public void Receive(byte[] f)
        {
            if (f.Length < 5) return;
            uint id = BitConverter.ToUInt32(f, 0); byte flags = f[4]; int off = 5;
            Incoming cur;
            lock (incoming)
            {
                if ((flags & 1) != 0)
                {
                    if (f.Length < 7) return;
                    int n = f[5] << 8 | f[6]; off = 7 + n;
                    if (f.Length < off) return;
                    var raw = Encoding.UTF8.GetString(f, 7, n);
                    string dir = Inbox.Dir, rel = raw; Download dl = null;
                    int bar = raw.IndexOf('|');
                    if (bar > 0)
                    {
                        lock (Downloads) Downloads.TryGetValue(raw.Substring(0, bar), out dl);
                        rel = raw.Substring(bar + 1);
                        if (dl != null) dir = dl.Dir;
                    }
                    // chemin relatif « dossier/sous-dossier/nom » : chaque morceau nettoyé, jamais de « .. »
                    var parts = rel.Split('/', '\\').Where(x => x.Length > 0 && x != "." && x != "..").Select(Clean).ToList();
                    if (dl == null && parts.Count > 1) parts = parts.Skip(parts.Count - 1).ToList();   // envoi spontané : nom seul
                    if (parts.Count == 0) parts.Add("fichier");
                    var folder = Path.Combine(new[] { dir }.Concat(parts.Take(parts.Count - 1)).ToArray());
                    Directory.CreateDirectory(folder);
                    var name = parts[parts.Count - 1];
                    var path = Path.Combine(folder, name);
                    for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
                    cur = new Incoming { Fs = new FileStream(path, FileMode.CreateNew), Path = path, Dl = dl };
                    incoming[id] = cur;
                }
                else if (!incoming.TryGetValue(id, out cur)) return;
                cur.Fs.Write(f, off, f.Length - off);
                if ((flags & 2) != 0) { cur.Fs.Dispose(); incoming.Remove(id); }
            }
            if (cur.Dl != null && cur.Dl.Progress != null) cur.Dl.Progress(f.Length - off);
            if ((flags & 2) != 0)
            {
                Log.Write("fichier reçu de l'iPad : " + cur.Path);
                if (cur.Dl != null) { if (cur.Dl.FileDone != null) cur.Dl.FileDone(cur.Path); }
                else if (Received != null) Received(cur.Path);
            }
        }

        static string Clean(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "_" : name;
        }

        // Envoie un fichier à l'iPad (dans un fil à part : la vidéo continue entre les morceaux).
        // remoteDir : dossier de destination dans « bSideDesk » sur l'iPad (« / », « /Projets »…), null = réception classique
        public static void Send(Session s, string path, string remoteDir = null, Action<long> progress = null)
        {
            uint id = (uint)Interlocked.Increment(ref nextId);
            var shown = Path.GetFileName(path);
            if (remoteDir != null) shown = "@/" + (remoteDir.Trim('/').Length > 0 ? remoteDir.Trim('/') + "/" : "") + shown;
            var name = Encoding.UTF8.GetBytes(shown);
            if (name.Length > 1000) name = name.Take(1000).ToArray();
            using (var fs = File.OpenRead(path))
            {
                var buf = new byte[Chunk]; bool first = true;
                while (!s.IsClosed)
                {
                    int n = fs.Read(buf, 0, buf.Length);
                    bool last = fs.Position >= fs.Length;
                    var ms = new MemoryStream();
                    ms.Write(BitConverter.GetBytes(id), 0, 4);
                    ms.WriteByte((byte)((first ? 1 : 0) | (last ? 2 : 0)));
                    if (first) { ms.WriteByte((byte)(name.Length >> 8)); ms.WriteByte((byte)name.Length); ms.Write(name, 0, name.Length); }
                    ms.Write(buf, 0, n);
                    var frame = ms.ToArray();
                    s.SendFrame(FrameType, frame, 0, frame.Length);
                    if (progress != null) progress(n);
                    first = false;
                    if (last) break;
                }
            }
            Log.Write("fichier envoyé à l'iPad : " + path + (remoteDir != null ? " → " + remoteDir : ""));
        }

        // Fichier ou dossier entier (sous-dossiers compris) vers un dossier de l'iPad
        public static void SendTree(Session s, string path, string remoteDir, Action<long> progress)
        {
            if (File.Exists(path)) { Send(s, path, remoteDir, progress); return; }
            if (!Directory.Exists(path)) return;
            var root = Path.GetDirectoryName(path.TrimEnd('\\'));
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (s.IsClosed) return;
                var relDir = Path.GetDirectoryName(f).Substring(root.Length).Replace('\\', '/');
                Send(s, f, remoteDir.TrimEnd('/') + "/" + relDir.Trim('/'), progress);
            }
        }

        public static long SizeOf(string path)
        {
            try
            {
                if (File.Exists(path)) return new FileInfo(path).Length;
                if (Directory.Exists(path)) return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { }
            return 0;
        }
    }

    // « Envoyer vers > iPad (bSideDesk) » dans l'Explorateur : une 2e instance dépose la demande dans ce dossier
    public static class Outbox
    {
        public static readonly string Dir = Path.Combine(Config.Dir, "outbox");
        public static void Queue(IEnumerable<string> files)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(Path.Combine(Dir, Guid.NewGuid().ToString("N") + ".txt"), files.Where(File.Exists).ToArray(), Encoding.UTF8);
        }
    }
}
