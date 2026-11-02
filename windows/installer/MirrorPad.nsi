; MirrorPad-Setup.exe : installation complète de MirrorPad pour Windows (NSIS, compilé par GitHub Actions)
;   makensis -DVERSION=1.0.42 MirrorPad.nsi
; Fichiers attendus à côté de ce script (préparés par le workflow) :
;   ffmpeg.exe, ffmpeg-LICENCE.txt, MirrorPadInstall.exe
Unicode true
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define APP "MirrorPad"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\MirrorPad"
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

Name "${APP}"
Caption "Installation de ${APP} ${VERSION}"
OutFile "MirrorPad-Setup.exe"
RequestExecutionLevel admin
InstallDir "$LOCALAPPDATA\MirrorPad\app"
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APP}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installation de ${APP}"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "Benjamin Laurens"

!define MUI_ICON "..\MirrorPad.ico"
!define MUI_UNICON "..\MirrorPad.ico"
!define MUI_ABORTWARNING
!define MUI_COMPONENTSPAGE_SMALLDESC
!define MUI_WELCOMEPAGE_TITLE "Bienvenue dans ${APP}"
!define MUI_WELCOMEPAGE_TEXT "${APP} transforme votre iPad en écran supplémentaire tactile pour ce PC, avec stylet, clavier, raccourcis, webcam et micro.$\r$\n$\r$\nL'installation met en place l'application et les pilotes nécessaires. Elle prend une à deux minutes."
!define MUI_FINISHPAGE_TITLE "${APP} est installé"
!define MUI_FINISHPAGE_TEXT "Ouvrez ${APP} sur l'iPad : le PC le trouve tout seul sur le Wi-Fi. À la première connexion, vérifiez que le code affiché est le même sur les deux appareils."
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "Lancer ${APP}"
!define MUI_FINISHPAGE_RUN_FUNCTION LaunchApp

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "French"

Var Warnings

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${APP} nécessite Windows 10 ou 11 en 64 bits."
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext current
  InitPluginsDir
FunctionEnd

