#!/usr/bin/env bash
# Sends dev commands to the running game (one per argument) and shows the new log lines.
GAME="/mnt/c/Program Files (x86)/Steam/steamapps/common/StellarDrive Demo"
LOG="$GAME/MelonLoader/Latest.log"
before=$(wc -l < "$LOG")
printf '%s\n' "$@" > "$GAME/UserData/buildzoop_cmd.txt"
sleep "${WAIT:-2}"
tail -n +$((before+1)) "$LOG" | grep -E "\[DEV\]|Exception|rror" | cut -c1-600
