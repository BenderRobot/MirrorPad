// Appels Windows : souris, clavier, définition d'écran, fenêtre active.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace MirrorPad
{
    public static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        public const uint LeftDown = 0x2, LeftUp = 0x4, RightDown = 0x8, RightUp = 0x10, Wheel = 0x800;

        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char c);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);

        const uint KEYUP = 2, UNICODE = 4;
        public static int Failures;

        static void Key(ushort vk, ushort scan, uint flags)
        {
            var i = new INPUT[1];
            i[0].type = 1; i[0].u.ki.wVk = vk; i[0].u.ki.wScan = scan; i[0].u.ki.dwFlags = flags;
            if (SendInput(1, i, Marshal.SizeOf(typeof(INPUT))) != 1) Failures++;
        }
        static void Real(ushort vk, uint flags) { Key(vk, (ushort)MapVirtualKey(vk, 0), flags | (IsExtended(vk) ? 1u : 0u)); }
        static bool IsExtended(ushort vk) { return (vk >= 0x21 && vk <= 0x2E) || vk == 0x5B || vk == 0x5C || (vk >= 0xA6 && vk <= 0xB7); }
        public static void Press(ushort vk) { Real(vk, 0); Real(vk, KEYUP); }

        // Chaque caractère passe par la vraie touche du clavier (AZERTY…) avec Maj / AltGr :
        // compatible avec toutes les applis (WhatsApp…). Les caractères sans touche passent en Unicode.
        public static void Text(string s)
        {
            foreach (char c in s)
            {
                if (c == '\n' || c == '\r') { Press(0x0D); continue; }
                if (c == '\t') { Press(0x09); continue; }
                short r = VkKeyScan(c);
                if (r == -1 || (r & 0xFF) == 0xFF) { Key(0, c, UNICODE); Key(0, c, UNICODE | KEYUP); continue; }
                ushort vk = (ushort)(r & 0xFF); int mods = (r >> 8) & 0xFF;
                if ((mods & 1) != 0) Real(0x10, 0);
                if ((mods & 2) != 0) Real(0x11, 0);
                if ((mods & 4) != 0) Real(0x12, 0);
                Real(vk, 0); Real(vk, KEYUP);
                if ((mods & 4) != 0) Real(0x12, KEYUP);
                if ((mods & 2) != 0) Real(0x11, KEYUP);
                if ((mods & 1) != 0) Real(0x10, KEYUP);
            }
        }

        public static void Zoom(int steps)
        {
            Real(0x11, 0);
            mouse_event(Wheel, 0, 0, steps * 120, UIntPtr.Zero);
            Real(0x11, KEYUP);
        }

        // Raccourci « ctrl+shift+s », « win+d », « volup »… : modificateurs enfoncés, touche, puis relâchement
        static readonly System.Collections.Generic.Dictionary<string, ushort> Keys = new System.Collections.Generic.Dictionary<string, ushort> {
            {"ctrl",0x11},{"shift",0x10},{"alt",0x12},{"win",0x5B},{"esc",0x1B},{"tab",0x09},{"enter",0x0D},{"space",0x20},
            {"back",0x08},{"del",0x2E},{"ins",0x2D},{"home",0x24},{"end",0x23},{"pgup",0x21},{"pgdn",0x22},
            {"left",0x25},{"up",0x26},{"right",0x27},{"down",0x28},{"printscreen",0x2C},{"apps",0x5D},
            {"volup",0xAF},{"voldown",0xAE},{"mute",0xAD},{"playpause",0xB3},{"next",0xB0},{"prev",0xB1},{"stop",0xB2},
            {"plus",0xBB},{"minus",0xBD} };
        static readonly string[] Modifiers = { "ctrl", "shift", "alt", "win" };

        public static bool Combo(string combo)
        {
            var parts = combo.ToLowerInvariant().Split('+');
            var mods = new System.Collections.Generic.List<ushort>(); ushort key = 0;
            foreach (var raw in parts)
            {
                var k = raw.Trim(); ushort vk;
                if (k.Length == 0) continue;
                if (Keys.TryGetValue(k, out vk)) { }
                else if (k.Length == 1 && char.IsLetterOrDigit(k[0]) && k[0] < 128) vk = (ushort)char.ToUpperInvariant(k[0]);
                else if (k.Length >= 2 && k[0] == 'f' && ushort.TryParse(k.Substring(1), out vk) && vk >= 1 && vk <= 24) vk = (ushort)(0x6F + vk);
                else return false;
                if (Array.IndexOf(Modifiers, k) >= 0) mods.Add(vk); else key = vk;
            }
            foreach (var m in mods) Real(m, 0);
            if (key != 0) { Real(key, 0); Real(key, KEYUP); }
            for (int i = mods.Count - 1; i >= 0; i--) Real(mods[i], KEYUP);
            return true;
        }

        public static void MoveRelative(int dx, int dy) { mouse_event(0x1, (uint)dx, (uint)dy, 0, UIntPtr.Zero); }

        // ---------- Injection de pointeurs (tactile natif et stylet, Windows 10 1809+) ----------
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_INFO
        {
            public int pointerType; public uint pointerId, frameId; public int pointerFlags;
            public IntPtr sourceDevice, hwndTarget;
            public POINT ptPixelLocation, ptHimetricLocation, ptPixelLocationRaw, ptHimetricLocationRaw;
            public uint dwTime, historyCount; public int InputData; public uint dwKeyStates; public ulong PerformanceCount; public int ButtonChangeType;
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_TOUCH_INFO { public POINTER_INFO pointerInfo; public int touchFlags, touchMask; public RECT rcContact, rcContactRaw; public uint orientation, pressure; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINTER_PEN_INFO { public POINTER_INFO pointerInfo; public int penFlags, penMask; public uint pressure, rotation; public int tiltX, tiltY; }
        [StructLayout(LayoutKind.Explicit)]
        public struct POINTER_TYPE_INFO { [FieldOffset(0)] public int type; [FieldOffset(8)] public POINTER_TOUCH_INFO touch; [FieldOffset(8)] public POINTER_PEN_INFO pen; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr CreateSyntheticPointerDevice(int pointerType, uint maxCount, int mode);
        [DllImport("user32.dll", SetLastError = true)] static extern bool InjectSyntheticPointerInput(IntPtr device, [In] POINTER_TYPE_INFO[] info, uint count);

        public const int PT_TOUCH = 2, PT_PEN = 3;
        public const int PF_INRANGE = 0x2, PF_INCONTACT = 0x4, PF_FIRSTBUTTON = 0x10, PF_PRIMARY = 0x2000, PF_CONFIDENCE = 0x4000,
                         PF_CANCELED = 0x8000, PF_DOWN = 0x10000, PF_UPDATE = 0x20000, PF_UP = 0x40000;
        static IntPtr touchDev = IntPtr.Zero, penDev = IntPtr.Zero;
        public static int PointerError;

        public static bool InjectTouch(POINTER_TYPE_INFO[] contacts)
        {
            if (touchDev == IntPtr.Zero) touchDev = CreateSyntheticPointerDevice(PT_TOUCH, 10, 1);
            if (touchDev == IntPtr.Zero) { PointerError = Marshal.GetLastWin32Error(); return false; }
            if (InjectSyntheticPointerInput(touchDev, contacts, (uint)contacts.Length)) return true;
            PointerError = Marshal.GetLastWin32Error(); return false;
        }

        public static bool InjectPen(POINTER_TYPE_INFO pen)
        {
            if (penDev == IntPtr.Zero) penDev = CreateSyntheticPointerDevice(PT_PEN, 1, 1);
            if (penDev == IntPtr.Zero) { PointerError = Marshal.GetLastWin32Error(); return false; }
            if (InjectSyntheticPointerInput(penDev, new[] { pen }, 1)) return true;
            PointerError = Marshal.GetLastWin32Error(); return false;
        }

        public static string ForegroundTitle()
        {
            var sb = new StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsEx")] static extern int ApplyDisplaySettings(string dev, IntPtr dm, IntPtr hwnd, uint flags, IntPtr lParam);
        const int DM_POSITION = 0x20, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000;
        const uint CDS_UPDATEREGISTRY = 1, CDS_NORESET = 0x10000000;

        // Position et définition actuelles d'un écran (false s'il n'est pas branché au bureau)
        public static bool GetDisplay(string device, out int x, out int y, out int w, out int h)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            x = y = w = h = 0;
            if (!EnumDisplaySettings(device, -1, ref dm) || dm.dmPelsWidth == 0) return false;
            x = dm.dmPositionX; y = dm.dmPositionY; w = dm.dmPelsWidth; h = dm.dmPelsHeight;
            return true;
        }

        // « Débranche » un écran du bureau Windows (les fenêtres reviennent sur les autres écrans)
        public static int DetachDisplay(string device)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            dm.dmFields = DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;   // définition 0×0 = écran retiré
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            return r;
        }

        [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPath, IntPtr paths, uint numMode, IntPtr modes, uint flags);
        // Équivalent de Windows + P > « Étendre » : tous les écrans connectés reviennent sur le bureau
        public static int ExtendAll() { return SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x80 | 0x4); }   // SDC_APPLY | SDC_TOPOLOGY_EXTEND

        // Rebranche l'écran à sa position et dans sa définition d'origine
        public static int AttachDisplay(string device, int x, int y, int w, int h)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            EnumDisplaySettings(device, -2, ref dm);   // réglages enregistrés (fréquence, couleurs…)
            dm.dmFields |= DM_POSITION | DM_PELSWIDTH | DM_PELSHEIGHT;
            dm.dmPositionX = x; dm.dmPositionY = y; dm.dmPelsWidth = w; dm.dmPelsHeight = h;
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY | CDS_NORESET, IntPtr.Zero);
            ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            return r;
        }

        // 0 = succès, -2 = définition non proposée par le pilote
        public static int SetResolution(string device, int w, int h)
        {
            var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettings(device, -1, ref dm)) return -100;
            dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmFields = 0x80000 | 0x100000;
            return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 1, IntPtr.Zero);
        }
    }
}
