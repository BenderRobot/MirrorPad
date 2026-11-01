# MirrorPad pour Windows : icône dans la zone de notification, sans fenêtre de terminal.
# Se lance via MirrorPad.vbs (double-clic). Réglages : %APPDATA%\MirrorPad\config.json
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MirrorPad {
  public static class Native {
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    // Diagnostic : nombre de frappes refusées par Windows et dernier code d'erreur
    public static int Failures = 0, LastError = 0;
    public static int InputSize { get { return Marshal.SizeOf(typeof(INPUT)); } }

    const uint KEYUP = 2, UNICODE = 4;
    static void Key(ushort vk, ushort scan, uint flags) {
      var i = new INPUT[1];
      i[0].type = 1; i[0].u.ki.wVk = vk; i[0].u.ki.wScan = scan; i[0].u.ki.dwFlags = flags;
      if (SendInput(1, i, Marshal.SizeOf(typeof(INPUT))) != 1) { Failures++; LastError = Marshal.GetLastWin32Error(); }
    }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    public static string ForegroundTitle() { var sb = new System.Text.StringBuilder(256); GetWindowText(GetForegroundWindow(), sb, 256); return sb.ToString(); }
    public static void Press(ushort vk) { Real(vk, 0); Real(vk, KEYUP); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char c);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    // Vraie touche (code virtuel + code matériel) : reconnue par toutes les applis, WhatsApp compris
    static void Real(ushort vk, uint flags) { Key(vk, (ushort)MapVirtualKey(vk, 0), flags); }
    // Tape un texte : chaque caractère passe par la touche correspondante du clavier actuel (AZERTY…),
    // avec Maj / AltGr si nécessaire ; les caractères absents du clavier (émojis…) passent en Unicode.
    public static void Text(string s) {
      foreach (char c in s) {
        if (c == '\n' || c == '\r') { Real(0x0D, 0); Real(0x0D, KEYUP); continue; }
        if (c == '\t') { Real(0x09, 0); Real(0x09, KEYUP); continue; }
        short r = VkKeyScan(c);
        if (r == -1 || (r & 0xFF) == 0xFF) { Key(0, c, UNICODE); Key(0, c, UNICODE | KEYUP); continue; }
        ushort vk = (ushort)(r & 0xFF); int mods = (r >> 8) & 0xFF;
        if ((mods & 1) != 0) Real(0x10, 0);       // Maj
        if ((mods & 2) != 0) Real(0x11, 0);       // Ctrl  (Ctrl + Alt = AltGr)
        if ((mods & 4) != 0) Real(0x12, 0);       // Alt
        Real(vk, 0); Real(vk, KEYUP);
        if ((mods & 4) != 0) Real(0x12, KEYUP);
        if ((mods & 2) != 0) Real(0x11, KEYUP);
        if ((mods & 1) != 0) Real(0x10, KEYUP);
      }
    }
    // Zoom : Ctrl maintenu + molette
    public static void Zoom(int steps) {
      Key(0x11, 0, 0);
      mouse_event(0x800, 0, 0, steps * 120, UIntPtr.Zero);
      Key(0x11, 0, KEYUP);
    }
  }
}
'@
# Coordonnées en pixels réels sur chaque écran (indispensable pour placer la souris correctement à 200 %)
try { [void][MirrorPad.Native]::SetProcessDpiAwarenessContext([IntPtr](-4)) } catch {}
[System.Windows.Forms.Application]::EnableVisualStyles()

$mutex = New-Object System.Threading.Mutex($false, 'Local\MirrorPadSender')
if (-not $mutex.WaitOne(0)) {
  [System.Windows.Forms.MessageBox]::Show("MirrorPad est déjà lancé (icône près de l'horloge).", 'MirrorPad') | Out-Null
  exit
}

# --- Configuration ------------------------------------------------------------
$AppDir = Join-Path $env:APPDATA 'MirrorPad'
New-Item -ItemType Directory -Force $AppDir | Out-Null
$ConfigPath = Join-Path $AppDir 'config.json'
$LogPath    = Join-Path $AppDir 'ffmpeg.log'
$VbsPath    = Join-Path $PSScriptRoot 'MirrorPad.vbs'

