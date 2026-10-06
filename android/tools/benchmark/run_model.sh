#!/bin/bash
# Usage: run_model.sh <model prefix> <name> [skip_prepare]
# run_device.sh for the one connected device whose model starts with <model prefix>, so a phone can
# be named by model rather than by serial or IP:port, e.g.
#   run_model.sh SM-A366 A36        (Galaxy A36 5G)
#   run_model.sh SM-A356 A35        (Galaxy A35 5G)
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
PREFIX="$1"; NAME="$2"; S=""
for d in $("$A" devices | awk 'NR>1 && $2=="device" {print $1}'); do
  case "$("$A" -s "$d" shell getprop ro.product.model | tr -d '\r')" in "$PREFIX"*) S="$d" ;; esac
done
[ -z "$S" ] && { echo "No $PREFIX* device connected: plug it in, or pair wireless debugging."; exit 1; }
echo "$NAME: $S"
bash "$(dirname "$0")/run_device.sh" "$S" "$NAME" $3
