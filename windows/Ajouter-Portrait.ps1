<#
  À lancer UNE FOIS en administrateur : ajoute la définition portrait 1536×2048 à l'écran virtuel
  (Virtual Display Driver) pour que l'iPad puisse passer en mode portrait.
    powershell -ExecutionPolicy Bypass -File Ajouter-Portrait.ps1
#>
$ErrorActionPreference = 'Stop'
$xmlPath = 'C:\VirtualDisplayDriver\vdd_settings.xml'
if (-not (Test-Path $xmlPath)) { throw "Fichier introuvable : $xmlPath" }

[xml]$doc = Get-Content $xmlPath -Raw
$res = $doc.vdd_settings.resolutions
$exists = $res.resolution | Where-Object { $_.width -eq '1536' -and $_.height -eq '2048' }
if ($exists) {
  Write-Host 'La définition 1536x2048 est déjà présente.' -ForegroundColor Yellow
} else {
  Copy-Item $xmlPath "$xmlPath.bak" -Force                       # sauvegarde
  $r = $doc.CreateElement('resolution')
  foreach ($kv in @(@('width', '1536'), @('height', '2048'), @('refresh_rate', '30'), @('refresh_rate', '60'))) {
    $e = $doc.CreateElement($kv[0]); $e.InnerText = $kv[1]; [void]$r.AppendChild($e)
  }
  [void]$res.AppendChild($r)
  $doc.Save($xmlPath)
  Write-Host 'Définition 1536x2048 ajoutée (sauvegarde : vdd_settings.xml.bak).' -ForegroundColor Green
}

# Redémarrage du pilote pour qu'il relise ses réglages
$d = Get-PnpDevice -Class Display | Where-Object { $_.FriendlyName -match 'Virtual Display' }
if ($d) {
  foreach ($dev in $d) { pnputil /restart-device "$($dev.InstanceId)" | Out-Null }   # plus fiable que Disable/Enable-PnpDevice
  Write-Host "Pilote redémarré. Vérifiez dans Paramètres > Affichage que l'écran virtuel est en 2048x1536, échelle 200 %." -ForegroundColor Green
} else {
  Write-Host 'Écran virtuel introuvable : redémarrez le PC pour appliquer.' -ForegroundColor Yellow
}
