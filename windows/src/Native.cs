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
        static void Real(ushort vk, uint flags) { Key(vk, (ushort)MapVirtualKey(vk, 0), flags); }
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