$defaults = [ordered]@{ IPad = '192.168.1.130'; Output = 1; Fps = 30; Mbps = 12; Encoder = 'auto'; DetectedEncoder = ''; AutoDiscover = $true; ConfigVersion = 2; Port = 7270; AutoConnect = $true; Touch = $true; InputPort = 7271; TouchScreen = ''; AutoUpdate = $true; UpdateRepo = 'BenderRobot/MirrorPad'; GitHubToken = '' }
$script:cfg = [pscustomobject]$defaults
if (Test-Path $ConfigPath) {
  try {
    $saved = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    foreach ($k in $defaults.Keys) { if ($null -ne $saved.$k) { $script:cfg.$k = $saved.$k } }
  } catch {}
}
# Migration des anciens réglages : encodeur et détection automatiques
if ([int]$script:cfg.ConfigVersion -lt 2 -or $null -eq $saved.ConfigVersion) {
  if ($script:cfg.Encoder -eq 'x264') { $script:cfg.Encoder = 'auto' }
  $script:cfg.AutoDiscover = $true
  $script:cfg.ConfigVersion = 2
}
function Save-Config { $script:cfg | ConvertTo-Json | Set-Content $ConfigPath -Encoding UTF8 }

# --- ffmpeg -------------------------------------------------------------------
$script:ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $script:ffmpeg) {
  $cand = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'
  if (Test-Path $cand) { $script:ffmpeg = $cand }
}
if (-not $script:ffmpeg) {
  [System.Windows.Forms.MessageBox]::Show("ffmpeg est introuvable.`n`nInstallez-le avec : winget install Gyan.FFmpeg", 'MirrorPad') | Out-Null
  exit
}

# Encodeurs, du plus efficace au plus universel. 'qsv' = Intel Quick Sync entièrement sur la puce graphique,
# 'qsv-sys' = Quick Sync avec conversion des couleurs par le processeur (repli), 'nvenc' = NVIDIA, 'x264' = processeur.
$EncoderNames = [ordered]@{ qsv = 'Intel Quick Sync'; 'qsv-sys' = 'Intel Quick Sync (compat.)'; nvenc = 'NVIDIA NVENC'; x264 = 'Processeur (x264)' }
function Get-EncoderArgs([string]$e) {
  $c = $script:cfg
  $gop = [int]$c.Fps * 2
  $rate = "-b:v $($c.Mbps)M -maxrate $($c.Mbps)M -bufsize $([math]::Max(1, [int]($c.Mbps / 4)))M"
  $qsv = "-c:v h264_qsv -preset veryfast -look_ahead 0 -async_depth 1 -bf 0 -profile:v high -g $gop $rate"
  switch ($e) {
    'qsv'     { "-vf hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12 $qsv" }
    'qsv-sys' { "-vf hwdownload,format=bgra,format=nv12 $qsv" }
    'nvenc'   { "-c:v h264_nvenc -preset p1 -tune ull -zerolatency 1 -bf 0 -rc cbr -profile:v high -g $gop -b:v $($c.Mbps)M" }
    default   { "-vf hwdownload,format=bgra,format=yuv420p -c:v libx264 -preset ultrafast -tune zerolatency -profile:v high -g $gop $rate" }
  }
}
function Get-ActiveEncoder {
  if ($script:cfg.Encoder -ne 'auto') { return $script:cfg.Encoder }
  if (-not $script:cfg.DetectedEncoder) {
    # essai réel de chaque encodeur sur 10 images : on garde le premier qui fonctionne
    $notify.ShowBalloonTip(2000, 'MirrorPad', 'Recherche du meilleur encodeur vidéo…', 'Info')
    $found = 'x264'
    foreach ($e in @('qsv', 'qsv-sys', 'nvenc')) {
      $a = "-hide_banner -loglevel quiet -f lavfi -i ddagrab=output_idx=$($script:cfg.Output):framerate=30 -frames:v 10 $(Get-EncoderArgs $e) -f null -"
      $p = Start-Process -FilePath $script:ffmpeg -ArgumentList $a -WindowStyle Hidden -PassThru -Wait
      if ($p.ExitCode -eq 0) { $found = $e; break }
    }
    $script:cfg.DetectedEncoder = $found
    Save-Config
  }
  $script:cfg.DetectedEncoder
}
function Get-FfmpegArgs {
  $c = $script:cfg
  $script:activeEncoder = Get-ActiveEncoder
  $src = "-hide_banner -loglevel error -f lavfi -i ddagrab=output_idx=$($c.Output):framerate=$($c.Fps)"
  "$src $(Get-EncoderArgs $script:activeEncoder) -f flv -flvflags no_duration_filesize -flush_packets 1 tcp://$($c.IPad):$($c.Port)?tcp_nodelay=1"
}

