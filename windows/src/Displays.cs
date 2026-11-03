// Identification de l'écran virtuel de bSideDesk parmi tous les écrans du PC (écran du portable, écrans externes…).
// On le reconnaît à son pilote (Virtual Display Driver, moniteur « MTT1337 ») et jamais à sa position ou à son rang :
// brancher un écran externe change les numéros \\.\DISPLAYn et l'ordre des sorties de capture.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace bSideDesk
{
    public static class Displays
    {
        public class Info { public string Name, Adapter, AdapterId, MonitorId; public bool Attached, Primary, Virtual; }

        // tous les écrans connus de Windows, branchés au bureau ou non
        public static List<Info> All()
        {
            var list = new List<Info>();
            for (uint i = 0; ; i++)
            {
                var a = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                if (!EnumDisplayDevices(null, i, ref a, 0)) break;
                if ((a.StateFlags & MIRRORING) != 0) continue;
                var m = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                string mon = EnumDisplayDevices(a.DeviceName, 0, ref m, 0) ? m.DeviceID : "";
                var inf = new Info
                {
                    Name = a.DeviceName, Adapter = a.DeviceString ?? "", AdapterId = a.DeviceID ?? "", MonitorId = mon ?? "",
                    Attached = (a.StateFlags & ATTACHED) != 0, Primary = (a.StateFlags & PRIMARY) != 0
                };
                inf.Virtual = Has(inf.Adapter, "Virtual Display") || Has(inf.AdapterId, "MttVDD") || Has(inf.MonitorId, "MTT1337") || Has(inf.MonitorId, "MttVDD");
                if (string.IsNullOrEmpty(mon) && !inf.Attached && !inf.Virtual) continue;   // sortie vide d'une carte graphique
                list.Add(inf);
            }
            return list;
        }

        static bool Has(string s, string what) { return s != null && s.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0; }

        // nom \\.\DISPLAYn de l'écran virtuel (null si le pilote n'est pas installé) ; attached = présent sur le bureau
        public static string Virtual(out bool attached)
        {
            attached = false;
            Info best = null;
            foreach (var d in All()) if (d.Virtual && (best == null || (d.Attached && !best.Attached))) best = d;
            if (best == null) return null;
            attached = best.Attached;
            return best.Name;
        }

        public static bool IsVirtual(string device)
        {
            if (string.IsNullOrEmpty(device)) return false;
            bool any = false;
            foreach (var d in All())
            {
                if (!d.Virtual) continue;
                any = true;
                if (string.Equals(d.Name, device, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return !any;   // pilote non reconnu : ancien comportement (on fait confiance à l'écran choisi)
        }

        public static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var d in All())
                sb.Append(" | ").Append(d.Name.Replace(@"\\.\", "")).Append(d.Primary ? " principal" : "").Append(d.Attached ? "" : " (débranché)")
                  .Append(d.Virtual ? " [bSideDesk]" : "").Append(" « ").Append(d.Adapter).Append(" » ").Append(d.MonitorId.Split('\\').Length > 1 ? d.MonitorId.Split('\\')[1] : d.MonitorId);
            return sb.Length > 3 ? sb.ToString(3, sb.Length - 3) : "aucun";
        }

        // Rang de l'écran parmi les sorties de la carte graphique principale : c'est le numéro attendu par
        // ffmpeg ddagrab (output_idx). -1 si introuvable.
        public static int CaptureIndex(string device)
        {
            if (string.IsNullOrEmpty(device)) return -1;
            IDXGIFactory1 f = null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out f) != 0 || f == null) return -1;
                for (uint a = 0; a < 8; a++)
                {
                    IDXGIAdapter ad;
                    if (f.EnumAdapters(a, out ad) != 0 || ad == null) break;
                    try
                    {
                        for (uint o = 0; o < 16; o++)
                        {
                            IDXGIOutput op;
                            if (ad.EnumOutputs(o, out op) != 0 || op == null) break;
                            try
                            {
                                DXGI_OUTPUT_DESC desc;
                                if (op.GetDesc(out desc) == 0 && string.Equals(desc.DeviceName, device, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (a != 0) Log.Write("écran de l'iPad sur la carte graphique n°" + a + " : capture peut-être impossible");
                                    return (int)o;
                                }
                            }
                            finally { Marshal.ReleaseComObject(op); }
                        }
                    }
                    finally { Marshal.ReleaseComObject(ad); }
                }
            }
            catch { }
            finally { if (f != null) Marshal.ReleaseComObject(f); }
            return -1;
        }

        // ---- user32 ----
        const int ATTACHED = 0x1, PRIMARY = 0x4, MIRRORING = 0x8;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);

        // ---- DXGI (seulement les méthodes utiles ; les autres réservent leur place dans la table virtuelle) ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom, AttachedToDesktop, Rotation;
            public IntPtr Monitor;
        }
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int EnumAdapters(uint index, out IDXGIAdapter adapter);
        }
        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
        }
        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput
        {
            void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }
        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IDXGIFactory1 factory);
    }
}
