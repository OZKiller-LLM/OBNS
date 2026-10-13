#!/bin/bash
# Usage: [TML_SOURCE=<folder>] [SKIP_PREPARE=1] [ONLY="serial serial..."] run_all.sh [apk]
# The 1.1 device benchmark on every phone connected to adb (USB and wireless), all at once:
# for each phone, where to keep the content (content_base.sh: the SD card first), install the
# build and copy what is missing (prepare_device.sh), the 1.1 suite (core11_suite.sh) and the
# plugin regression (plugin_regression.sh). Phones run in parallel; each writes its own files:
#   results/all_<model>_<serial>.txt   prepare, content location, benchmark, plugin regression
# then a summary of every phone's frame rates (results/all_summary.txt).
# NO_TML is set per phone with about 2 GB of RAM or less (the A04s, 3 GB, runs it). Install failures (another signature) stop
# that phone only; nothing is uninstalled.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
D="$(cd "$(dirname "$0")" && pwd)"
APK="$1"
mkdir -p "$D/results"
serials="${ONLY:-$("$A" devices | awk 'NR>1 && $2=="device" {print $1}')}"
[ -z "$serials" ] && { echo "no phones connected"; exit 1; }

one() {
  local s="$1" model safe out base tml where ram notml
  model=$(MSYS_NO_PATHCONV=1 "$A" -s "$s" shell getprop ro.product.model | tr -d '\r ')
  safe="${model}_${s//[:.]/_}"
  out="$D/results/all_$safe.txt"
  : > "$out"
  eval "$(bash "$D/content_base.sh" "$s")"
  base="$BASE"; tml="$TML"; where="$WHERE"
  ram=$(MSYS_NO_PATHCONV=1 "$A" -s "$s" shell "grep MemTotal /proc/meminfo" | awk '{print $2}')
  notml=""; [ -n "$ram" ] && [ "$ram" -lt 2300000 ] && notml=1
  echo "=== $model ($s): content in $base ($where), Tuen Ma Line in $tml${notml:+ (skipped: little RAM)} ===" >> "$out"
  if [ -z "$SKIP_PREPARE" ]; then
    BASE="$base" TML="$tml" NO_TML="$notml" bash "$D/prepare_device.sh" "$s" $APK >> "$out" 2>&1 || { echo "STOPPED at prepare" >> "$out"; return; }
  fi
  bash "$D/core11_suite.sh" "$s" "$out.bench" "$base" "$tml" $notml
  cat "$out.bench" >> "$out"; rm -f "$out.bench"
  echo "--- plugin regression" >> "$out"
  bash "$D/plugin_regression.sh" "$s" "$base" >> "$out" 2>&1
  echo "=== done $(date '+%H:%M')" >> "$out"
}

for s in $serials; do
  one "$s" &
  sleep 2
done
wait

{
  for f in "$D"/results/all_*.txt; do
    [ "$f" = "$D/results/all_summary.txt" ] && continue
    head -1 "$f"
    grep -E "^=== .*\| (vulkannative|opengl) \||^load:|fps \(5 s|PASS|FAIL|STOPPED" "$f" | cut -c1-140
    echo
  done
} > "$D/results/all_summary.txt"
cat "$D/results/all_summary.txt"
