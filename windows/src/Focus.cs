// Détection des zones de saisie : quand une zone de texte de l'écran de l'iPad reçoit le focus
// (clic dans un champ, la barre d'adresse, un document…), on demande à l'iPad d'ouvrir son clavier.
// Source : UI Automation de Windows (comme les lecteurs d'écran), interrogée 4 fois par seconde.
using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace MirrorPad
{
    public class FocusWatcher
    {
        public Func<Rectangle> ScreenBounds;   // écran de l'iPad (pixels)
        public Action<string> Send;            // « KB 1 x y w h » (zone normalisée) / « KB 0 »
        Thread thread; volatile bool running; volatile int gen;
        string lastKey = ""; bool shown;

        public void Start()
        {
            if (running) return;
            running = true;
            int g = ++gen;
            thread = new Thread(() => Loop(g)) { IsBackground = true, Name = "saisie" };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
        public void Stop() { running = false; lastKey = ""; shown = false; }

        void Loop(int g)
        {
            IUIAutomation uia = null;
            try { uia = (IUIAutomation)new CUIAutomation(); }
            catch (Exception e) { Log.Write("détection des zones de saisie indisponible : " + e.Message); running = false; return; }
            while (running && g == gen)
            {
                try { Check(uia); } catch { }
                Thread.Sleep(250);
            }
        }

        const int ControlType = 30003, Bounds = 30001, ValueReadOnly = 30046, ValueAvailable = 30043, TextAvailable = 30040, HasFocus = 30008;
        const int Edit = 50004, Document = 50030, ComboBox = 50003;

        void Check(IUIAutomation uia)
        {
            IUIAutomationElement el;
            if (uia.GetFocusedElement(out el) != 0 || el == null) { Hide(); return; }
            try
            {
                int type = Int(Prop(el, ControlType));
                bool editable;
                if (type == Edit || type == ComboBox) editable = !Bool(Prop(el, ValueReadOnly));
                else if (type == Document) editable = Bool(Prop(el, ValueAvailable)) && !Bool(Prop(el, ValueReadOnly));
                else editable = false;
                if (type == ComboBox && !Bool(Prop(el, ValueAvailable))) editable = false;   // liste déroulante simple
                var r = Prop(el, Bounds) as double[];
                if (!editable || r == null || r.Length < 4) { Hide(); return; }

                var b = ScreenBounds != null ? ScreenBounds() : Rectangle.Empty;
                double cx = r[0] + r[2] / 2, cy = r[1] + r[3] / 2;
                if (b.Width <= 0 || cx < b.Left || cx >= b.Right || cy < b.Top || cy >= b.Bottom) { Hide(); return; }   // pas sur l'iPad

                // nouvelle zone (ou zone déplacée) : on montre le clavier une seule fois, l'utilisateur peut le refermer
                string key = type + ":" + (int)r[0] + ":" + (int)r[1] + ":" + (int)r[2] + ":" + (int)r[3];
                if (key == lastKey) return;
                lastKey = key; shown = true;
                Func<double, string> f = v => Math.Max(0, Math.Min(1, v)).ToString("0.0000", CultureInfo.InvariantCulture);
                if (Send != null) Send("KB 1 " + f((r[0] - b.Left) / b.Width) + " " + f((r[1] - b.Top) / b.Height) + " " + f(r[2] / b.Width) + " " + f(r[3] / b.Height));
            }
            finally { Marshal.ReleaseComObject(el); }
        }

        void Hide()
        {
            lastKey = "";
            if (!shown) return;
            shown = false;
            if (Send != null) Send("KB 0");
        }

        static object Prop(IUIAutomationElement el, int id)
        {
            object v;
            return el.GetCurrentPropertyValue(id, out v) == 0 ? v : null;
        }
        static int Int(object v) { return v is int ? (int)v : 0; }
        static bool Bool(object v) { return v is bool && (bool)v; }

        // ---- UI Automation (COM) : seules les méthodes utilisées sont déclarées, dans l'ordre exact de la vtable ----
        [ComImport, Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E")] class CUIAutomation { }

        [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomation
        {
            [PreserveSig] int CompareElements(IntPtr a, IntPtr b, out int same);
            [PreserveSig] int CompareRuntimeIds(IntPtr a, IntPtr b, out int same);
            [PreserveSig] int GetRootElement(out IntPtr root);
            [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IntPtr el);
            [PreserveSig] int ElementFromPoint(long pt, out IntPtr el);
            [PreserveSig] int GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IUIAutomationElement el);
        }

        [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IUIAutomationElement
        {
            [PreserveSig] int SetFocus();
            [PreserveSig] int GetRuntimeId(out IntPtr ids);
            [PreserveSig] int FindFirst(int scope, IntPtr cond, out IntPtr found);
            [PreserveSig] int FindAll(int scope, IntPtr cond, out IntPtr found);
            [PreserveSig] int FindFirstBuildCache(int scope, IntPtr cond, IntPtr req, out IntPtr found);
            [PreserveSig] int FindAllBuildCache(int scope, IntPtr cond, IntPtr req, out IntPtr found);
            [PreserveSig] int BuildUpdatedCache(IntPtr req, out IntPtr el);
            [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        }
    }
}
