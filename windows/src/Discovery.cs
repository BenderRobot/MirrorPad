// Recherche de l'iPad : requête mDNS « _bsidedesk._tcp.local » envoyée sur chaque carte réseau.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace bSideDesk
{
    public static class Discovery
    {
        public static string FindIPad(int timeoutMs)
        {
            var q = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
            foreach (var label in new[] { "_bsidedesk", "_tcp", "local" }) { q.Add((byte)label.Length); q.AddRange(Encoding.ASCII.GetBytes(label)); }
            q.AddRange(new byte[] { 0, 0, 12, 0x80, 1 });   // PTR, classe IN, réponse directe
            var packet = q.ToArray();
            var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            var socks = new List<UdpClient>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    try { var u = new UdpClient(new IPEndPoint(ua.Address, 0)); u.Send(packet, packet.Length, group); socks.Add(u); }
                    catch { }
                }
            }
            string found = null;
            var end = DateTime.Now.AddMilliseconds(timeoutMs);
            while (found == null && DateTime.Now < end)
            {
                foreach (var u in socks)
                {
                    if (u.Available <= 0) continue;
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    try
                    {
                        var data = u.Receive(ref from);
                        if (Encoding.ASCII.GetString(data).Contains("_bsidedesk")) { found = from.Address.ToString(); break; }
                    }
                    catch { }
                }
                Thread.Sleep(30);
            }
            foreach (var u in socks) u.Close();
            return found;
        }
    }
}
