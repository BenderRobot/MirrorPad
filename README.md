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

1 doigt tapé = clic · 1 doigt glissé = glisser · 2 doigts tapés = clic droit · 2 doigts glissés = molette.

## Installation Windows

`powershell -ExecutionPolicy Bypass -File windows\Installer-MirrorPad.ps1` (ajouter `-Token github_pat_…` si le dépôt est privé).
Réglages : `%APPDATA%\MirrorPad\config.json` · journaux : `ffmpeg.log`, `tactile.log` dans le même dossier.