# Détection de l'iPad : requête mDNS (Bonjour) « _mirrorpad._tcp.local » envoyée sur chaque carte réseau ;
# l'adresse de celui qui répond est celle de l'iPad.
function Find-IPad {
  $q = New-Object System.Collections.Generic.List[byte]
  $q.AddRange([byte[]](0,0, 0,0, 0,1, 0,0, 0,0, 0,0))
  foreach ($label in '_mirrorpad', '_tcp', 'local') { $q.Add([byte]$label.Length); $q.AddRange([System.Text.Encoding]::ASCII.GetBytes($label)) }
  $q.AddRange([byte[]](0, 0,12, 0x80,1))   # type PTR, classe IN + réponse directe demandée
  $packet = $q.ToArray()
  $group = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Parse('224.0.0.251')), 5353
  $socks = @()
  foreach ($ni in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($ni.OperationalStatus -ne 'Up' -or $ni.NetworkInterfaceType -eq 'Loopback') { continue }
    foreach ($ua in $ni.GetIPProperties().UnicastAddresses) {
      if ($ua.Address.AddressFamily -ne 'InterNetwork') { continue }
      try {
        $u = New-Object System.Net.Sockets.UdpClient (New-Object System.Net.IPEndPoint $ua.Address, 0)
        [void]$u.Send($packet, $packet.Length, $group); $socks += $u
      } catch {}
    }
  }
  $found = $null
  $deadline = (Get-Date).AddMilliseconds(1500)
  while (-not $found -and (Get-Date) -lt $deadline) {
    foreach ($u in $socks) {
      if ($u.Available -gt 0) {
        $from = New-Object System.Net.IPEndPoint ([System.Net.IPAddress]::Any), 0
        $data = $u.Receive([ref]$from)
        if ([System.Text.Encoding]::ASCII.GetString($data) -match '_mirrorpad') { $found = $from.Address.ToString(); break }
      }
    }
    Start-Sleep -Milliseconds 40
  }
  foreach ($u in $socks) { $u.Close() }
  $found
}
$script:fails = 0
$script:activeEncoder = '' 

$script:proc = $null
$script:procStart = [datetime]::MinValue
$script:nextTry = [datetime]::MinValue
$script:wanted = $false
$script:state = ''

function Start-Stream {
  $psi = New-Object System.Diagnostics.ProcessStartInfo
  $psi.FileName = $script:ffmpeg
  $psi.Arguments = Get-FfmpegArgs
  $psi.UseShellExecute = $false
  $psi.CreateNoWindow = $true
  $rep = ($LogPath -replace '\\', '/') -replace ':', '\:'
  $psi.EnvironmentVariables['FFREPORT'] = "file=$($rep):level=16"
  $script:proc = [System.Diagnostics.Process]::Start($psi)
  $script:procStart = Get-Date
}
function Stop-Stream {
  if ($script:proc -and -not $script:proc.HasExited) { try { $script:proc.Kill() } catch {} }
  $script:proc = $null
}

# --- Icônes (dessinées à la volée) -----------------------------------------------
# Icône de l'app + pastille d'état (gris / orange / vert) en bas à droite
$script:baseImage = $null
$pngPath = Join-Path $PSScriptRoot 'MirrorPad.png'
if (Test-Path $pngPath) { $script:baseImage = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream(,[System.IO.File]::ReadAllBytes($pngPath)))) }
function New-TabletIcon([System.Drawing.Color]$fill) {
  $bmp = New-Object System.Drawing.Bitmap 32, 32
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.InterpolationMode = 'HighQualityBicubic'
  $g.Clear([System.Drawing.Color]::Transparent)
  if ($script:baseImage) {
    $g.DrawImage($script:baseImage, 0, 0, 32, 32)
    $g.FillEllipse([System.Drawing.Brushes]::White, 18, 18, 14, 14)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $fill), 20, 20, 10, 10)
  } else {
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $fill), 3, 6, 26, 20)
    $g.DrawRectangle((New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2), 3, 6, 26, 20)
  }
  $g.Dispose()
  [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
}
$icons = @{
  idle      = New-TabletIcon ([System.Drawing.Color]::FromArgb(120, 120, 120))
  waiting   = New-TabletIcon ([System.Drawing.Color]::FromArgb(230, 150, 30))
  connected = New-TabletIcon ([System.Drawing.Color]::FromArgb(40, 170, 90))
}

