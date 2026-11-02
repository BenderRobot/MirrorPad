# Installateur Windows (MirrorPad-Setup.exe)

**Règle du projet : tout composant Windows dont l'app a besoin s'installe par `MirrorPad-Setup.exe`.**
Jamais de script à lancer à la main. Chaque nouveau composant = une nouvelle section dans `MirrorPad.nsi`,
testée en relançant l'installateur de la release.

| Section            | Composant                                   | Source                               |
|--------------------|---------------------------------------------|--------------------------------------|
| MirrorPad          | app + ffmpeg                                 | release GitHub (ffmpeg : BtbN, GPL)  |
| Écran virtuel      | Virtual Display Driver 24.10.27              | `windows/drivers/vdd` (MIT)          |
| Webcam             | caméra virtuelle d'OBS Studio                | winget `OBSProject.OBSStudio`        |
| Câble USB          | service Apple Mobile Device                  | winget msstore Apple Devices / iTunes |
| Micro              | VB-CABLE                                     | vb-audio.com (téléchargé à l'install)|

Chaque section détecte si le composant est déjà là et ne réinstalle rien d'inutile.
Dans l'app : menu MirrorPad > « Installer les composants manquants » télécharge le dernier installateur et le lance.
