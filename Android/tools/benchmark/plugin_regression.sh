#!/bin/bash
# Usage: plugin_regression.sh <serial> [content base]
# The train plugins known to work, checked on one device - run after any change that could touch
# plugin loading, the x86 layer, trimming or input:
#   SelTrac + OS_Ats1 (Win32, x86 layer)  MTR R-Train on East Rail: ATO departs Lo Wu
#   TBL (Win32, x86 layer)                KCR MLR on the KCR M184 route: ATO departs
#   FCMB (.NET Framework plugin)          Barcelona L3 with the FCMB 2000 and 3000: plugin loads
# A check whose content is not on the device is skipped, not failed. Input goes to the game by
# injected keys, so the game must have focus: a system dialog on top (a Samsung screen recorder
# prompt did this once) swallows them - the script checks focus and reports it.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"; S="$1"
F="${2:-/storage/emulated/0/Android/data/net.openbve/files}"
sh_() { MSYS_NO_PATHCONV=1 "$A" -s "$S" shell "$@"; }
key() { "$A" -s "$S" shell input keyevent "$@"; }
log() { "$A" -s "$S" logcat -d | grep " OpenBVE"; }
exists() { [ -n "$(sh_ "ls \"$1\" 2>/dev/null")" ]; }
focused() { sh_ "dumpsys window" 2>/dev/null | grep -m1 mCurrentFocus | grep -q "net.openbve/"; }
state() { log | grep "train:" | tail -1 | grep -oE "doors L [0-9.]+ R [0-9.]+|brake [0-9/]+"; }
speeds() { log | grep "scene:" | tail -4 | grep -oE "[0-9.]+ km/h" | tr '\n' ' '; }
moving() { log | grep "scene:" | tail -3 | grep -qE ", [1-9][0-9.]* km/h"; }
PASS=0; FAIL=0; SKIP=0
result() { echo "$1: $2"; case "$2" in PASS*) PASS=$((PASS+1));; SKIP*) SKIP=$((SKIP+1));; *) FAIL=$((FAIL+1));; esac; }

start() {  # <route> <train> <encoding|-> : starts the game and waits for it to load
  "$A" -s "$S" logcat -c
  local extra=""; [ "$3" != "-" ] && extra="--ei train_encoding $3"
  sh_ "am start -S -n net.openbve/net.openbve.GameActivity --es graphics opengl --es route_file '$1' --es train_folder '$2' $extra --es interface_style auto" >/dev/null
  for i in $(seq 1 200); do sleep 3; log | grep -qE "startup:|loading failed|render thread failed" && break; done
  sleep 8
}
plugin() { log | grep -E "train plugin|safety plugin" | sed 's/.*OpenBVE : //' | cut -c1-150 | tr '\n' ';'; }

# --- SelTrac + OS_Ats1 -------------------------------------------------------------------------
R="$F/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv"; T="$F/Train/MTR R-Train EMU 2023"
if exists "$R" && exists "$T"; then
  start "$R" "$T" 1200
  st=$(state)
  case "$st" in *"L 0.0"*) ;; *) key KEYCODE_F5 ;; esac
  case "$st" in *"R 0.0"*) ;; *) key KEYCODE_F6 ;; esac
  sleep 5
  for i in $(seq 1 10); do key KEYCODE_Q; sleep 0.25; done           # full service brake
  sleep 1
  for i in $(seq 1 5); do key KEYCODE_FORWARD_DEL; sleep 0.6; done   # A2 x5: mode 5 Off -> 0 Auto
  sleep 70                                                           # past the departure time
  key KEYCODE_SPACE                                                  # S: ATO departs
  sleep 30
  focused || echo "  (the game did not have focus - a dialog may have taken the keys)"
  if moving; then result "SelTrac ATO" "PASS ($(speeds))"; else result "SelTrac ATO" "FAIL (speeds: $(speeds); $(plugin))"; fi
else result "SelTrac ATO" "SKIP (no East Rail / R-Train on the device)"; fi

# --- TBL -----------------------------------------------------------------------------------------
R="$F/Railway/Route/KCR East Rail/九廣東鐵南行 (羅湖→尖東) M184 ver2007.csv"; T="$F/Train/KCR East Rail MLR (PXSE Workshop)"
if exists "$R" && exists "$T"; then
  start "$R" "$T" 1200
  sleep 4
  st=$(state)
  case "$st" in *"L 0.0"*) ;; *) key KEYCODE_F5 ;; esac                # close the open doors once
  case "$st" in *"R 0.0"*) ;; *) key KEYCODE_F6 ;; esac
  sleep 7
  for i in $(seq 1 6); do key KEYCODE_COMMA; sleep 0.3; done         # brake to N
  sleep 2; key KEYCODE_2; sleep 2; key KEYCODE_SPACE                 # D (ATO), S (start)
  sleep 40
  focused || echo "  (the game did not have focus - a dialog may have taken the keys)"
  if moving; then result "TBL ATO" "PASS ($(speeds))"; else result "TBL ATO" "FAIL (speeds: $(speeds); $(state | tr '\n' ' '); $(plugin))"; fi
else result "TBL ATO" "SKIP (no KCR route / MLR on the device)"; fi

# --- FCMB (.NET Framework plugin) ----------------------------------------------------------------
for series in 2000 3000; do
  R="$F/Railway/Route/Metro de Barcelona - L3/$series Zona Universitària.csv"; T="$F/Train/FCMB $series"
  if exists "$R" && exists "$T"; then
    start "$R" "$T" -
    if log | grep -q "Train plugin loaded successfully" && ! log | grep -q "failed to load"; then
      result "FCMB $series plugin" "PASS"
    else result "FCMB $series plugin" "FAIL ($(log | grep -iE "train plugin" | sed 's/.*OpenBVE : //' | cut -c1-200 | tr '\n' ';'))"; fi
  else result "FCMB $series plugin" "SKIP (Barcelona L3 / FCMB $series not installed)"; fi
done

echo "passed $PASS, failed $FAIL, skipped $SKIP"
[ "$FAIL" -eq 0 ]
