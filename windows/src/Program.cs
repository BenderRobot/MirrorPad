using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("bSideDesk")]
[assembly: System.Reflection.AssemblyProduct("bSideDesk")]
[assembly: System.Reflection.AssemblyDescription("iPad en écran supplémentaire tactile")]
[assembly: System.Reflection.AssemblyCompany("Benjamin Laurens")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Benjamin Laurens")]

namespace bSideDesk
{
    static class Program
    {
        static Mutex mutex;
        public static bool Migrated, ReEnableAdmin;   // premier lancement après le renommage MirrorPad -> bSideDesk

        [STAThread]
        static void Main(string[] args)
        {
            // « Envoyer vers > iPad (bSideDesk) » : on dépose la demande pour l'instance déjà lancée, et on s'arrête
            if (args.Length > 0 && args[0] == "--send") { try { Outbox.Queue(args.Skip(1)); } catch { } return; }
            mutex = new Mutex(false, @"Local\bSideDeskSender");
            if (!mutex.WaitOne(0))
            {
                MessageBox.Show("bSideDesk est déjà lancé (icône près de l'horloge).", "bSideDesk");
                return;
            }
            // l'ancienne app (MirrorPad) ne doit plus tourner en même temps
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("MirrorPad")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            bool wasAdmin = false;
            try { Migrated = Config.MigrateFromMirrorPad(out wasAdmin); } catch { }
            ReEnableAdmin = wasAdmin;
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }   // pixels réels sur chaque écran
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) => Log.Write("erreur : " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Write("erreur fatale : " + e.ExceptionObject);
            Application.Run(new App());
        }

        public static void ReleaseMutex()
        {
            try { mutex.ReleaseMutex(); mutex.Dispose(); } catch { }
        }
    }
}