# --- Mise à jour automatique (releases GitHub) ----------------------------------------
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
$script:localVersion = '0.0.0'
$verFile = Join-Path $PSScriptRoot 'version.txt'
if (Test-Path $verFile) { $script:localVersion = (Get-Content $verFile -Raw).Trim() }
$UpdatedFlag = Join-Path $AppDir 'updated.txt'

function Get-GitHubHeaders {
  $h = @{ 'User-Agent' = 'MirrorPad'; 'Accept' = 'application/vnd.github+json' }
  if ($script:cfg.GitHubToken) { $h['Authorization'] = "Bearer $($script:cfg.GitHubToken)" }
  $h
}
function Restart-App {
  $timer.Stop(); $fast.Stop(); Close-Input; Stop-Stream
  $notify.Visible = $false; $notify.Dispose()
  try { $mutex.ReleaseMutex(); $mutex.Dispose() } catch {}
  Start-Process -FilePath "$env:SystemRoot\System32\wscript.exe" -ArgumentList "`"$VbsPath`""
  [System.Windows.Forms.Application]::Exit()
}
function Update-App([bool]$manual) {
  try {
    $rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$($script:cfg.UpdateRepo)/releases/latest" -Headers (Get-GitHubHeaders) -TimeoutSec 10
    $remote = $rel.tag_name.TrimStart('v')
    if ([version]$remote -le [version]$script:localVersion) {
      if ($manual) { $notify.ShowBalloonTip(2000, 'MirrorPad', "Déjà à jour (version $($script:localVersion)).", 'Info') }
      return
    }
    $asset = $rel.assets | Where-Object { $_.name -eq 'MirrorPad-Windows.zip' } | Select-Object -First 1
    if (-not $asset) { return }
    $tmp = Join-Path $env:TEMP 'mirrorpad-update'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp 'MirrorPad-Windows.zip'
    if ($script:cfg.GitHubToken) {
      $h = Get-GitHubHeaders; $h['Accept'] = 'application/octet-stream'
      Invoke-WebRequest -Uri $asset.url -Headers $h -OutFile $zip -UseBasicParsing -TimeoutSec 60
    } else {
      Invoke-WebRequest -Uri $asset.browser_download_url -Headers @{ 'User-Agent' = 'MirrorPad' } -OutFile $zip -UseBasicParsing -TimeoutSec 60
    }
    $x = Join-Path $tmp 'x'
    Expand-Archive -Path $zip -DestinationPath $x -Force
    if (-not (Test-Path (Join-Path $x 'MirrorPad-App.ps1'))) { throw 'archive incomplète' }
    Copy-Item (Join-Path $x '*') $PSScriptRoot -Force
    Set-Content -Path $UpdatedFlag -Value $remote
    Restart-App
  } catch {
    if ($manual) { $notify.ShowBalloonTip(3000, 'MirrorPad', "Mise à jour impossible : $($_.Exception.Message)", 'Error') }
  }
}

# --- Tactile : le PC se connecte à l'iPad (port 7271) et rejoue les gestes en souris ---------
$script:inClient = $null; $script:inStream = $null; $script:inTask = $null
$script:inNext = [datetime]::MinValue; $script:inBuf = ''; $script:leftDown = $false
$script:touchBounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$TouchLog = Join-Path $AppDir 'tactile.log'
function Write-TouchLog([string]$msg) {
  try { Add-Content -Path $TouchLog -Value ("{0:HH:mm:ss.fff} {1}" -f (Get-Date), $msg) } catch {}
}

function Get-TouchBounds {
  $screens = [System.Windows.Forms.Screen]::AllScreens
  $s = $screens | Where-Object { $script:cfg.TouchScreen -and $_.DeviceName -eq $script:cfg.TouchScreen } | Select-Object -First 1
  if (-not $s) { $s = $screens | Where-Object { $_.Bounds.Width -eq 2048 -and $_.Bounds.Height -eq 1536 } | Select-Object -First 1 }
  if (-not $s) { $s = $screens | Where-Object { -not $_.Primary } | Select-Object -First 1 }
  if (-not $s) { $s = [System.Windows.Forms.Screen]::PrimaryScreen }
  $s.Bounds
}
function Release-Left {
  if ($script:leftDown) { [MirrorPad.Native]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); $script:leftDown = $false }
}
function Close-Input {
  if ($script:inClient) { try { $script:inClient.Close() } catch {} }
  $script:inClient = $null; $script:inStream = $null; $script:inTask = $null; $script:inBuf = ''
  $script:inNext = (Get-Date).AddSeconds(2)
  Release-Left
}
function Get-ForegroundTitle { $t = [MirrorPad.Native]::ForegroundTitle(); if ($t) { "« $t »" } else { '(aucune fenêtre active)' } }
function Invoke-TouchLine([string]$line) {
  $p = $line.Trim().Split(' ')
  if ($p.Count -ge 3) {
    $b = $script:touchBounds
    $x = [int]($b.Left + [double]::Parse($p[1], $inv) * ($b.Width - 1))
    $y = [int]($b.Top + [double]::Parse($p[2], $inv) * ($b.Height - 1))
    [void][MirrorPad.Native]::SetCursorPos($x, $y)
  }
  switch ($p[0]) {
    'D' { [MirrorPad.Native]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); $script:leftDown = $true }
    'U' { Release-Left }
    'R' { [MirrorPad.Native]::mouse_event(0x8, 0, 0, 0, [UIntPtr]::Zero); [MirrorPad.Native]::mouse_event(0x10, 0, 0, 0, [UIntPtr]::Zero) }
    'W' { [MirrorPad.Native]::mouse_event(0x800, 0, 0, [int]$p[1], [UIntPtr]::Zero) }
    'Z' { [MirrorPad.Native]::Zoom([int]$p[1]) }
    'T' {
      if ($p.Count -ge 2) {
        [MirrorPad.Native]::Failures = 0
        [MirrorPad.Native]::Text([System.Uri]::UnescapeDataString($p[1]))
        if ([MirrorPad.Native]::Failures) { Write-TouchLog "frappe refusée par Windows ($([MirrorPad.Native]::Failures) touches, erreur $([MirrorPad.Native]::LastError), taille INPUT $([MirrorPad.Native]::InputSize))" }
        else { Write-TouchLog "frappe envoyée à : $(Get-ForegroundTitle)" }
      }
    }
    'K' { switch ($p[1]) { 'BACK' { [MirrorPad.Native]::Press(0x08) } 'ENTER' { [MirrorPad.Native]::Press(0x0D) } } }
  }
}

$fast = New-Object System.Windows.Forms.Timer
$fast.Interval = 15
$fast.Add_Tick({
  if (-not ($script:wanted -and $script:cfg.Touch)) { if ($script:inClient) { Close-Input }; return }
  try {
    if ($script:inTask) {
      if ($script:inTask.IsCompleted) {
        if ($script:inTask.IsFaulted -or -not $script:inClient.Connected) {
          Write-TouchLog "connexion tactile impossible : $($script:inTask.Exception.InnerException.Message)"; Close-Input
        } else {
          $script:inStream = $script:inClient.GetStream(); $script:inTask = $null; $script:inBuf = ''
          $b = $script:touchBounds
          Write-TouchLog "canal tactile connecté ; écran cible $($b.Left),$($b.Top) $($b.Width)x$($b.Height)"
        }
      }
      return
    }
    if (-not $script:inClient) {
      if ((Get-Date) -ge $script:inNext) {
        $script:inClient = New-Object System.Net.Sockets.TcpClient
        $script:inClient.NoDelay = $true
        $script:inTask = $script:inClient.ConnectAsync([string]$script:cfg.IPad, [int]$script:cfg.InputPort)
        $script:inNext = (Get-Date).AddSeconds(2)
      }
      return
    }
    $sock = $script:inClient.Client
    if ($sock.Poll(0, [System.Net.Sockets.SelectMode]::SelectRead) -and $sock.Available -eq 0) { Write-TouchLog 'canal tactile fermé par l''iPad'; Close-Input; return }
    if ($sock.Available -gt 0) {
      $bytes = [byte[]]::new($sock.Available)
      $n = $script:inStream.Read($bytes, 0, $bytes.Length)
      $script:inBuf += [System.Text.Encoding]::ASCII.GetString($bytes, 0, $n)
      $lines = $script:inBuf.Split("`n")
      $script:inBuf = $lines[-1]
      $last = $lines.Count - 1
      for ($i = 0; $i -lt $last; $i++) {
        # on ne rejoue que le dernier déplacement d'une rafale
        if ($lines[$i].StartsWith('M') -and ($i + 1) -lt $last -and $lines[$i + 1].StartsWith('M')) { continue }
        if ($lines[$i]) { if (-not $lines[$i].StartsWith('M')) { Write-TouchLog "reçu : $($lines[$i])" }; Invoke-TouchLine $lines[$i] }
      }
    }
  } catch { Write-TouchLog "erreur : $($_.Exception.Message)"; Close-Input }
})

