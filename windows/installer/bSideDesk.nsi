; bSideDesk-Setup.exe : installation complète de bSideDesk pour Windows (NSIS, compilé par GitHub Actions)
;   makensis -DVERSION=1.0.42 bSideDesk.nsi
; Fichiers attendus à côté de ce script (préparés par le workflow) :
;   ffmpeg.exe, ffmpeg-LICENCE.txt, bSideDeskInstall.exe
Unicode true
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!define APP "bSideDesk"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\bSideDesk"
!include "MUI2.nsh"
!include "x64.nsh"
!include "LogicLib.nsh"

Name "${APP}"
Caption "Installation de ${APP} ${VERSION}"
OutFile "bSideDesk-Setup.exe"
RequestExecutionLevel admin
InstallDir "$LOCALAPPDATA\bSideDesk\app"
SetCompressor /SOLID lzma
BrandingText "${APP} ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=1036 "ProductName" "${APP}"
VIAddVersionKey /LANG=1036 "FileDescription" "Installation de ${APP}"
VIAddVersionKey /LANG=1036 "FileVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "ProductVersion" "${VERSION}"
VIAddVersionKey /LANG=1036 "LegalCopyright" "Benjamin Laurens"

!define MUI_ICON "..\bSideDesk.ico"
!define MUI_UNICON "..\bSideDesk.ico"
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
  Exec '"$WINDIR\explorer.exe" "$INSTDIR\bSideDesk.exe"'
FunctionEnd

Section "${APP}" SecApp
  SectionIn RO
  DetailPrint "Arrêt de ${APP} s'il est lancé..."
  nsExec::Exec 'taskkill /f /im bSideDesk.exe'
  Pop $0
  ; ancien nom de l'app (MirrorPad) : arrêt et désinstallation, les réglages sont repris par bSideDesk au démarrage
  nsExec::Exec 'taskkill /f /im MirrorPad.exe'
  Pop $0
  nsExec::Exec 'schtasks.exe /delete /tn MirrorPad /f'
  Pop $0
  Sleep 800
  Delete "$SMPROGRAMS\MirrorPad.lnk"
  Delete "$SMSTARTUP\MirrorPad.lnk"
  Delete "$SENDTO\iPad (MirrorPad).lnk"
  RMDir /r "$LOCALAPPDATA\MirrorPad\app"
  RMDir "$LOCALAPPDATA\MirrorPad"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MirrorPad"
  SetOutPath "$INSTDIR"
  File "..\bSideDesk.exe"
  File "..\bSideDesk.ico"
  File "..\bSideDesk.png"
  File "..\bSideDesk.vbs"
  File "..\version.txt"
  File "ffmpeg.exe"
  File "ffmpeg-LICENCE.txt"
  WriteUninstaller "$INSTDIR\Desinstaller.exe"

  CreateShortCut "$SMPROGRAMS\${APP}.lnk" "$INSTDIR\bSideDesk.exe" "" "$INSTDIR\bSideDesk.ico" 0
  CreateShortCut "$SMSTARTUP\${APP}.lnk" "$INSTDIR\bSideDesk.exe" "" "$INSTDIR\bSideDesk.ico" 0

  ; entrée « Applications installées » de Windows
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "Benjamin Laurens"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\bSideDesk.ico"
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
  File "/oname=$PLUGINSDIR\bSideDeskInstall.exe" "bSideDeskInstall.exe"
  nsExec::ExecToLog '"$PLUGINSDIR\bSideDeskInstall.exe" --setup-display "$PLUGINSDIR\vdd"'
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
  ; service « Apple Mobile Device » seul (extrait de l'installateur officiel d'Apple, sans iTunes)
  DetailPrint "Service Apple pour le câble USB (téléchargement d'environ 200 Mo si absent)..."
  File "/oname=$PLUGINSDIR\bSideDeskInstall.exe" "bSideDeskInstall.exe"
  nsExec::ExecToLog '"$PLUGINSDIR\bSideDeskInstall.exe" --setup-usb'
  Pop $0
  ${If} $0 == 3010
    SetRebootFlag true
  ${ElseIf} $0 != 0
    StrCpy $Warnings "$Warnings$\r$\n- câble USB (code $0) : détails dans la fenêtre « Plus d'infos » de l'installation"
  ${EndIf}
SectionEnd

Section "Micro (l'iPad comme micro)" SecMic
  DetailPrint "Téléchargement et installation de VB-CABLE (vb-audio.com)..."
  File "/oname=$PLUGINSDIR\bSideDeskInstall.exe" "bSideDeskInstall.exe"
  nsExec::ExecToLog '"$PLUGINSDIR\bSideDeskInstall.exe" --setup-vbcable'
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
  !insertmacro MUI_DESCRIPTION_TEXT ${SecUsb} "Service Apple Mobile Device (sans iTunes, depuis l'installateur officiel d'Apple) : connexion par le câble, plus rapide que le Wi-Fi, iPad en charge."
  !insertmacro MUI_DESCRIPTION_TEXT ${SecMic} "Câble audio VB-CABLE, téléchargé depuis vb-audio.com : le micro de l'iPad devient « CABLE Output » dans vos applis."
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------- désinstallation ----------
Function un.onInit
  SetRegView 64
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  nsExec::Exec 'taskkill /f /im bSideDesk.exe'
  Pop $0
  Sleep 800
  nsExec::Exec 'schtasks.exe /delete /tn bSideDesk /f'
  Pop $0
  Delete "$SMPROGRAMS\${APP}.lnk"
  Delete "$SMSTARTUP\${APP}.lnk"
  RMDir /r "$INSTDIR"
  RMDir "$LOCALAPPDATA\bSideDesk"
  DeleteRegKey HKCU "${UNINST_KEY}"
  MessageBox MB_YESNO|MB_ICONQUESTION "Supprimer aussi vos réglages et les iPad appairés ?" /SD IDNO IDNO keep
    RMDir /r "$APPDATA\bSideDesk"
  keep:
  ; les pilotes (écran virtuel, webcam, VB-CABLE) restent : ils peuvent servir à d'autres logiciels
SectionEnd
