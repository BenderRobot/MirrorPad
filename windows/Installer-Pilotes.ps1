# Installe les deux pilotes qui transforment l'iPad en webcam et en micro pour Windows :
#   - akvirtualcamera : la webcam virtuelle « MirrorPad Camera »
#   - VB-CABLE        : le câble audio virtuel (micro « CABLE Output »)
# Lancement : clic droit > Exécuter avec PowerShell, ou depuis le menu de MirrorPad.
param([switch]$Webcam, [switch]$Micro)
$ErrorActionPreference = 'Stop'
if (-not $Webcam -and -not $Micro) { $Webcam = $true; $Micro = $true }

# 1. Droits administrateur (obligatoires pour installer un pilote) : on se relance si besoin
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Webcam) { $a += '-Webcam' }; if ($Micro) { $a += '-Micro' }
    Start-Process powershell -Verb RunAs -ArgumentList $a
    exit
}
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$tmp = Join-Path $env:TEMP 'mirrorpad-pilotes'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
$ok = $true

# 2. Webcam : dernière version d'akvirtualcamera sur GitHub
if ($Webcam) {
    $deja = Get-ChildItem "$env:ProgramFiles\AkVirtualCamera", "${env:ProgramFiles(x86)}\AkVirtualCamera" -Recurse -Filter AkVCamManager.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($deja) { Write-Host "Webcam : akvirtualcamera est déjà installé." -ForegroundColor Green }
    else {
        try {
            Write-Host "Webcam : recherche de la dernière version d'akvirtualcamera..."
            $rel = Invoke-RestMethod 'https://api.github.com/repos/webcamoid/akvirtualcamera/releases/latest' -Headers @{ 'User-Agent' = 'MirrorPad' }
            $asset = $rel.assets | Where-Object { $_.name -match 'windows' -and $_.name -match '\.exe$' } | Select-Object -First 1
            if (-not $asset) { throw "aucun installateur Windows dans la version $($rel.tag_name)" }
            $exe = Join-Path $tmp $asset.name
            Write-Host "Webcam : téléchargement de $($asset.name)..."
            Invoke-WebRequest $asset.browser_download_url -OutFile $exe -UseBasicParsing
            Write-Host "Webcam : l'installateur s'ouvre, validez les étapes (options par défaut)." -ForegroundColor Cyan
            Start-Process $exe -Wait
            Write-Host "Webcam : installée." -ForegroundColor Green
        } catch {
            $ok = $false
            Write-Host "Webcam : échec ($($_.Exception.Message)). Installez-la à la main : https://github.com/webcamoid/akvirtualcamera/releases" -ForegroundColor Red
        }
    }
}

# 3. Micro : VB-CABLE (installation silencieuse)
if ($Micro) {
    $deja = Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'VB-Audio Virtual Cable' }
    if ($deja) { Write-Host "Micro : VB-CABLE est déjà installé." -ForegroundColor Green }
    else {
        try {
            Write-Host "Micro : recherche de la dernière version de VB-CABLE..."
            $page = Invoke-WebRequest 'https://vb-audio.com/Cable/' -UseBasicParsing
            $lien = ($page.Links | Where-Object { $_.href -match 'VBCABLE_Driver_Pack\d+\.zip' } | Select-Object -First 1).href
            if (-not $lien) { $lien = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip' }
            if ($lien -notmatch '^https?://') { $lien = [Uri]::new([Uri]'https://vb-audio.com/Cable/', $lien).AbsoluteUri }
            $zip = Join-Path $tmp 'vbcable.zip'; $dir = Join-Path $tmp 'vbcable'
            Write-Host "Micro : téléchargement de $lien..."
            Invoke-WebRequest $lien -OutFile $zip -UseBasicParsing
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
            Expand-Archive $zip $dir -Force
            $setup = Get-ChildItem $dir -Recurse -Filter 'VBCABLE_Setup_x64.exe' | Select-Object -First 1
            if (-not $setup) { throw "VBCABLE_Setup_x64.exe introuvable dans l'archive" }
            Write-Host "Micro : installation du pilote..."
            Start-Process $setup.FullName -ArgumentList '-i', '-h' -Wait
            Write-Host "Micro : installé. Dans vos applis (Teams, Discord…), choisissez « CABLE Output » comme micro." -ForegroundColor Green
        } catch {
            $ok = $false
            Write-Host "Micro : échec ($($_.Exception.Message)). Installez-le à la main : https://vb-audio.com/Cable/" -ForegroundColor Red
        }
    }
}

Write-Host ""
if ($ok) { Write-Host "Terminé. Relancez la webcam ou le micro depuis l'iPad (si le micro n'apparaît pas, redémarrez le PC)." -ForegroundColor Green }
Read-Host "Appuyez sur Entrée pour fermer"
