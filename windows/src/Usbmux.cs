// Connexion par câble USB : le service « Apple Mobile Device » (installé avec iTunes ou l'app Apple Devices)
// écoute sur 127.0.0.1:27015 et sait ouvrir un tunnel TCP vers un port de l'iPad branché (protocole usbmuxd).
// On lui demande la liste des appareils, puis un tunnel vers le port 7272 de bSideDesk : la suite (TLS, appairage)
// est identique au Wi-Fi, mais sans réseau, avec une latence quasi nulle et l'iPad qui charge.
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace bSideDesk
{
    public static class Usbmux
    {
        const int MuxPort = 27015;
        static int tag;

        // Renvoie un tunnel TCP déjà relié au port de l'iPad, ou null (pas de service Apple, pas d'iPad branché…)
        public static TcpClient Connect(int port, int timeoutMs)
        {
            int? device = FindDevice(timeoutMs);
            if (device == null) return null;
            var c = Open(timeoutMs);
            if (c == null) return null;
            try
            {
                int swapped = ((port & 0xFF) << 8) | ((port >> 8) & 0xFF);   // le port est attendu en ordre réseau
                Send(c, "<key>MessageType</key><string>Connect</string><key>DeviceID</key><integer>" + device.Value +
                        "</integer><key>PortNumber</key><integer>" + swapped + "</integer>");
                var reply = Receive(c);
                var m = Regex.Match(reply ?? "", @"<key>Number</key>\s*<integer>(\d+)</integer>");
                if (m.Success && m.Groups[1].Value == "0") { c.ReceiveTimeout = 0; return c; }   // tunnel ouvert
            }
            catch { }
            c.Close();
            return null;
        }

        static int? FindDevice(int timeoutMs)
        {
            var c = Open(timeoutMs);
            if (c == null) return null;
            try
            {
                Send(c, "<key>MessageType</key><string>ListDevices</string>");
                var reply = Receive(c) ?? "";
                // premier appareil relié en USB (pas en Wi-Fi)
                foreach (Match d in Regex.Matches(reply, @"<dict>\s*<key>DeviceID</key>\s*<integer>(\d+)</integer>.*?</dict>\s*</dict>", RegexOptions.Singleline))
                    if (d.Value.IndexOf("<string>USB</string>", StringComparison.Ordinal) >= 0) return int.Parse(d.Groups[1].Value);
                var any = Regex.Match(reply, @"<key>DeviceID</key>\s*<integer>(\d+)</integer>");
                return any.Success && reply.IndexOf("<string>USB</string>", StringComparison.Ordinal) >= 0 ? int.Parse(any.Groups[1].Value) : (int?)null;
            }
            catch { return null; }
            finally { c.Close(); }
        }

        static TcpClient Open(int timeoutMs)
        {
            var c = new TcpClient { NoDelay = true };
            try
            {
                var ar = c.BeginConnect("127.0.0.1", MuxPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(Math.Min(timeoutMs, 1000))) { c.Close(); return null; }
                c.EndConnect(ar);
                c.ReceiveTimeout = timeoutMs;
                return c;
            }
            catch { c.Close(); return null; }
        }

        // Message usbmuxd : [longueur][version 1][type 8 = plist][étiquette] (entiers 32 bits petit-boutistes) + plist XML
        static void Send(TcpClient c, string dictBody)
        {
            var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
                      "<plist version=\"1.0\"><dict>" + dictBody +
                      "<key>ClientVersionString</key><string>bSideDesk</string><key>ProgName</key><string>bSideDesk</string></dict></plist>\n";
            var body = Encoding.UTF8.GetBytes(xml);
            var msg = new byte[16 + body.Length];
            BitConverter.GetBytes(msg.Length).CopyTo(msg, 0);
            BitConverter.GetBytes(1).CopyTo(msg, 4);
            BitConverter.GetBytes(8).CopyTo(msg, 8);
            BitConverter.GetBytes(++tag).CopyTo(msg, 12);
            Array.Copy(body, 0, msg, 16, body.Length);
            c.GetStream().Write(msg, 0, msg.Length);
        }

        static string Receive(TcpClient c)
        {
            var s = c.GetStream();
            var h = ReadExact(s, 16);
            if (h == null) return null;
            int len = BitConverter.ToInt32(h, 0);
            if (len < 16 || len > 1 << 20) return null;
            var b = ReadExact(s, len - 16);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        static byte[] ReadExact(NetworkStream s, int n)
        {
            var b = new byte[n]; int off = 0;
            while (off < n) { int r = s.Read(b, off, n - off); if (r <= 0) return null; off += r; }
            return b;
        }
    }
}