# --- Zone de notification -------------------------------------------------------
$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = $icons.idle
$notify.Visible = $true
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$notify.ContextMenuStrip = $menu

$statusItem = $menu.Items.Add('Arrêté'); $statusItem.Enabled = $false
$versionItem = $menu.Items.Add("Version $($script:localVersion)"); $versionItem.Enabled = $false
[void]$menu.Items.Add('-')
$toggleItem = $menu.Items.Add('Connecter l''iPad')
$toggleItem.Font = New-Object System.Drawing.Font($toggleItem.Font, [System.Drawing.FontStyle]::Bold)
$screensItem = $menu.Items.Add('Repérer les écrans…')
$settingsItem = $menu.Items.Add('Paramètres…')
$touchItem = $menu.Items.Add('Tactile (le doigt pilote la souris)')
$startupItem = $menu.Items.Add('Lancer au démarrage de Windows')
$updateItem = $menu.Items.Add('Rechercher une mise à jour')
[void]$menu.Items.Add('-')
$quitItem = $menu.Items.Add('Quitter')

function Set-State([string]$s) {
  if ($script:state -eq $s) { return }
  $prev = $script:state; $script:state = $s
  $target = "$($script:cfg.IPad) · écran $($script:cfg.Output)"
  switch ($s) {
    'idle'      { $notify.Icon = $icons.idle;      $statusItem.Text = 'Arrêté';                $toggleItem.Text = 'Connecter l''iPad' }
    'waiting'   { $notify.Icon = $icons.waiting;   $statusItem.Text = "Recherche de l'iPad…";  $toggleItem.Text = 'Déconnecter' }
    'connected' { $notify.Icon = $icons.connected; $statusItem.Text = "Connecté ($target · $($EncoderNames[$script:activeEncoder]))";    $toggleItem.Text = 'Déconnecter' }
  }
  $tip = "MirrorPad – $($statusItem.Text)"
  if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 63) }
  $notify.Text = $tip
  if ($s -eq 'connected' -and $prev -ne 'connected') { $notify.ShowBalloonTip(2000, 'MirrorPad', "iPad connecté ($target)", 'Info') }
}

