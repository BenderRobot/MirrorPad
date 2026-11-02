# bSideDesk est désormais une application C# (bSideDesk.exe, sources dans windows/src).
# Ce fichier ne sert qu'à la transition : l'ancienne version PowerShell vérifie sa présence lors de la mise à jour
# automatique, puis relance bSideDesk.vbs, qui démarre bSideDesk.exe.
Start-Process -FilePath (Join-Path $PSScriptRoot 'bSideDesk.exe')
