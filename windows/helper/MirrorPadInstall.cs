// MirrorPadInstall.exe : outil interne de MirrorPad-Setup.exe (jamais installé sur le PC), lancé en administrateur.
// Séparé de MirrorPad.exe pour que l'application elle-même ne contienne aucun code d'installation de pilote.
//   --setup-display <dossier>  pilote d'écran virtuel (Virtual Display Driver) + définitions iPad
//   --setup-vbcable            pilote micro VB-CABLE, téléchargé depuis vb-audio.com
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

[assembly: System.Reflection.AssemblyTitle("MirrorPad - installation des pilotes")]
[assembly: System.Reflection.AssemblyProduct("MirrorPad")]
[assembly: System.Reflection.AssemblyCompany("Benjamin Laurens")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Benjamin Laurens")]

namespace MirrorPadInstall
{
    static class Setup
    {
        static int Main(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("usage : MirrorPadInstall --setup-display <dossier> | --setup-vbcable"); return 2; }
            try
            {
                switch (args[0])
                {
                    case "--setup-display": return SetupDisplay(args.Length > 1 ? args[1] : null);
                    case "--setup-vbcable": return SetupVbCable();
                    case "--setup-usb": return SetupUsb();
                }
                Console.WriteLine("commande inconnue : " + args[0]);
                return 2;
            }
            catch (Exception e)
            {
                Console.WriteLine("échec : " + e);
                return 1;
            }
        }

        // ---------- écran virtuel ----------
        const string HwId = @"Root\MttVDD";
        static Guid DisplayClass = new Guid("4D36E968-E325-11CE-BFC1-08002BE10318");
        const string VddDir = @"C:\VirtualDisplayDriver";

        static int SetupDisplay(string dir)
        {
            if (!Environment.Is64BitProcess) throw new Exception("Windows 64 bits requis");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "MttVDD.inf"))) throw new Exception("fichiers du pilote introuvables");

            // 1. Définitions de l'iPad (paysage + portrait) dans la configuration du pilote
            bool xmlChanged = EnsureSettings(Path.Combine(dir, "vdd_settings.xml"));

            // 2. Pilote déjà présent : on le redémarre seulement si les définitions ont changé
            string instance = FindDevice();
            if (instance != null)
            {
                Console.WriteLine("écran virtuel déjà installé (" + instance + ")");
                if (xmlChanged) Exec("pnputil.exe", "/restart-device \"" + instance + "\"");
                return 0;
            }

            // 3. Certificat de l'éditeur du pilote (catalogue signé par le projet Virtual Display Driver)
            var cer = Path.Combine(dir, "Virtual_Display_Driver.cer");
            if (File.Exists(cer))
            {
                var cert = new X509Certificate2(cer);
                foreach (var name in new[] { StoreName.Root, StoreName.TrustedPublisher })
                    using (var st = new X509Store(name, StoreLocation.LocalMachine)) { st.Open(OpenFlags.ReadWrite); st.Add(cert); }
            }

            // 4. Périphérique racine « Root\MttVDD » puis installation du pilote dessus (équivalent de devcon install)
            CreateRootDevice();
            bool reboot;
            if (!UpdateDriverForPlugAndPlayDevices(IntPtr.Zero, HwId, Path.Combine(dir, "MttVDD.inf"), INSTALLFLAG_FORCE, out reboot))
                throw new Exception("installation du pilote refusée (code " + Marshal.GetLastWin32Error() + ")");
            Console.WriteLine("écran virtuel installé" + (reboot ? " (redémarrage conseillé)" : ""));
            return reboot ? 3010 : 0;
        }

        // Crée C:\VirtualDisplayDriver\vdd_settings.xml, ou y ajoute les définitions de l'iPad s'il existe déjà
        static bool EnsureSettings(string template)
        {
            Directory.CreateDirectory(VddDir);
            var path = Path.Combine(VddDir, "vdd_settings.xml");
            if (!File.Exists(path)) { File.Copy(template, path); return true; }
            var doc = new XmlDocument(); doc.Load(path);
            var res = doc.SelectSingleNode("/vdd_settings/resolutions");
            if (res == null) return false;
            bool changed = false;
            foreach (var wh in new[] { new[] { 2048, 1536 }, new[] { 1536, 2048 } })
            {
                bool has = res.SelectNodes("resolution").Cast<XmlNode>().Any(n =>
                    (n.SelectSingleNode("width") ?? n).InnerText.Trim() == wh[0].ToString() && (n.SelectSingleNode("height") ?? n).InnerText.Trim() == wh[1].ToString());
                if (has) continue;
                var r = doc.CreateElement("resolution");
                foreach (var kv in new[] { new[] { "width", wh[0].ToString() }, new[] { "height", wh[1].ToString() }, new[] { "refresh_rate", "30" }, new[] { "refresh_rate", "60" } })
                { var e = doc.CreateElement(kv[0]); e.InnerText = kv[1]; r.AppendChild(e); }
                res.AppendChild(r); changed = true;
            }
            if (changed) doc.Save(path);
            return changed;
        }

        static string FindDevice()
        {
            IntPtr set = SetupDiGetClassDevs(ref DisplayClass, null, IntPtr.Zero, DIGCF_PRESENT);
            if (set == INVALID) return null;
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
                {
                    var buf = new byte[2048]; uint type, need;
                    if (!SetupDiGetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, out type, buf, (uint)buf.Length, out need)) continue;
                    var ids = Encoding.Unicode.GetString(buf, 0, (int)need).Split('\0');
                    if (!ids.Any(x => x.Equals(HwId, StringComparison.OrdinalIgnoreCase))) continue;
                    var sb = new StringBuilder(512);
                    return SetupDiGetDeviceInstanceId(set, ref d, sb, sb.Capacity, out need) ? sb.ToString() : HwId;
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return null;
        }

        static void CreateRootDevice()
        {
            IntPtr set = SetupDiCreateDeviceInfoList(ref DisplayClass, IntPtr.Zero);
            if (set == INVALID) throw new Exception("SetupDiCreateDeviceInfoList (" + Marshal.GetLastWin32Error() + ")");
            try
            {
                var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
                if (!SetupDiCreateDeviceInfo(set, "Display", ref DisplayClass, null, IntPtr.Zero, DICD_GENERATE_ID, ref d))
                    throw new Exception("SetupDiCreateDeviceInfo (" + Marshal.GetLastWin32Error() + ")");
                var ids = Encoding.Unicode.GetBytes(HwId + "\0\0");
                if (!SetupDiSetDeviceRegistryProperty(set, ref d, SPDRP_HARDWAREID, ids, (uint)ids.Length))
                    throw new Exception("SetupDiSetDeviceRegistryProperty (" + Marshal.GetLastWin32Error() + ")");
                if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                    throw new Exception("DIF_REGISTERDEVICE (" + Marshal.GetLastWin32Error() + ")");
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        // ---------- câble USB : service « Apple Mobile Device » ----------
        // On n'installe pas iTunes : on télécharge l'installateur officiel d'Apple, on en extrait
        // uniquement « AppleMobileDeviceSupport64.msi » (le service USB) et on l'installe en silence.
        static bool UsbServicePresent() { return Exec("sc.exe", "query \"Apple Mobile Device Service\"") == 0; }

        static int SetupUsb()
        {
            if (UsbServicePresent()) { Console.WriteLine("service Apple déjà installé"); return 0; }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var tmp = Path.Combine(Path.GetTempPath(), "mirrorpad-usb");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            var setup = Path.Combine(tmp, "iTunes64Setup.exe");
            Console.WriteLine("téléchargement de l'installateur Apple (environ 200 Mo)...");
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                wc.DownloadFile("https://www.apple.com/itunes/download/win64", setup);
            }
            var sig = new X509Certificate2(X509Certificate.CreateFromSignedFile(setup));
            if (sig.Subject.IndexOf("Apple Inc", StringComparison.OrdinalIgnoreCase) < 0) throw new Exception("installateur non signé par Apple : " + sig.Subject);
            Console.WriteLine("extraction du service USB...");
            using (var p = Process.Start(new ProcessStartInfo(setup, "/extract") { WorkingDirectory = tmp, UseShellExecute = false, CreateNoWindow = true }))
                if (!p.WaitForExit(300000)) { try { p.Kill(); } catch { } }
            var msi = Directory.GetFiles(tmp, "AppleMobileDeviceSupport64.msi", SearchOption.AllDirectories).FirstOrDefault();
            if (msi == null) throw new Exception("AppleMobileDeviceSupport64.msi introuvable dans l'installateur Apple");
            Console.WriteLine("installation de " + Path.GetFileName(msi) + "...");
            var code = Exec("msiexec.exe", "/i \"" + msi + "\" /qn /norestart", 300000);
            Console.WriteLine("msiexec : code " + code);
            try { Directory.Delete(tmp, true); } catch { }
            if (!UsbServicePresent()) throw new Exception("le service Apple n'a pas été installé (msiexec " + code + ")");
            return code == 3010 ? 3010 : 0;
        }

        // ---------- micro : VB-CABLE ----------
        static int SetupVbCable()
        {
            if (FindCable()) { Console.WriteLine("VB-CABLE déjà installé"); return 0; }
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var tmp = Path.Combine(Path.GetTempPath(), "mirrorpad-vbcable");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            Directory.CreateDirectory(tmp);
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "MirrorPad-Setup";
                string url = null;
                try
                {
                    var page = wc.DownloadString("https://vb-audio.com/Cable/");
                    var m = Regex.Match(page, "href=\"([^\"]*VBCABLE_Driver_Pack\\d+\\.zip)\"", RegexOptions.IgnoreCase);
                    if (m.Success) url = new Uri(new Uri("https://vb-audio.com/Cable/"), m.Groups[1].Value).AbsoluteUri;
                }
                catch { }
                if (url == null) url = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip";
                Console.WriteLine("téléchargement : " + url);
                var zip = Path.Combine(tmp, "vbcable.zip");
                wc.DownloadFile(url, zip);
                ZipFile.ExtractToDirectory(zip, Path.Combine(tmp, "x"));
            }
            var setup = Directory.GetFiles(Path.Combine(tmp, "x"), "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (setup == null) throw new Exception("VBCABLE_Setup_x64.exe absent de l'archive");
            var code = Exec(setup, "-i -h", 180000);
            Console.WriteLine("VB-CABLE : code " + code);
            return 0;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WAVEOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwFormats; public ushort wChannels, wReserved1; public uint dwSupport; }
        [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int waveOutGetDevCaps(UIntPtr id, ref WAVEOUTCAPS caps, int size);
        static bool FindCable()
        {
            uint n = waveOutGetNumDevs();
            for (uint i = 0; i < n; i++)
            {
                var c = new WAVEOUTCAPS();
                if (waveOutGetDevCaps(new UIntPtr(i), ref c, Marshal.SizeOf(typeof(WAVEOUTCAPS))) == 0 && c.szPname != null && c.szPname.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static int Exec(string file, string args, int timeout = 60000)
        {
            using (var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (!p.WaitForExit(timeout)) { try { p.Kill(); } catch { } return -1; }
                return p.ExitCode;
            }
        }

        // ---------- SetupAPI ----------
        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        static readonly IntPtr INVALID = new IntPtr(-1);
        const uint DIGCF_PRESENT = 0x2, DICD_GENERATE_ID = 0x1, SPDRP_HARDWAREID = 0x1, DIF_REGISTERDEVICE = 0x19, INSTALLFLAG_FORCE = 0x1;

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid cls, string enumerator, IntPtr hwnd, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid cls, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiCreateDeviceInfo(IntPtr set, string name, ref Guid cls, string desc, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, out uint type, byte[] buf, uint size, out uint need);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiSetDeviceRegistryProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint prop, byte[] buf, uint size);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SP_DEVINFO_DATA data, StringBuilder id, int size, out uint need);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiCallClassInstaller(uint fn, IntPtr set, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool UpdateDriverForPlugAndPlayDevices(IntPtr hwnd, string hwid, string inf, uint flags, out bool reboot);
    }
}