function Connect-IPad    { $script:wanted = $true;  $script:nextTry = [datetime]::MinValue; Set-State 'waiting' }
function Disconnect-IPad { $script:wanted = $false; Stop-Stream; Set-State 'idle' }
function Restart-IfRunning { if ($script:wanted) { Stop-Stream; $script:nextTry = [datetime]::MinValue; $script:state = ''; Set-State 'waiting' } }

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 1000
$script:ticks = 0
$timer.Add_Tick({
  $script:ticks++
  if ($script:cfg.AutoUpdate -and ($script:ticks -eq 10 -or $script:ticks % 120 -eq 0)) { Update-App $false }
  try { $script:touchBounds = Get-TouchBounds } catch {}
  if (-not $script:wanted) { return }
  $now = Get-Date
  if ($script:proc -and -not $script:proc.HasExited) {
    if (($now - $script:procStart).TotalSeconds -ge 3) { Set-State 'connected' }
    return
  }
  if ($script:proc) {
    if (($now - $script:procStart).TotalSeconds -lt 3) { $script:fails++ } else { $script:fails = 0 }
    $script:proc = $null; $script:nextTry = $now.AddSeconds(3); Set-State 'waiting'
  }
  if ($now -ge $script:nextTry) {
    # adresse inconnue ou plusieurs échecs : on recherche l'iPad sur le réseau
    if ($script:cfg.AutoDiscover -and (-not $script:cfg.IPad -or $script:fails -ge 2)) {
      $ip = Find-IPad
      if ($ip -and $ip -ne $script:cfg.IPad) { $script:cfg.IPad = $ip; Save-Config; Close-Input }
      $script:fails = 0
      if (-not $script:cfg.IPad) { $script:nextTry = $now.AddSeconds(5); return }
    }
    try { Start-Stream } catch { $script:nextTry = $now.AddSeconds(10); $notify.ShowBalloonTip(3000, 'MirrorPad', "ffmpeg n'a pas pu démarrer : $($_.Exception.Message)", 'Error') }
  }
})

