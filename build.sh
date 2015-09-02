#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/
cp settings.json.default settings.json
nuget restore
xbuild /p:Configuration=Release
