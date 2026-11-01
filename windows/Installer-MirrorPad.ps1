<#
  Installe (ou réinstalle) MirrorPad pour Windows depuis la dernière release GitHub,
  dans %LOCALAPPDATA%\MirrorPad\app, puis le lance. Les mises à jour suivantes sont automatiques.

  Dépôt public :  powershell -ExecutionPolicy Bypass -File Installer-MirrorPad.ps1
  Dépôt privé  :  powershell -ExecutionPolicy Bypass -File Installer-MirrorPad.ps1 -Token github_pat_xxx
#>
param([string]$Token = '', [string]$Repo = 'BenderRobot/MirrorPad')
$ErrorActionPreference = 'Stop'
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$dest = Join-Path $env:LOCALAPPDATA 'MirrorPad\app'
$h = @{ 'User-Agent' = 'MirrorPad'; 'Accept' = 'application/vnd.github+json' }
if ($Token) { $h['Authorization'] = "Bearer $Token" }

Write-Host "Recherche de la dernière version de $Repo..."
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $h
$asset = $rel.assets | Where-Object { $_.name -eq 'MirrorPad-Windows.zip' } | Select-Object -First 1
if (-not $asset) { throw "La release $($rel.tag_name) ne contient pas MirrorPad-Windows.zip" }

$zip = Join-Path $env:TEMP 'MirrorPad-Windows.zip'
if ($Token) {
  $h['Accept'] = 'application/octet-stream'
  Invoke-WebRequest -Uri $asset.url -Headers $h -OutFile $zip -UseBasicParsing
} else {
  Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
}

# arrêter une instance en cours
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
  Where-Object { $_.CommandLine -like '*MirrorPad-App.ps1*' } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
Get-Process ffmpeg -ErrorAction SilentlyContinue | Where-Object { $_.Path } | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process MirrorPad -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $dest | Out-Null
Expand-Archive -Path $zip -DestinationPath $dest -Force

# mémoriser le jeton pour les mises à jour automatiques (dépôt privé)
if ($Token) {
  $cfgDir = Join-Path $env:APPDATA 'MirrorPad'; New-Item -ItemType Directory -Force $cfgDir | Out-Null
  $cfgPath = Join-Path $cfgDir 'config.json'
  $cfg = if (Test-Path $cfgPath) { Get-Content $cfgPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
  $cfg | Add-Member -NotePropertyName GitHubToken -NotePropertyValue $Token -Force
  $cfg | ConvertTo-Json | Set-Content $cfgPath -Encoding UTF8
}

# Au lancement, l'app recrée ses raccourcis (menu Démarrer, démarrage) vers ce dossier.
Start-Process -FilePath (Join-Path $dest 'MirrorPad.exe')
Write-Host "MirrorPad $($rel.tag_name) installé dans $dest et lancé." -ForegroundColor Green