# --- Démarrage avec Windows / raccourci Menu Démarrer ----------------------------
$StartupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'MirrorPad.lnk'
$ProgramsLnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'MirrorPad.lnk'
function New-Shortcut([string]$path) {
  $sh = New-Object -ComObject WScript.Shell
  $l = $sh.CreateShortcut($path)
  $l.TargetPath = "$env:SystemRoot\System32\wscript.exe"
  $l.Arguments = "`"$VbsPath`""
  $l.WorkingDirectory = $PSScriptRoot
  $ico = Join-Path $PSScriptRoot 'MirrorPad.ico'
  if (Test-Path $ico) { $l.IconLocation = "$ico,0" } else { $l.IconLocation = "$env:SystemRoot\System32\shell32.dll,15" }
  $l.Description = 'MirrorPad : iPad en écran supplémentaire'
  $l.Save()
}
try { New-Shortcut $ProgramsLnk } catch {}
if (Test-Path $StartupLnk) { try { New-Shortcut $StartupLnk } catch {} }
$startupItem.Checked = Test-Path $StartupLnk
$touchItem.Checked = [bool]$script:cfg.Touch

# --- Repérage des écrans -----------------------------------------------------------
function Find-Screens {
  $was = $script:wanted
  if ($was) { Stop-Stream }
  $dir = Join-Path $AppDir 'ecrans'
  New-Item -ItemType Directory -Force $dir | Out-Null
  Get-ChildItem $dir -Filter '*.png' | Remove-Item -Force
  for ($i = 0; $i -lt 6; $i++) {
    $png = Join-Path $dir "ecran-$i.png"
    Start-Process -FilePath $script:ffmpeg -WindowStyle Hidden -Wait -ArgumentList "-hide_banner -loglevel quiet -y -f lavfi -i ddagrab=output_idx=$($i):framerate=5 -frames:v 1 -vf hwdownload,format=bgra `"$png`""
  }
  Invoke-Item $dir
  [System.Windows.Forms.MessageBox]::Show("Une image par écran a été enregistrée (ecran-0.png, ecran-1.png…).`n`nRepérez celle de l'écran virtuel puis indiquez son numéro dans Paramètres.", 'MirrorPad') | Out-Null
  if ($was) { $script:nextTry = [datetime]::MinValue }
}

