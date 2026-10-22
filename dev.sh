#!/usr/bin/env bash
# Dev loop (WSL): build with DEV, deploy, (re)start the game on the BZ_Test world and wait for it to load.
set -e
export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
LOG="$GAME/MelonLoader/Latest.log"
cd "$(dirname "$0")"
taskkill.exe /IM StellarDrive.exe /F > /dev/null 2>&1 || true
dotnet build src/BuildZoop.csproj -c Release -p:DefineConstants=DEV -nologo -v q 2>&1 | grep -E "rror CS" && exit 1
sleep 2
cp src/bin/Release/BuildZoop.dll "$GAME/Mods/"
rm -f "$LOG"
(cd "$GAME" && cmd.exe /c start "" StellarDrive.exe -screen-fullscreen 0 -screen-width 1280 -screen-height 720 < /dev/null > /dev/null 2>&1 &)
for i in $(seq 1 180); do
  if [ -f "$LOG" ] && grep -qE "scene loaded [0-9]+ MainGame|\[Build_Zoop\].*Exception|test world missing" "$LOG"; then break; fi
  sleep 1
done
grep -E "\[DEV\]|Build_Zoop|Build Zoop" "$LOG" | tail -20
