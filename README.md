# MirrorPad

**L'iPad devient le « bureau d'à côté » du PC** : second écran tactile, stylet, clavier, Touch Bar contextuelle,
webcam, micro, haut-parleur, transfert de fichiers et tableau de bord. Windows 10/11 (et Debian X11), iPad sous iOS 12 jailbreaké.

```
Windows : écran virtuel (Virtual Display Driver) → ffmpeg ddagrab → H.264/FLV ─┐
          son (WASAPI loopback) → PCM ──────────────────────────────────────────┤  TLS 1.2, port 7272
          Touch Bar, tableau de bord, fichiers ─────────────────────────────────┤  Wi-Fi ou câble USB
iPad    : gestes, stylet, clavier, webcam, micro, presse-papiers, fichiers ─────┘  (usbmuxd)
```

| Dossier | Contenu |
|---|---|
| `ipad/` | App iOS (`MirrorPad.m`), `build.py` (Theos sur SRVHOME), `deploy.py` (SSH vers l'iPad) |
| `windows/src/` | App de la zone de notification en C# (.NET Framework 4.8), compilée en `MirrorPad.exe` par `build.sh` |
| `windows/helper/` | `MirrorPadInstall.exe` : outil d'installation des pilotes (embarqué dans l'installateur uniquement) |
| `windows/installer/` | `MirrorPad.nsi` (installateur NSIS), `release.yml` (copie du workflow), `LISEZMOI.md` |
| `windows/drivers/vdd/` | Virtual Display Driver 24.10.27 (MIT) + définitions iPad paysage / portrait |
| `windows/tests/` | `Test-Tactile.ps1` : diagnostic de l'injection tactile Windows |
| `linux/` | Envoyeur Python pour Debian / X11 |
| `srvhome/` | Ligne cron de rattrapage si l'iPad était éteint lors d'une release |
| `.github/workflows/release.yml` | Chaîne de publication |

## Fonctionnalités

| Domaine | Ce que ça fait |
|---|---|
| **Écran** | Écran étendu 2048×1536 (2048×1436 avec la Touch Bar dépliée : pas de bandes noires), portrait / paysage selon l'iPad (rideau pendant la bascule), position à droite / gauche / au-dessus, retiré quand l'iPad se verrouille, rebranché à son retour |
| **Fluidité** | Débit adaptatif (baisse si le Wi-Fi sature, remonte quand il se libère), tampon d'envoi court, encodage Quick Sync / NVENC / x264 faible latence, câble USB |
| **Saisie** | Modes Souris, Tactile, Stylet (pression), Pavé tactile ; clavier de l'iPad ouvert automatiquement quand on touche une zone de saisie (UI Automation + règles pour les applis muettes comme WhatsApp) |
| **Touch Bar** | Bande façon MacBook en bas de l'iPad, boutons selon l'appli active (Paint, Photoshop, PowerPoint, VS Code, navigateurs, Word, Excel, Explorateur, Teams, WhatsApp, lecteurs), repliable |
| **Fenêtres** | 3 doigts vers le haut, bouton Touch Bar ou **Ctrl+Alt+I** : la fenêtre active passe sur l'iPad (agrandie), et revient |
| **Loupe** | 2 doigts immobiles 0,6 s : zoom ×2,5 autour des doigts, pour viser au stylet ; même geste pour sortir |
| **Pavé numérique** | Touches pavé numérique vers le PC + calculatrice locale avec historique (un résultat touché est tapé sur le PC) |
| **Présentateur** | Touch Bar PowerPoint : mode Présentateur (Alt+F5) sur l'iPad, diapo précédente / suivante, laser, stylo, écran noir |
| **Capteurs** | Webcam (caméra virtuelle OBS), micro (VB-CABLE), son du PC sur les haut-parleurs de l'iPad |
| **Partage** | Presse-papiers texte et images, photos et fichiers iPad → PC, fichiers PC → iPad (« Envoyer vers > iPad ») |
| **Tableau de bord** | Horloge, processeur, mémoire, disque, réseau, durée d'allumage, et surveillance de sites / serveurs (HTTP ou ping) |
| **Sécurité** | TLS 1.2 avec épinglage du certificat, appairage par code à 6 chiffres, verrouillage du PC quand l'iPad s'éloigne (option) |