# --- Fenêtre Paramètres -----------------------------------------------------------
function Show-Settings {
  $f = New-Object System.Windows.Forms.Form
  $f.Text = 'MirrorPad – Paramètres'; $f.FormBorderStyle = 'FixedDialog'; $f.MaximizeBox = $false; $f.MinimizeBox = $false
  $f.StartPosition = 'CenterScreen'; $f.ClientSize = New-Object System.Drawing.Size(380, 290); $f.TopMost = $true
  $f.Font = New-Object System.Drawing.Font('Segoe UI', 10)
  $y = 16
  function Add-Row($label, $ctrl) {
    $l = New-Object System.Windows.Forms.Label; $l.Text = $label; $l.Location = New-Object System.Drawing.Point(16, ($script:rowY + 4)); $l.AutoSize = $true
    $ctrl.Location = New-Object System.Drawing.Point(170, $script:rowY); $ctrl.Width = 170
    $f.Controls.Add($l); $f.Controls.Add($ctrl); $script:rowY += 36
  }
  $script:rowY = 16
  $disc = New-Object System.Windows.Forms.CheckBox; $disc.Text = 'Trouver l''iPad automatiquement'; $disc.Checked = [bool]$script:cfg.AutoDiscover; $disc.Location = New-Object System.Drawing.Point(16, $script:rowY); $disc.AutoSize = $true; $f.Controls.Add($disc); $script:rowY += 32
  $ip = New-Object System.Windows.Forms.TextBox; $ip.Text = $script:cfg.IPad; Add-Row 'Adresse de l''iPad' $ip
  $out = New-Object System.Windows.Forms.NumericUpDown; $out.Minimum = 0; $out.Maximum = 9; $out.Value = [int]$script:cfg.Output; Add-Row 'Numéro d''écran' $out
  $fps = New-Object System.Windows.Forms.ComboBox; $fps.DropDownStyle = 'DropDownList'; [void]$fps.Items.AddRange(@('30', '60')); $fps.SelectedItem = "$($script:cfg.Fps)"; if ($fps.SelectedIndex -lt 0) { $fps.SelectedIndex = 0 }; Add-Row 'Images / seconde' $fps
  $mb = New-Object System.Windows.Forms.NumericUpDown; $mb.Minimum = 2; $mb.Maximum = 50; $mb.Value = [int]$script:cfg.Mbps; Add-Row 'Débit (Mb/s)' $mb
  $encKeys = @('auto') + @($EncoderNames.Keys)
  $enc = New-Object System.Windows.Forms.ComboBox; $enc.DropDownStyle = 'DropDownList'
  $autoLabel = 'Automatique'; if ($script:cfg.DetectedEncoder) { $autoLabel += " ($($EncoderNames[$script:cfg.DetectedEncoder]))" }
  [void]$enc.Items.Add($autoLabel); foreach ($k in $EncoderNames.Keys) { [void]$enc.Items.Add($EncoderNames[$k]) }
  $enc.SelectedIndex = [math]::Max(0, [array]::IndexOf($encKeys, [string]$script:cfg.Encoder)); Add-Row 'Encodage' $enc
  $auto = New-Object System.Windows.Forms.CheckBox; $auto.Text = 'Se connecter au lancement'; $auto.Checked = [bool]$script:cfg.AutoConnect; $auto.Location = New-Object System.Drawing.Point(16, $script:rowY); $auto.AutoSize = $true; $f.Controls.Add($auto)
  $ok = New-Object System.Windows.Forms.Button; $ok.Text = 'Enregistrer'; $ok.DialogResult = 'OK'; $ok.Location = New-Object System.Drawing.Point(170, 250); $ok.Width = 100
  $cancel = New-Object System.Windows.Forms.Button; $cancel.Text = 'Annuler'; $cancel.DialogResult = 'Cancel'; $cancel.Location = New-Object System.Drawing.Point(278, 250); $cancel.Width = 86
  $f.Controls.Add($ok); $f.Controls.Add($cancel); $f.AcceptButton = $ok; $f.CancelButton = $cancel
  if ($f.ShowDialog() -eq 'OK') {
    $addr = $null
    if (($ip.Text.Trim() -or -not $disc.Checked) -and -not [System.Net.IPAddress]::TryParse($ip.Text.Trim(), [ref]$addr)) {
      [System.Windows.Forms.MessageBox]::Show('Adresse IP invalide.', 'MirrorPad') | Out-Null; return
    }
    $script:cfg.AutoDiscover = $disc.Checked
    $script:cfg.IPad = $ip.Text.Trim()
    $script:cfg.Output = [int]$out.Value
    $script:cfg.Fps = [int]$fps.SelectedItem
    $script:cfg.Mbps = [int]$mb.Value
    $newEnc = $encKeys[$enc.SelectedIndex]
    $script:cfg.DetectedEncoder = ''   # la détection de l'encodeur est refaite à la prochaine connexion
    $script:cfg.Encoder = $newEnc
    $script:cfg.AutoConnect = $auto.Checked
    Save-Config
    Restart-IfRunning
  }
  $f.Dispose()
}

# --- Actions -----------------------------------------------------------------------
$toggleItem.Add_Click({ if ($script:wanted) { Disconnect-IPad } else { Connect-IPad } })
$notify.Add_MouseDoubleClick({ if ($script:wanted) { Disconnect-IPad } else { Connect-IPad } })
$screensItem.Add_Click({ Find-Screens })
$settingsItem.Add_Click({ Show-Settings })
$startupItem.Add_Click({
  if (Test-Path $StartupLnk) { Remove-Item $StartupLnk -Force } else { New-Shortcut $StartupLnk }
  $startupItem.Checked = Test-Path $StartupLnk
})
$updateItem.Add_Click({ Update-App $true })
$touchItem.Add_Click({
  $script:cfg.Touch = -not [bool]$script:cfg.Touch
  $touchItem.Checked = $script:cfg.Touch
  Save-Config
})
$quitItem.Add_Click({
  $timer.Stop(); $fast.Stop(); Close-Input; Disconnect-IPad
  $notify.Visible = $false; $notify.Dispose()
  [System.Windows.Forms.Application]::Exit()
})

Save-Config
Set-State 'idle'
if (Test-Path $UpdatedFlag) {
  $notify.ShowBalloonTip(3000, 'MirrorPad', "Mis à jour en version $(Get-Content $UpdatedFlag -Raw)", 'Info')
  Remove-Item $UpdatedFlag -Force
}
$timer.Start()
$fast.Start()
if ($script:cfg.AutoConnect) { Connect-IPad }
[System.Windows.Forms.Application]::Run((New-Object System.Windows.Forms.ApplicationContext))
Stop-Stream
