# MirrorPad est désormais une application C# (MirrorPad.exe, sources dans windows/src).
# Ce fichier ne sert qu'à la transition : l'ancienne version PowerShell vérifie sa présence lors de la mise à jour
# automatique, puis relance MirrorPad.vbs, qui démarre MirrorPad.exe.
Start-Process -FilePath (Join-Path $PSScriptRoot 'MirrorPad.exe')
