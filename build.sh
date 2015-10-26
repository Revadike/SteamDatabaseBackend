#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/
cp settings.json.default settings.json

awk '!/AssemblyInformationalVersion/' Properties/AssemblyInfo.cs > assembly.temp && mv assembly.temp Properties/AssemblyInfo.cs
echo "[assembly: AssemblyInformationalVersion(\"$(git rev-parse --verify HEAD)\")]" >> Properties/AssemblyInfo.cs

nuget restore
xbuild /p:Configuration=Release
