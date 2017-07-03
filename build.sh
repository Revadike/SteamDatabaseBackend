#!/bin/bash

echo Building Steam Database...

cd "$(dirname "$0")"

rm -r bin/ obj/

dotnet build -f netcoreapp2.0 -c Release
