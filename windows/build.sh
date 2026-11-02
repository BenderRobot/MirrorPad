#!/bin/sh
# Compile MirrorPad.exe (.NET Framework 4.8) avec le compilateur C# de Mono — utilisé par GitHub Actions.
set -e
cd "$(dirname "$0")"
REF=/usr/lib/mono/4.8-api
mcs -target:winexe -out:MirrorPad.exe -langversion:7 -optimize+ -nostdlib -noconfig \
  -win32icon:MirrorPad.ico \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Drawing.dll \
  -r:$REF/System.Windows.Forms.dll -r:$REF/System.Runtime.Serialization.dll -r:$REF/System.Security.dll \
  -r:$REF/System.IO.Compression.dll -r:$REF/System.IO.Compression.FileSystem.dll -r:$REF/Microsoft.CSharp.dll \
  -r:$REF/System.Xml.dll -r:$REF/System.ServiceProcess.dll -r:$REF/Facades/System.Runtime.dll \
  src/*.cs
echo "BUILD_OK MirrorPad.exe"

# Outil d'installation des pilotes : embarqué dans MirrorPad-Setup.exe seulement, jamais installé sur le PC
mcs -target:exe -out:installer/MirrorPadInstall.exe -langversion:7 -optimize+ -nostdlib -noconfig \
  -win32icon:MirrorPad.ico \
  -r:$REF/mscorlib.dll -r:$REF/System.dll -r:$REF/System.Core.dll -r:$REF/System.Xml.dll \
  -r:$REF/System.IO.Compression.dll -r:$REF/System.IO.Compression.FileSystem.dll -r:$REF/Facades/System.Runtime.dll \
  helper/*.cs
echo "BUILD_OK MirrorPadInstall.exe"
