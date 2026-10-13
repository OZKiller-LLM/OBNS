#!/bin/bash
# Usage: content_base.sh <serial>
# Picks where a phone keeps the benchmark content, preferring its SD card, and prints three lines:
#   BASE=<folder with Railway/ and Train/>   (light route, East Rail)
#   TML=<folder with the Tuen Ma Line's Railway/ and Train/>
#   WHERE=<sd-documents | sd-app | internal>
# In order:
#   1. Documents/OpenBVE on the SD card, where the app has "all files" access (granted by the
#      player in Options; this script does not grant it) and the shell can write there;
#   2. the app's own folder on the SD card (Android/data/net.openbve/files), which the app can
#      always read;
#   3. the app's own folder on the phone's storage.
# A Tuen Ma Line already merged into the chosen Railway/Train folders (as copied by hand) is used
# where it is; otherwise it goes in TML/ beside them.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"; S="$1"
sh_() { MSYS_NO_PATHCONV=1 "$A" -s "$S" shell "$@" 2>/dev/null | tr -d '\r'; }
INTERNAL=/storage/emulated/0/Android/data/net.openbve/files

# Can the shell make a folder and write a file there?
writable() {
  [ "$(sh_ "mkdir -p '$1' && touch '$1/.obve_probe' && rm '$1/.obve_probe' && echo yes")" = "yes" ]
}

choose() {
  local card all
  all=$(sh_ "appops get net.openbve MANAGE_EXTERNAL_STORAGE")
  for card in $(sh_ "ls /storage" | grep -E '^[0-9A-F]{4}-[0-9A-F]{4}$'); do
    if echo "$all" | grep -q "allow" && writable "/storage/$card/Documents/OpenBVE"; then
      echo "/storage/$card/Documents/OpenBVE sd-documents"; return
    fi
    if writable "/storage/$card/Android/data/net.openbve/files"; then
      echo "/storage/$card/Android/data/net.openbve/files sd-app"; return
    fi
  done
  echo "$INTERNAL internal"
}

read -r BASE WHERE <<< "$(choose)"
if [ -n "$(sh_ "ls '$BASE/Railway/Route/TMLDN 20144.csv'")" ]; then
  TML="$BASE"
else
  TML="$BASE/TML"
fi
echo "BASE=$BASE"
echo "TML=$TML"
echo "WHERE=$WHERE"
