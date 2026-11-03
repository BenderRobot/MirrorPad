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

        // Rebranche l'écran virtuel au bureau en activant son « chemin » d'affichage (API CCD de Windows), sans toucher
        // aux autres écrans : c'est ce que fait Paramètres > Affichage > « Étendre le bureau à cet écran ».
        // 0 = fait, sinon code d'erreur Windows.
        public static int Enable(string device)
        {
            uint np, nm;
            int r = GetDisplayConfigBufferSizes(QDC_ALL_PATHS, out np, out nm);
            if (r != 0) return r;
            var paths = new PATH[np]; var modes = new MODE[nm];
            r = QueryDisplayConfig(QDC_ALL_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r != 0) return r;
            int pick = -1;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                if (p.targetAvailable == 0) continue;
                bool isVdd = TargetPath(p).IndexOf("MTT1337", StringComparison.OrdinalIgnoreCase) >= 0
                          || string.Equals(SourceName(p), device, StringComparison.OrdinalIgnoreCase);
                if (!isVdd) continue;
                if ((p.flags & PATH_ACTIVE) != 0) return 0;   // déjà branché
                // source libre (pas déjà utilisée par un écran actif de la même carte), sinon ce serait un écran dupliqué
                bool used = false;
                for (int j = 0; j < np; j++)
                    if ((paths[j].flags & PATH_ACTIVE) != 0 && paths[j].sourceId == p.sourceId && paths[j].srcLow == p.srcLow && paths[j].srcHigh == p.srcHigh) used = true;
                if (used) continue;
                if (pick < 0 || string.Equals(SourceName(p), device, StringComparison.OrdinalIgnoreCase)) pick = i;
            }
            if (pick < 0) return -404;
            var list = new System.Collections.Generic.List<PATH>();
            for (int i = 0; i < np; i++) if ((paths[i].flags & PATH_ACTIVE) != 0) list.Add(paths[i]);
            var v = paths[pick];
            v.flags |= PATH_ACTIVE; v.sourceModeIdx = INVALID; v.targetModeIdx = INVALID;
            list.Add(v);
            var arr = list.ToArray();
            return SetDisplayConfig((uint)arr.Length, arr, nm, modes, SDC_APPLY | SDC_USE_SUPPLIED | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        }

        static string SourceName(PATH p)
        {
            var d = new SOURCE_NAME { type = 1, size = Marshal.SizeOf(typeof(SOURCE_NAME)), low = p.srcLow, high = p.srcHigh, id = p.sourceId };
            return DisplayConfigGetDeviceInfo(ref d) == 0 ? d.gdiName : "";
        }
        static string TargetPath(PATH p)
        {
            var d = new TARGET_NAME { type = 2, size = Marshal.SizeOf(typeof(TARGET_NAME)), low = p.tgtLow, high = p.tgtHigh, id = p.targetId };
            return DisplayConfigGetDeviceInfo(ref d) == 0 ? (d.devicePath ?? "") : "";
        }

        const uint QDC_ALL_PATHS = 1, PATH_ACTIVE = 1, INVALID = 0xffffffff;
        const uint SDC_APPLY = 0x80, SDC_USE_SUPPLIED = 0x20, SDC_SAVE_TO_DATABASE = 0x200, SDC_ALLOW_CHANGES = 0x400;
        [StructLayout(LayoutKind.Sequential)]
        struct PATH
        {
            public uint srcLow; public int srcHigh; public uint sourceId, sourceModeIdx, sourceStatus;
            public uint tgtLow; public int tgtHigh; public uint targetId, targetModeIdx, outputTechnology, rotation, scaling, refreshNum, refreshDen, scanLine;
            public int targetAvailable; public uint targetStatus;
            public uint flags;
        }
        [StructLayout(LayoutKind.Sequential, Size = 64)] struct MODE { public uint infoType, id; public uint low; public int high; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SOURCE_NAME
        {
            public int type, size; public uint low; public int high; public uint id;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string gdiName;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_NAME
        {
            public int type, size; public uint low; public int high; public uint id;
            public uint flags, outputTechnology; public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string friendlyName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string devicePath;
        }
        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PATH[] paths, ref uint numModes, [Out] MODE[] modes, IntPtr topology);
        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, [In] PATH[] paths, uint numModes, [In] MODE[] modes, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);

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
