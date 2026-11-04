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
