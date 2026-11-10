#!/bin/bash
# Usage: [EXTRA="<more intent extras>"] [ROUTE=<route file>] [TRAIN=<train folder>] drive_device.sh <serial> <out> <TML content base> [graphics] [settings]
# The drive benchmark on a phone (2026-11-08): the SP1900 from Tuen Mun to Long Ping on TMLDN 20144, driven by its own
# SelTrac ATO as a driver would (DriveBenchmark.cs, the Switch's): reverser F, B8, the mode selector to Auto, the doors
# and ATO start at each station, the timetable kept. Exterior view throughout. ROUTE and TRAIN drive another SelTrac
# train instead (2026-11-10: East Rail with the R-Train, last_station=Tai Wo). Prints each step as it happens and
# appends the summary to <out>. Changes no setting of the phone and never force-stops the game afterwards.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
S="$1"; OUT="$2"; TML="$3"; G="${4:-vulkannative}"; SETTINGS="${5:-selector_presses=5;last_station=LOP;dwell=20}"
ROUTE="${ROUTE:-$TML/Railway/Route/TMLDN 20144.csv}"; TRAIN="${TRAIN:-$TML/Train/MTR TML SP1900 EMU ver2022}"
sh_() { MSYS_NO_PATHCONV=1 "$A" -s "$S" shell "$@"; }

{
echo "=== drive: $(basename "$ROUTE") + $(basename "$TRAIN"), to $(echo "${SETTINGS#*last_station=}" | cut -d";" -f1) | $G${EXTRA:+ | $EXTRA} | $(date '+%Y-%m-%d %H:%M') ==="
echo "device: $(sh_ getprop ro.product.model) | Android $(sh_ getprop ro.build.version.release) | SoC $(sh_ getprop ro.soc.manufacturer) $(sh_ getprop ro.soc.model) | app $(sh_ dumpsys package net.openbve | grep -m1 versionName | sed 's/.*=//')"
} | tee -a "$OUT"

"$A" -s "$S" logcat -c
sh_ "am start -S -n net.openbve/net.openbve.GameActivity --es graphics $G --es route_file '$ROUTE' --es train_folder '$TRAIN' --es interface_style auto --es drive_benchmark '$SETTINGS' $EXTRA" >/dev/null
started=$(date +%s); seen=0; done=""
while [ -z "$done" ]; do
  sleep 10
  lines=$("$A" -s "$S" logcat -d -s OpenBVE:I | grep -E "drive benchmark:|benchmark:|benchmark summary|loaded after|Loading complete|Exception|potato mode|performance hints" )
  new=$(echo "$lines" | tail -n +$((seen + 1)))
  if [ -n "$new" ]; then echo "$new" | sed 's/^.*OpenBVE *: //'; seen=$(echo "$lines" | wc -l); fi
  echo "$lines" | grep -qE "summary written|given up|summary not written" && done=1
  [ $(( $(date +%s) - started )) -gt 3000 ] && { echo "timed out after 50 minutes"; done=1; }
done

"$A" -s "$S" logcat -d -s OpenBVE:I | grep -E "benchmark summary|benchmark:" | sed 's/^.*OpenBVE *: //' >> "$OUT"
echo >> "$OUT"
