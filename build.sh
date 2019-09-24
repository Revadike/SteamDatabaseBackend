#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/ obj/

dotnet build -c Release
