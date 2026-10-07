#!/bin/bash
# Usage: [EXTRA="<more intent extras>"] bench_device.sh <serial> <label> <opengl|vulkan> <route_file> <train_folder> <encoding|-> <out> [load_timeout_s]
# One standing benchmark on one device: device info, cool-down, load, ~35 s cab, ~35 s exterior,
# heat. Appends a readable block to <out>; never force-stops the game afterwards.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
S="$1"; LABEL="$2"; G="$3"; ROUTE="$4"; TRAIN="$5"; ENC="$6"; OUT="$7"; TIMEOUT="${8:-1800}"
sh_() { MSYS_NO_PATHCONV=1 "$A" -s "$S" shell "$@"; }
thermal() { sh_ dumpsys thermalservice | grep -m1 "Thermal Status" | grep -oE "[0-9]+"; }
skin() { sh_ dumpsys thermalservice | grep -m1 "mName=SKIN" | grep -oE "mValue=[0-9.]+" | cut -d= -f2; }
log() { "$A" -s "$S" logcat -d | grep " OpenBVE"; }
# The game runs in its own process (net.openbve:game).
# Closed only when adb answers, twice, that there is no such process: an adb hiccup (its server
# restarting when a phone is plugged in) once made every running game look closed.
running() {
  local i out
  for i in 1 2 3; do
    out=$(sh_ "pidof net.openbve:game; echo adb-ok" 2>/dev/null)
    case "$out" in
      *adb-ok*) [ -n "${out%adb-ok*}" ] && [ -n "$(echo "${out%adb-ok*}" | tr -d '[:space:]')" ] && return 0
                [ "$i" -ge 2 ] && return 1 ;;
    esac
    sleep 2
  done
  return 0
}

{
echo "=== $LABEL | $G | $(date '+%Y-%m-%d %H:%M') ==="
echo "device: $(sh_ getprop ro.product.model) | Android $(sh_ getprop ro.build.version.release) | SoC $(sh_ getprop ro.soc.manufacturer) $(sh_ getprop ro.soc.model) ($(sh_ getprop ro.hardware)) | $(sh_ grep MemTotal /proc/meminfo | awk '{printf "%.1f GB RAM", $2/1048576}') | $(sh_ wm size | tail -1 | sed 's/.*: //')"
} >> "$OUT"

# Cool down to thermal status 0-1 (at most 10 minutes), so runs are comparable.
for i in $(seq 1 60); do t=$(thermal); [ -z "$t" ] || [ "$t" -le 1 ] && break; sleep 10; done
echo "before: thermal status $(thermal), skin $(skin) C" >> "$OUT"

# The previous game stopped first, then the log cleared: a "scene:" line it wrote after the clear
# once made a run look loaded after 4 s.
sh_ am force-stop net.openbve; sleep 2
"$A" -s "$S" logcat -c
extra=""; [ "$ENC" != "-" ] && extra="--ei train_encoding $ENC"
start=$(date +%s)
sh_ "am start -S -n net.openbve/net.openbve.GameActivity --es graphics $G --es route_file '$ROUTE' --es train_folder '$TRAIN' $extra --es interface_style auto $EXTRA" >/dev/null
result="timeout"
loadlog=""
while [ $(( $(date +%s) - start )) -lt "$TIMEOUT" ]; do
  sleep 3
  # Collected and cleared as it goes: some phones keep only 256 KiB of log, which other apps fill.
  loadlog+="$(log)"$'\n'; "$A" -s "$S" logcat -c
  # "scene:" (every 5 s once running) as well: a line written between reading and clearing is lost.
  echo "$loadlog" | grep -qE "startup:| scene: [0-9.]+ fps" && { result="loaded"; break; }
  echo "$loadlog" | grep -qE "render thread failed|loading failed" && { result="failed"; break; }
  running || { result="app closed (killed or crashed)"; break; }
done
echo "load: $result after $(( $(date +%s) - start )) s" >> "$OUT"
echo "$loadlog" | grep -E "startup:|memory after|object prefetch|train plugin's own time|placement steps|safety plugin:|scripted trains: [0-9]+ of|texture transparency:|load profile, (route|train):|render thread failed|loading failed|graphics backend" | sed 's/.*OpenBVE : /  /' | cut -c1-600 >> "$OUT"
if [ "$result" != "loaded" ]; then
  "$A" -s "$S" logcat -d | grep -iE "lowmemorykiller|lmkd.*openbve|FATAL|AndroidRuntime" | tail -5 | cut -c1-200 | sed 's/^/  /' >> "$OUT"
  echo "after: thermal status $(thermal), skin $(skin) C" >> "$OUT"; echo >> "$OUT"; exit 1
fi

phase() {  # <name>: ~36 s of the game's 5-second scene/frame lines, read every 4 s
  "$A" -s "$S" logcat -c
  local l="" i
  for i in $(seq 1 9); do sleep 4; l+="$(log)"$'\n'; "$A" -s "$S" logcat -c; done
  local fps; fps=$(echo "$l" | grep "scene:" | grep -oE "scene: [0-9.]+" | awk '{print $2}' | tr '\n' ' ')
  echo "$1 fps (5 s samples): $fps" >> "$OUT"
  echo "$l" | grep "frame:" | tail -1 | sed 's/.*frame: /  last frame: /' | cut -c1-330 >> "$OUT"
  echo "$l" | grep "memory" | tail -1 | sed 's/.*OpenBVE : /  /' >> "$OUT"
  running || echo "  app closed during $1" >> "$OUT"
}
sleep 12            # first frames: batches build, textures upload
# The cab camera often starts behind the cab-to-saloon door or a barrier: move it forward for one
# second (CAMERA_MOVE_FORWARD, Keypad 9) so the cab controls are what is measured. --duration needs
# Android 12+; older phones get two long presses (~0.5 s each).
sh_ "input keyevent --duration 1000 KEYCODE_NUMPAD_9" >/dev/null 2>&1 || { sh_ "input keyevent --longpress KEYCODE_NUMPAD_9"; sh_ "input keyevent --longpress KEYCODE_NUMPAD_9"; }
sleep 1
phase "cab"
"$A" -s "$S" shell input keyevent KEYCODE_F2
sleep 5
phase "exterior"
echo "after: thermal status $(thermal), skin $(skin) C" >> "$OUT"
echo >> "$OUT"
