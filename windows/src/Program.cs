using System;
using System.Net;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("MirrorPad")]
[assembly: System.Reflection.AssemblyProduct("MirrorPad")]
[assembly: System.Reflection.AssemblyDescription("iPad en écran supplémentaire tactile")]
[assembly: System.Reflection.AssemblyCompany("Benjamin Laurens")]
[assembly: System.Reflection.AssemblyCopyright("© 2026 Benjamin Laurens")]

namespace MirrorPad
{
    static class Program
    {
        static Mutex mutex;

        [STAThread]
        static void Main()
        {
            mutex = new Mutex(false, @"Local\MirrorPadSender");
            if (!mutex.WaitOne(0))
            {
                MessageBox.Show("MirrorPad est déjà lancé (icône près de l'horloge).", "MirrorPad");
                return;
            }
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