## Gestes

| Geste | Effet (tous les modes) |
|---|---|
| 1 doigt tapé / glissé | clic / glisser (tracé continu en mode Stylet) |
| appui long | clic droit |
| 2 doigts glissés | défilement fin, avec élan |
| pincer | zoom (Ctrl + molette) |
| 2 doigts immobiles 0,6 s | loupe ×2,5 (même geste pour sortir) |
| 3 doigts tapés | clavier de l'iPad |
| 3 doigts vers le haut | fenêtre active ⇄ iPad |
| 4 doigts tapés | réglages |

Sur ce PC, Windows ignore le tactile et le stylet simulés : MirrorPad le détecte au premier appui et bascule seul
en **émulation souris** (vrais mouvements `SendInput`, défilement fin, pincer = zoom, appui long = clic droit).

## Installation Windows

**Tout passe par `MirrorPad-Setup.exe`** (release GitHub) : jamais de script à lancer à la main.
Chaque nouveau composant Windows = une nouvelle section de `windows/installer/MirrorPad.nsi`.

| Composant (case de l'installateur) | Contenu |
|---|---|
| MirrorPad | app + ffmpeg, menu Démarrer, démarrage avec Windows, désinstallation dans « Applications installées » |
| Écran virtuel | Virtual Display Driver + `C:\VirtualDisplayDriver\vdd_settings.xml` (2048×1536, 1536×2048 et les tailles avec Touch Bar 2048×1436, 1536×1948) |
| Webcam | OBS Studio via winget (caméra « OBS Virtual Camera » : Teams, Zoom, navigateurs ; pas l'appli Caméra de Windows 10) |
| Câble USB | service Apple Mobile Device seul, extrait de l'installateur officiel d'Apple (sans iTunes) |
| Micro | VB-CABLE, téléchargé depuis vb-audio.com pendant l'installation |

Dans l'app : menu de l'icône > « Installer les composants manquants… » télécharge et relance le dernier installateur.
Réglages : `%APPDATA%\MirrorPad\config.json` · journal : `mirrorpad.log` (même dossier) · fichiers reçus : `Téléchargements\MirrorPad`.

### Surveillance du tableau de bord

Dans `config.json` (MirrorPad fermé) :

```json
"DashboardChecks": [
  { "Name": "Zabbix",  "Target": "https://zabbix.exemple.fr" },
  { "Name": "SRVHOME", "Target": "192.168.1.41" }
]
```
`https://…` = requête web (code et temps de réponse), sinon ping. Vérifié toutes les 15 s, 8 cibles au maximum.

## Publier une mise à jour

```powershell
.\Publier.ps1 "ce qui change"
```

À chaque push sur `main` (version `1.0.<n>`) :
1. **SRVHOME** (runner auto-hébergé) compile l'app iPad et l'installe aussitôt sur l'iPad (l'app est relancée).
2. **GitHub (Ubuntu)** compile `MirrorPad.exe` et `MirrorPadInstall.exe` avec Mono, télécharge ffmpeg (BtbN, GPL),
   construit `MirrorPad-Setup.exe` avec NSIS et signe les exe si Azure Trusted Signing est configuré (`windows/sign.sh`).
3. Une **release GitHub** publie le `.deb` iPad, `MirrorPad-Windows.zip` (mises à jour) et `MirrorPad-Setup.exe`.
4. **L'app Windows** vérifie les releases toutes les 2 minutes, se met à jour et redémarre seule.

Signature (recommandée contre les alertes Defender / SmartScreen) : variables `AZURE_SIGNING_ENDPOINT`,
`AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE` et secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`.

## Protocole

Un seul port, **7272**, en **TLS 1.2**. Le PC se connecte à l'iPad (Wi-Fi, ou tunnel USB via usbmuxd sur `127.0.0.1:27015`)
mais joue le rôle de serveur TLS : l'iPad épingle le certificat du PC, le PC n'accepte que les iPad appairés (jeton de 256 bits).
Appairage : le même code à 6 chiffres (SHA-256 du certificat + nonce de l'iPad) s'affiche des deux côtés.

Trames : `[type 1 octet][longueur 3 octets][données]`

| Type | Sens | Contenu |
|---|---|---|
| 1 | ↔ | ligne de contrôle (texte) |
| 2 | PC → iPad | vidéo FLV (H.264) |
| 3 | iPad → PC | webcam H.264 Annex-B |
| 4 | ↔ | image du presse-papiers (PNG) |
| 5 | iPad → PC | photo : `[longueur nom 2 o][nom][données]` |
| 6 | iPad → PC | micro PCM 16 bits mono 48 kHz |
| 7 | PC → iPad | son du PC, PCM 16 bits stéréo |
| 8 | ↔ | fichier en morceaux : `[id 4 o][drapeaux 1 o : 1 début, 2 fin][début : longueur nom 2 o + nom][données]` |

Poignée de main : `HELLO 2 <idPC> <nomPC>` → `AUTH <idIPad> <jeton>` ou `PAIR <idIPad> <nom> <nonce>` → `OK` / `PAIRED <jeton>` / `ERR …`.

Lignes iPad → PC : `D/M/U/R x y`, `W n`, `Z n`, `F id:x:y:d|m|u|c …`, `PD/PM x y pression [e]`, `PU`, `RM dx dy`, `CL`, `CR`,
`DD/DU`, `KS combo`, `T texte`, `K BACK|ENTER`, `RES largeur hauteur` (zone d'image sans la Touch Bar), `O P|L`, `P n`, `S fps mbps`, `SLEEP`, `CAM START w h fps|STOP`,
`MIC START rate ch|STOP`, `SND START|STOP`, `DB START|STOP`, `WIN SWAP`, `CB texte`.

Lignes PC → iPad : `N nom`, `C fps mbps encodeur`, `V NEW` / `V END`, `Q saturation% débit`, `SC …` (raccourcis),
`TB titre libellé|touches …` (Touch Bar), `KB 1 x y w h` / `KB 0` (clavier auto), `SND FMT rate 2`,
`DB cpu ramUtilisée ramTotale envoiKo réceptionKo disque% minutes nomPC`, `DBX nom|ok|détail …`, `CB texte`, `MSG texte`, `P n`.

## Linux (Debian, session X11)

```bash
sudo apt install ffmpeg xdotool x11-xserver-utils
python3 linux/mirrorpad-sender.py            # iPad détecté automatiquement
cp linux/mirrorpad.desktop ~/.config/autostart/   # lancement à l'ouverture de session
```
Crée un moniteur virtuel 2048×1536 à droite des écrans (`xrandr --setmonitor`), encodage VA-API si disponible, sinon x264.
Réglages : `~/.config/mirrorpad/config.json`. Wayland non pris en charge. Les fonctions récentes (Touch Bar, son, fichiers,
tableau de bord, USB) sont pour l'instant propres à Windows.

## Limites connues

- Exe non signés : Defender peut les bloquer (faux positif) tant qu'Azure Trusted Signing n'est pas activé.
- Webcam invisible dans l'appli Caméra de Windows 10 (DirectShow seulement) ; visible partout sous Windows 11 avec une future caméra Media Foundation.
- Tableau de bord sans Outlook / Teams : l'app tourne en administrateur et Windows lui interdit de lire ces applis.
- App iPad pour iPad jailbreaké (iOS 12) ; une version App Store demandera Xcode 26, un Mac et un iPad récent.
