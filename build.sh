#!/usr/bin/env bash
# Builds the mod and packs the release zip in the layout StellarModManager expects:
#   dist/StellarDrive-BuildZoop.zip
#     mod.json
#     Thumbnail.png
#     Mods/BuildZoop.dll
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
cd "$(dirname "$0")"
rm -rf src/bin src/obj
dotnet build src/BuildZoop.csproj -c Release -nologo -v q "$@"
rm -rf dist/stage && mkdir -p dist/stage/Mods
cp src/bin/Release/BuildZoop.dll dist/stage/Mods/
cp Mods/mod.json Mods/Thumbnail.png dist/stage/
rm -f dist/StellarDrive-BuildZoop.zip
(cd dist/stage && python3 -c "import zipfile,os
z=zipfile.ZipFile('../StellarDrive-BuildZoop.zip','w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
    for f in fs:
        p=os.path.join(r,f); z.write(p,os.path.relpath(p,'.'))
z.close()")
rm -rf dist/stage
echo "Package: dist/StellarDrive-BuildZoop.zip"
