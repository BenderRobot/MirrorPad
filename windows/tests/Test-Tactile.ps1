# Test de diagnostic : trace un trait au doigt puis au stylet (injection Windows),
# d'abord sur l'écran principal, puis sur l'écran de l'iPad. Ouvrez Paint en plein écran sur l'écran visé avant chaque test.
#   powershell -ExecutionPolicy Bypass -File windows\tests\Test-Tactile.ps1
Add-Type -ReferencedAssemblies System.Windows.Forms, System.Drawing -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Threading;
public static class TT {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] public struct PI { public int type; public uint id, frame; public int flags; public IntPtr src, hwnd;
    public POINT px, hm, pxr, hmr; public uint time, hist; public int data; public uint keys; public ulong perf; public int btn; }
  [StructLayout(LayoutKind.Sequential)] public struct TI { public PI p; public int tflags, tmask; public RECT rc, rcr; public uint orient, pres; }
  [StructLayout(LayoutKind.Sequential)] public struct PE { public PI p; public int pflags, pmask; public uint pres, rot; public int tx, ty; }
  [StructLayout(LayoutKind.Explicit)] public struct PTI { [FieldOffset(0)] public int type; [FieldOffset(8)] public TI t; [FieldOffset(8)] public PE pen; }
  [DllImport("user32.dll", SetLastError=true)] static extern IntPtr CreateSyntheticPointerDevice(int type, uint max, int mode);
  [DllImport("user32.dll", SetLastError=true)] static extern bool InjectSyntheticPointerInput(IntPtr dev, PTI[] info, uint n);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
  // un appui tactile / stylet réellement pris en compte déplace le curseur à l'endroit touché
  static string Cursor(int x, int y) { Thread.Sleep(150); POINT c; GetCursorPos(out c);
    return (Math.Abs(c.x - x) <= 3 && Math.Abs(c.y - y) <= 3) ? " - PRIS EN COMPTE par Windows" : " - IGNORÉ par Windows (curseur en " + c.x + "," + c.y + ")"; }
  [DllImport("user32.dll", SetLastError=true)] static extern bool InitializeTouchInjection(uint max, uint mode);
  [DllImport("user32.dll", SetLastError=true)] static extern bool InjectTouchInput(uint n, TI[] info);
  // ancienne API tactile (Windows 8+) : pour comparer
  public static string LegacyTouch(int x1, int y1, int x2, int y2) {
    if (!InitializeTouchInjection(1, 1)) return "InitializeTouchInjection erreur " + Marshal.GetLastWin32Error();
    int fails = 0, err = 0;
    for (int i = 0; i <= 31; i++) {
      int x = x1 + (x2 - x1) * Math.Min(i, 30) / 30, y = y1 + (y2 - y1) * Math.Min(i, 30) / 30;
      int f = i == 0 ? (INRANGE|INCONTACT|DOWN) : i == 31 ? UP : (INRANGE|INCONTACT|UPDATE);
      var a = new TI[1]; a[0].p.type = 2; a[0].p.flags = f; a[0].p.px.x = x; a[0].p.px.y = y; a[0].tmask = 7; a[0].orient = 90; a[0].pres = 32000;
      a[0].rc.l = x - 2; a[0].rc.t = y - 2; a[0].rc.r = x + 2; a[0].rc.b = y + 2;
      if (!InjectTouchInput(1, a)) { fails++; err = Marshal.GetLastWin32Error(); }
      Thread.Sleep(12);
    }
    return (fails == 0 ? "injecté sans erreur" : fails + " trames refusées (erreur " + err + ")") + Cursor(x2, y2);
  }
  const int INRANGE=2, INCONTACT=4, FIRSTBUTTON=0x10, PRIMARY=0x2000, CONF=0x4000, DOWN=0x10000, UPDATE=0x20000, UP=0x40000;
  public static string Line(bool pen, int x1, int y1, int x2, int y2) {
    IntPtr dev = CreateSyntheticPointerDevice(pen ? 3 : 2, 1, 1);
    if (dev == IntPtr.Zero) return "CreateSyntheticPointerDevice erreur " + Marshal.GetLastWin32Error();
    int fails = 0, err = 0;
    for (int i = 0; i <= 31; i++) {
      int x = x1 + (x2 - x1) * Math.Min(i, 30) / 30, y = y1 + (y2 - y1) * Math.Min(i, 30) / 30;
      int f = i == 0 ? (INRANGE|INCONTACT|DOWN) : i == 31 ? UP : (INRANGE|INCONTACT|UPDATE);
      var a = new PTI[1]; a[0].type = pen ? 3 : 2;
      if (pen) { a[0].pen.p.type = 3; a[0].pen.p.flags = f | (i < 31 ? FIRSTBUTTON : 0); a[0].pen.p.px.x = x; a[0].pen.p.px.y = y; a[0].pen.pmask = 1; a[0].pen.pres = (uint)(i < 31 ? 600 : 0); }
      else { a[0].t.p.type = 2; a[0].t.p.flags = f | PRIMARY | CONF; a[0].t.p.px.x = x; a[0].t.p.px.y = y; a[0].t.tmask = 1;
             a[0].t.rc.l = x - 4; a[0].t.rc.t = y - 4; a[0].t.rc.r = x + 4; a[0].t.rc.b = y + 4; }
      if (!InjectSyntheticPointerInput(dev, a, 1)) { fails++; err = Marshal.GetLastWin32Error(); }
      Thread.Sleep(12);
    }
    return (fails == 0 ? "injecté sans erreur" : fails + " trames refusées (erreur " + err + ")") + Cursor(x2, y2);
  }
}
"@
[TT]::SetProcessDpiAwarenessContext([IntPtr]-4) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
foreach ($s in [System.Windows.Forms.Screen]::AllScreens) { "{0}  {1}  principal={2}" -f $s.DeviceName, $s.Bounds, $s.Primary }

$main = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$ipad = ([System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -Last 1).Bounds
foreach ($t in @(@{n='écran principal'; b=$main}, @{n="écran de l'iPad"; b=$ipad})) {
  if (-not $t.b) { continue }
  Read-Host "`nOuvrez Paint en plein écran sur l'$($t.n) (outil Pinceau), puis appuyez sur Entrée"
  Start-Sleep -Milliseconds 800
  $cx = $t.b.X + [int]($t.b.Width / 2); $cy = $t.b.Y + [int]($t.b.Height / 2)
  "doigt  sur $($t.n) : " + [TT]::Line($false, $cx - 300, $cy - 100, $cx + 300, $cy - 100)
  Start-Sleep -Milliseconds 500
  "stylet sur $($t.n) : " + [TT]::Line($true,  $cx - 300, $cy + 100, $cx + 300, $cy + 100)
  Start-Sleep -Milliseconds 500
  "doigt (ancienne API) sur $($t.n) : " + [TT]::LegacyTouch($cx - 300, $cy + 250, $cx + 300, $cy + 250)
}
"`nRegardez Paint sur chaque écran : trait du haut = doigt, milieu = stylet, bas = doigt (ancienne API). Lesquels sont apparus ?"
"`nInfos système :"
(Get-CimInstance Win32_OperatingSystem).Caption + " build " + [Environment]::OSVersion.Version.Build
Get-PnpDevice -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -match 'tactile|touch|stylet|pen' } | Format-Table Status, FriendlyName -AutoSize
