using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace bSideDesk
{
    // ---- Fichiers en morceaux (trame type 8), dans les deux sens ----
    // [id 4 octets][drapeaux 1 octet : 1 = début, 2 = fin][si début : longueur du nom 2 octets + nom UTF-8][données]
    public class FileTransfer
    {
        public const byte FrameType = 8;
        const int Chunk = 256 * 1024;
        class Incoming { public FileStream Fs; public string Path, Temporary; public Download Dl; }
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
                    Cancel(id);
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
                    var temporary = Path.Combine(folder, ".bsidedesk-" + Guid.NewGuid().ToString("N") + ".part");
                    cur = new Incoming { Fs = new FileStream(temporary, FileMode.CreateNew), Path = path, Temporary = temporary, Dl = dl };
                    incoming[id] = cur;
                }
                else if (!incoming.TryGetValue(id, out cur)) return;
                try
                {
                    cur.Fs.Write(f, off, f.Length - off);
                    if ((flags & 2) != 0)
                    {
                        cur.Fs.Dispose();
                        File.Move(cur.Temporary, cur.Path);
                        incoming.Remove(id);
                    }
                }
                catch { Cancel(id); throw; }
            }
            if (cur.Dl != null && cur.Dl.Progress != null) cur.Dl.Progress(f.Length - off);
            if ((flags & 2) != 0)
            {
                Log.Write("fichier reçu de l'iPad : " + cur.Path);
                if (cur.Dl != null) { if (cur.Dl.FileDone != null) cur.Dl.FileDone(cur.Path); }
                else if (Received != null) Received(cur.Path);
            }
        }

        void Cancel(uint id)
        {
            Incoming cur;
            if (!incoming.TryGetValue(id, out cur)) return;
            incoming.Remove(id);
            try { cur.Fs.Dispose(); }
            finally { try { File.Delete(cur.Temporary); } catch (Exception e) { Log.Write("nettoyage du transfert : " + e.Message); } }
        }

        public void CancelAll()
        {
            lock (incoming)
                foreach (var id in incoming.Keys.ToArray()) Cancel(id);
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

}
