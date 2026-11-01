// Session chiffrée avec l'iPad (un seul port TCP, 7272). C'est le PC qui se connecte, mais il joue
// le rôle de serveur TLS : l'iPad vérifie le certificat du PC, le PC vérifie le jeton de l'iPad.
//
// Trames (dans TLS) : [type 1 octet][longueur 3 octets][données]   type 1 = ligne de contrôle, 2 = vidéo
// Poignée de main :
//   PC   -> HELLO 2 <idPC> <nomPC>
//   iPad -> AUTH <idIPad> <jeton>                       (iPad déjà appairé)
//        |  PAIR <idIPad> <nomIPad> <nonce>             (première fois : code à 6 chiffres sur les deux écrans)
//   PC   -> OK  |  PAIRED <jeton>  |  ERR <raison>
using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;

namespace MirrorPad
{
    public class Session
    {
        public const byte Control = 1, Video = 2, Camera = 3, ClipImage = 4, File = 5, Audio = 6;

        TcpClient tcp;
        SslStream ssl;
        readonly object writeLock = new object();
        volatile bool closed;

        public string Host { get; private set; }
        public string IPadId { get; private set; }
        public string IPadName { get; private set; }
        public bool Authenticated { get; private set; }

        public event Action<string> LineReceived;      // fil de lecture de la session
        public event Action<byte, byte[]> BinaryReceived;   // webcam, micro, image copiée, fichier
        public event Action<Session> Closed;

        // Décide de l'appairage : (nom iPad, code) -> accepté ? (bloquant, appelé depuis le fil de la session)
        public Func<string, string, bool> AskPairing;
        public Config Config;

        public static Session Connect(string host, Config cfg, int timeoutMs)
        {
            var s = new Session { Host = host, Config = cfg };
            s.tcp = new TcpClient { NoDelay = true };
            var ar = s.tcp.BeginConnect(host, Config.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) { s.tcp.Close(); throw new IOException("iPad injoignable"); }
            s.tcp.EndConnect(ar);
            s.tcp.ReceiveTimeout = 15000;   // l'iPad envoie une mesure de latence chaque seconde
            s.ssl = new SslStream(s.tcp.GetStream(), false);
            s.ssl.AuthenticateAsServer(Identity.Certificate, false, SslProtocols.Tls12, false);
            return s;
        }

        // Poignée de main + appairage ; renvoie true si l'iPad est authentifié
        public bool Handshake()
        {
            Send("HELLO 2 " + Config.PcId + " " + Uri.EscapeDataString(Environment.MachineName));
            byte type; byte[] data;
            while (ReadFrame(out type, out data))
            {
                if (type != Control) continue;
                var p = Encoding.UTF8.GetString(data).Split(' ');
                if (p[0] == "AUTH" && p.Length >= 3)
                {
                    var dev = Config.FindPaired(p[1]);
                    if (dev != null && Identity.SafeEquals(dev.Token, p[2]))
                    {
                        IPadId = dev.Id; IPadName = dev.Name; Authenticated = true;
                        Send("OK");
                        Log.Write("iPad authentifié : " + IPadName + " (" + Host + ")");
                        return true;
                    }
                    Log.Write("jeton refusé pour l'appareil " + p[1] + " (" + Host + ")");
                    Send("ERR AUTH");
                    return false;
                }
                if (p[0] == "PAIR" && p.Length >= 4)
                {
                    var nonce = Identity.FromHex(p[3]);
                    if (nonce == null || nonce.Length < 16) { Send("ERR PROTOCOL"); return false; }
                    var name = Uri.UnescapeDataString(p[2]);
                    var code = Identity.PairingCode(nonce);
                    Log.Write("demande d'appairage : " + name + " (" + Host + "), code " + code);
                    bool ok = AskPairing != null && AskPairing(name, code);
                    if (!ok) { Send("ERR REFUSED"); return false; }
                    var dev = new PairedDevice { Id = p[1], Name = name, Token = Identity.RandomHex(32), Added = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
                    lock (Config.Paired) { Config.Paired.RemoveAll(d => d.Id == dev.Id); Config.Paired.Add(dev); }
                    Config.Save();
                    IPadId = dev.Id; IPadName = dev.Name; Authenticated = true;
                    Send("PAIRED " + dev.Token);
                    Log.Write("iPad appairé : " + name);
                    return true;
                }
                if (p[0] == "ERR") { Log.Write("l'iPad refuse la connexion : " + string.Join(" ", p)); return false; }
            }
            return false;
        }

        // Boucle de lecture (à lancer dans un fil dédié après Handshake)
        public void ReadLoop()
        {
            try
            {
                byte type; byte[] data;
                while (ReadFrame(out type, out data))
                {
                    if (type == Control) { if (LineReceived != null) LineReceived(Encoding.UTF8.GetString(data)); }
                    else if (BinaryReceived != null) BinaryReceived(type, data);
                }
            }
            catch (Exception e) { if (!closed) Log.Write("session interrompue : " + e.Message); }
            Close();
        }

        bool ReadFrame(out byte type, out byte[] data)
        {
            type = 0; data = null;
            var h = new byte[4];
            if (!ReadExact(h, 4)) return false;
            int len = h[1] << 16 | h[2] << 8 | h[3];
            data = new byte[len];
            if (len > 0 && !ReadExact(data, len)) return false;
            type = h[0];
            return true;
        }

        bool ReadExact(byte[] b, int n)
        {
            int off = 0;
            while (off < n) { int r = ssl.Read(b, off, n - off); if (r <= 0) return false; off += r; }
            return true;
        }

        public void Send(string line) { var b = Encoding.UTF8.GetBytes(line); SendFrame(Control, b, 0, b.Length); }

        public void SendFrame(byte type, byte[] b, int off, int n)
        {
            if (closed) return;
            var h = new byte[] { type, (byte)(n >> 16), (byte)(n >> 8), (byte)n };
            try
            {
                lock (writeLock) { ssl.Write(h, 0, 4); if (n > 0) ssl.Write(b, off, n); ssl.Flush(); }
            }
            catch (Exception e) { if (!closed) Log.Write("envoi impossible : " + e.Message); Close(); }
        }

        public bool IsClosed { get { return closed; } }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try { if (ssl != null) ssl.Close(); } catch { }
            try { if (tcp != null) tcp.Close(); } catch { }
            var h = Closed; if (h != null) h(this);
        }
    }
}
