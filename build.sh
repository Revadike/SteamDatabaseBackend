#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/ obj/

dotnet publish --configuration Release -p:PublishSingleFile=true --runtime linux-x64