; Lance l'app sans les droits administrateur de l'installateur (via l'Explorateur)
Function LaunchApp
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\MirrorPad.exe"'
FunctionEnd

Section "${APP}" SecApp
  SectionIn RO
  DetailPrint "Arrêt de ${APP} s'il est lancé..."
  nsExec::Exec 'taskkill /f /im MirrorPad.exe'
  Pop $0
  Sleep 800
  SetOutPath "$INSTDIR"
  File "..\MirrorPad.exe"
  File "..\MirrorPad.ico"
  File "..\MirrorPad.png"
  File "..\MirrorPad.vbs"
  File "..\version.txt"
  File "ffmpeg.exe"
  File "ffmpeg-LICENCE.txt"
  WriteUninstaller "$INSTDIR\Desinstaller.exe"

  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\MirrorPad.exe" "" "$INSTDIR\MirrorPad.ico" 0
  CreateShortCut "$SMSTARTUP\${APP}.lnk" "$INSTDIR\MirrorPad.exe" "" "$INSTDIR\MirrorPad.ico" 0

  ; entrée « Applications installées » de Windows
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "Benjamin Laurens"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\MirrorPad.ico"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Desinstaller.exe"'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "EstimatedSize" 180000
SectionEnd

Section "Écran virtuel" SecDisplay
  DetailPrint "Installation du pilote d'écran virtuel..."
  SetOutPath "$PLUGINSDIR\vdd"
  File "..\drivers\vdd\MttVDD.inf"
  File "..\drivers\vdd\MttVDD.dll"
  File "..\drivers\vdd\mttvdd.cat"
  File "..\drivers\vdd\Virtual_Display_Driver.cer"
  File "..\drivers\vdd\vdd_settings.xml"
  File "/oname=$PLUGINSDIR\MirrorPadInstall.exe" "MirrorPadInstall.exe"
  nsExec::ExecToLog '"$PLUGINSDIR\MirrorPadInstall.exe" --setup-display "$PLUGINSDIR\vdd"'
  Pop $0
  ${If} $0 == 3010
    SetRebootFlag true
  ${ElseIf} $0 != 0
    StrCpy $Warnings "$Warnings$\r$\n- écran virtuel (code $0)"
  ${EndIf}
  SetOutPath "$INSTDIR"
SectionEnd

Section "Webcam (l'iPad comme caméra)" SecCam
  ; caméra virtuelle d'OBS Studio : logiciel signé, installé depuis le catalogue officiel winget
  ${If} ${FileExists} "$PROGRAMFILES64\obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll"
    DetailPrint "Caméra virtuelle OBS déjà installée."
  ${Else}
    DetailPrint "Installation d'OBS Studio (caméra virtuelle) via winget, quelques minutes..."
    nsExec::ExecToLog 'winget install --id OBSProject.OBSStudio -e --silent --accept-package-agreements --accept-source-agreements'
    Pop $0
    ${IfNot} ${FileExists} "$PROGRAMFILES64\obs-studio\data\obs-plugins\win-dshow\obs-virtualcam-module64.dll"
      StrCpy $Warnings "$Warnings$\r$\n- webcam : installez OBS Studio depuis obsproject.com (code $0)"
    ${EndIf}
  ${EndIf}
SectionEnd

Section "Câble USB (service Apple)" SecUsb
  ; service « Apple Mobile Device » : connexion par le câble (latence minimale, iPad en charge)
  nsExec::Exec 'sc.exe query "Apple Mobile Device Service"'
  Pop $0
  ${If} $0 == 0
    DetailPrint "Service Apple déjà installé."
  ${Else}
    DetailPrint "Installation d'Apple Devices (Microsoft Store) pour le câble USB..."
    nsExec::ExecToLog 'winget install --id 9NP83LWLPZ9K --source msstore --silent --accept-package-agreements --accept-source-agreements'
    Pop $0
    nsExec::Exec 'sc.exe query "Apple Mobile Device Service"'
    Pop $1
    ${If} $1 != 0
      DetailPrint "Apple Devices indisponible : installation d'iTunes (même service)..."
      nsExec::ExecToLog 'winget install --id Apple.iTunes -e --silent --accept-package-agreements --accept-source-agreements'
      Pop $0
      nsExec::Exec 'sc.exe query "Apple Mobile Device Service"'
      Pop $1
      ${If} $1 != 0
        StrCpy $Warnings "$Warnings$\r$\n- câble USB : installez « Apple Devices » depuis le Microsoft Store"
      ${EndIf}
    ${EndIf}
  ${EndIf}
SectionEnd

Section "Micro (l'iPad comme micro)" SecMic
  DetailPrint "Téléchargement et installation de VB-CABLE (vb-audio.com)..."
  File "/oname=$PLUGINSDIR\MirrorPadInstall.exe" "MirrorPadInstall.exe"
  nsExec::ExecToLog '"$PLUGINSDIR\MirrorPadInstall.exe" --setup-vbcable'
  Pop $0
  ${If} $0 != 0
    StrCpy $Warnings "$Warnings$\r$\n- micro (code $0, connexion Internet nécessaire)"
  ${EndIf}
SectionEnd

Section "-Bilan"
  ${If} $Warnings != ""
    MessageBox MB_ICONEXCLAMATION "Installation terminée, mais ces éléments n'ont pas pu être installés :$Warnings$\r$\n$\r$\nRelancez l'installateur plus tard pour réessayer."
  ${EndIf}
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SecApp} "L'application ${APP} (icône près de l'horloge), lancée au démarrage de Windows, avec ses mises à jour automatiques."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecDisplay} "Pilote d'écran virtuel (Virtual Display Driver) : l'écran que l'iPad affiche, en paysage et en portrait."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecCam} "Caméra virtuelle d'OBS Studio (installé via winget) : la caméra de l'iPad devient « OBS Virtual Camera » dans Teams, Zoom, Meet..."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUsb} "Service Apple Mobile Device (application Apple Devices ou iTunes) : connexion par le câble, plus rapide que le Wi-Fi, iPad en charge."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMic} "Câble audio VB-CABLE, téléchargé depuis vb-audio.com : le micro de l'iPad devient « CABLE Output » dans vos applis."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- désinstallation ----------
Function un.onInit
  SetRegView 64
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /f /im MirrorPad.exe'
  Pop $0
  Sleep 800
  nsExec::Exec 'schtasks.exe /delete /tn MirrorPad /f'
  Pop $0
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$SMSTARTUP\${APP}.lnk"
  RMDir /r "$INSTDIR"
  RMDir "$LOCALAPPDATA\MirrorPad"
  DeleteRegKey HKCU "${UNINST_KEY}"
  MessageBox MB_YESNO|MB_ICONQUESTION "Supprimer aussi vos réglages et les iPad appairés ?" /SD IDNO IDNO keep
    RMDir /r "$APPDATA\MirrorPad"
  keep:
  ; les pilotes (écran virtuel, webcam, VB-CABLE) restent : ils peuvent servir à d'autres logiciels
SectionEnd
