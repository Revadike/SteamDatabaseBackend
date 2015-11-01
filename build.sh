#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/
cp settings.json.default settings.json


mv Properties/AssemblyInfo.cs assembly.temp
awk '!/AssemblyInformationalVersion/' assembly.temp > Properties/AssemblyInfo.cs

echo "[assembly: AssemblyInformationalVersion(\"$(git rev-parse --verify HEAD)\")]" >> Properties/AssemblyInfo.cs

nuget restore
xbuild /p:Configuration=Release

mv assembly.temp Properties/AssemblyInfo.cs
