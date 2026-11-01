<#
.SYNOPSIS
  MirrorPad Sender : capture un écran Windows et l'envoie à l'app MirrorPad de l'iPad (H.264 / FLV sur TCP).

.EXAMPLE
  .\MirrorPad-Sender.ps1 -ListOutputs
  Capture une image de chaque écran pour savoir quel numéro correspond à l'écran virtuel.

.EXAMPLE
  .\MirrorPad-Sender.ps1 -IPad 192.168.1.130 -Output 1
  Envoie l'écran n°1 vers l'iPad. Ctrl+C pour arrêter.

.EXAMPLE
  .\MirrorPad-Sender.ps1 -Output 1 -Encoder nvenc -Fps 60
  Encodage sur carte NVIDIA (moins de charge CPU).
#>
param(
  [string]$IPad = '192.168.1.130',
  [int]$Output = 1,
  [int]$Fps = 30,
  [int]$Mbps = 12,
  [ValidateSet('x264','nvenc')][string]$Encoder = 'x264',
  [int]$Port = 7270,
  [switch]$ListOutputs,
  [string]$FFmpeg = 'ffmpeg'
)

if (-not (Get-Command $FFmpeg -ErrorAction SilentlyContinue)) {
  Write-Host "ffmpeg introuvable. Installez-le avec : winget install Gyan.FFmpeg  (puis rouvrez le terminal)" -ForegroundColor Red
  exit 1
}

# --- Repérage des écrans -------------------------------------------------------
if ($ListOutputs) {
  $dir = Join-Path $env:TEMP 'mirrorpad-ecrans'
  New-Item -ItemType Directory -Force $dir | Out-Null
  Get-ChildItem $dir -Filter 'ecran-*.png' | Remove-Item -Force
  for ($i = 0; $i -lt 6; $i++) {
    $png = Join-Path $dir "ecran-$i.png"
    & $FFmpeg -hide_banner -loglevel quiet -y -f lavfi -i "ddagrab=output_idx=$($i):framerate=5" -frames:v 1 -vf 'hwdownload,format=bgra' $png
    if (Test-Path $png) { Write-Host "Écran $i  ->  $png" }
  }
  Write-Host "Ouvrez les images : celle qui montre l'écran virtuel donne le numéro à passer avec -Output."
  Invoke-Item $dir
  exit 0
}

# --- Diffusion -----------------------------------------------------------------
$gop = $Fps * 2                                   # une image clé toutes les 2 s (reprise rapide après une coupure)
$buf = [math]::Max(1, [int]($Mbps / 4))           # petit tampon = faible latence

switch ($Encoder) {
  'x264'  { $enc = @('-vf','hwdownload,format=bgra,format=yuv420p',
                     '-c:v','libx264','-preset','ultrafast','-tune','zerolatency','-profile:v','high',
                     '-g',"$gop",'-b:v',"$($Mbps)M",'-maxrate',"$($Mbps)M",'-bufsize',"$($buf)M") }
  'nvenc' { $enc = @('-c:v','h264_nvenc','-preset','p1','-tune','ull','-zerolatency','1','-bf','0',
                     '-rc','cbr','-profile:v','high','-g',"$gop",'-b:v',"$($Mbps)M") }
}

$url = "tcp://$($IPad):$($Port)?tcp_nodelay=1"
Write-Host "MirrorPad : écran $Output -> $($IPad):$($Port)  ($Encoder, $Fps i/s, $Mbps Mb/s). Ctrl+C pour arrêter." -ForegroundColor Cyan

while ($true) {
  & $FFmpeg -hide_banner -loglevel warning `
      -f lavfi -i "ddagrab=output_idx=$($Output):framerate=$($Fps)" `
      @enc `
      -f flv -flvflags no_duration_filesize -flush_packets 1 $url
  Write-Host "iPad injoignable ou connexion perdue (app MirrorPad ouverte ?). Nouvel essai dans 3 s..." -ForegroundColor Yellow
  Start-Sleep -Seconds 3
}
