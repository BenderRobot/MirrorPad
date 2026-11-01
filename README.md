# MirrorPad

L'iPad (iOS 12, jailbreaké) comme écran supplémentaire tactile d'un PC Windows.

```
Windows : écran virtuel (Virtual Display Driver) → ffmpeg ddagrab → H.264/FLV ──Wi-Fi :7270──► iPad (décodage matériel)
Windows : souris  ◄──────────────────────────── gestes tactiles ──Wi-Fi :7271──── iPad
```

| Dossier | Contenu |
|---|---|
| `ipad/` | App iOS (`MirrorPad.m`), `build.py` (Theos sur SRVHOME), `deploy.py` (SSH vers l'iPad) |
| `windows/` | App de la zone de notification (`MirrorPad-App.ps1` + `MirrorPad.vbs`), installeur |
| `srvhome/` | Ligne cron de rattrapage si l'iPad était éteint lors d'une release |
| `.github/workflows/release.yml` | Chaîne de publication |

## Publier une mise à jour

```
git add -A && git commit -m "ce qui change" && git push
```

À chaque push sur `main` (version `1.0.<n>`) :
1. **SRVHOME** (runner auto-hébergé) compile l'app iPad et l'installe aussitôt sur l'iPad (l'app est relancée).
2. Une **release GitHub** est publiée avec `mirrorpad_<version>_iphoneos-arm.deb` et `MirrorPad-Windows.zip`.
3. **L'app Windows** vérifie les releases toutes les 2 minutes, se met à jour et redémarre seule.

## Gestes

| Geste | Effet |
|---|---|
| 1 doigt tapé / glissé | clic / glisser |
| appui long | clic droit |
| 2 doigts tapés / glissés | clic droit / molette (avec élan) |
| pincer | zoom (Ctrl + molette) |
| 3 doigts tapés | clavier de l'iPad (texte envoyé au PC) |
| 4 doigts tapés | réglages (qualité, gestes, indicateur de performance, derniers PC) |

Tourner l'iPad bascule l'écran virtuel en portrait (1536×2048) ou paysage.

## Canal de contrôle (port 7271)

iPad → PC : `D/M/U/R x y`, `W n`, `Z n`, `T texte%encodé`, `K BACK`, `O P|L`, `P n`, `S fps mbps`
PC → iPad : `P n` (latence), `N nom-du-pc`, `C fps mbps encodeur`

## Installation Windows

`powershell -ExecutionPolicy Bypass -File windows\Installer-MirrorPad.ps1` (ajouter `-Token github_pat_…` si le dépôt est privé).
Réglages : `%APPDATA%\MirrorPad\config.json` · journaux : `ffmpeg.log`, `tactile.log` dans le même dossier.

Mode portrait : lancer une fois `windows\Ajouter-Portrait.ps1` **en administrateur** (ajoute 1536×2048 au pilote d'écran virtuel).

## Linux (Debian, session X11)

```bash
sudo apt install ffmpeg xdotool x11-xserver-utils
python3 linux/mirrorpad-sender.py            # iPad détecté automatiquement
cp linux/mirrorpad.desktop ~/.config/autostart/   # lancement à l'ouverture de session
```
Crée un moniteur virtuel 2048×1536 à droite des écrans (`xrandr --setmonitor`), encodage VA-API si disponible, sinon x264.
Réglages : `~/.config/mirrorpad/config.json`. Wayland non pris en charge.
