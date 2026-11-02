<#
  Publie les modifications de bSideDesk : commit + push sur main.
  GitHub Actions compile ensuite, met à jour l'iPad et publie la release (PC mis à jour sous 2 min).

  Double-clic sur Publier.cmd, ou :  .\Publier.ps1 "message du commit"
#>
param([string]$Message = '')
Set-Location $PSScriptRoot

function Fail([string]$msg) { Write-Host "`n$msg" -ForegroundColor Red; Read-Host 'Entrée pour fermer'; exit 1 }

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail 'git est introuvable (winget install Git.Git).' }

$changes = git status --porcelain
if (-not $changes) { Write-Host 'Rien à publier : aucune modification.' -ForegroundColor Yellow; Start-Sleep 2; exit 0 }

Write-Host 'Modifications :' -ForegroundColor Cyan
git status --short
Write-Host ''

if (-not $Message) { $Message = Read-Host 'Description (Entrée = message automatique)' }
if (-not $Message) { $Message = "Mise à jour du $(Get-Date -Format 'dd/MM/yyyy HH:mm')" }

git add -A
git commit -m $Message | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'Le commit a échoué.' }

Write-Host "`nRécupération des changements distants..." -ForegroundColor Cyan
git pull --rebase origin main | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'Conflit pendant git pull --rebase. Corrigez puis lancez : git rebase --continue ; git push' }

Write-Host "`nEnvoi sur GitHub..." -ForegroundColor Cyan
git push origin main | Out-Host
if ($LASTEXITCODE -ne 0) { Fail 'Le push a échoué.' }

Write-Host "`nPublié. L'iPad se met à jour dans la minute, le PC sous 2 minutes." -ForegroundColor Green
Start-Sleep 3
