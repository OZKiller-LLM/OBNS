#!/bin/bash
# Usage: [TML_SOURCE=<folder>] [NO_TML=1] prepare_device.sh <serial> [apk]
# Installs the test build and copies the benchmark content into the app's own folder:
#   files/Railway, files/Train           light test route + East Rail + R-Train (content/)
#   files/TML/Railway, files/TML/Train   the Tuen Ma Line pack, from TML_SOURCE (kept apart: its
#                                        Sound folder). The pack is private: never commit it.
# Stops, without uninstalling anything, if an existing install has another signature.
A="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"; S="$1"; D="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cygpath -m "$(cd "$D/../../.." && pwd)")"   # the repository, as C:/... for adb.exe
APK="${2:-$ROOT/android/OpenBve.Android/bin/Release/net10.0-android36.0/android-arm64/net.openbve-Signed.apk}"
F=/storage/emulated/0/Android/data/net.openbve/files
sh_() { MSYS_NO_PATHCONV=1 "$A" -s "$S" shell "$@"; }

# adb push creates folders as the shell user without group write, and on some Android versions
# (Galaxy A35, Android 14) it then cannot write files into them - nor can the app. Folders made
# with mkdir in the shell get group write, so each tree's folders are created that way first.
# push <parent folder on the PC> <folder name> <device parent folder>
push() {
  echo "  $1/$2 -> $3/"
  local list="$D/mkdirs_${S//[:.]/_}.sh"
  local q="'" r="'\\''" dir
  (cd "$1" && find "$2" -type d) | while IFS= read -r dir; do printf "mkdir -p '%s'\n" "$3/${dir//$q/$r}"; done > "$list"
  MSYS_NO_PATHCONV=1 "$A" -s "$S" push "$(cygpath -aw "$list")" /data/local/tmp/obve_mkdirs.sh >/dev/null
  sh_ "sh /data/local/tmp/obve_mkdirs.sh; rm /data/local/tmp/obve_mkdirs.sh"
  rm -f "$list"
  # The full output is kept (piping into grep would end early and cut the push off).
  # --sync copies only files missing or older on the device, so a rerun (or a retry after a
  # wireless connection drops mid-copy) sends only what is left.
  local try
  for try in 1 2 3; do
    MSYS_NO_PATHCONV=1 "$A" -s "$S" push --sync "$1/$2" "$3/" > "$list.out" 2>&1
    grep -v "%]" "$list.out" | tail -3
    grep -q "error" "$list.out" || break
    echo "  (copy interrupted - retrying)"; sleep 5
  done
  rm -f "$list.out"
}

echo "device: $(sh_ getprop ro.product.model), Android $(sh_ getprop ro.build.version.release), ABIs $(sh_ getprop ro.product.cpu.abilist)"
echo "free on /sdcard: $(sh_ df -h /sdcard | tail -1 | awk '{print $4}')"
out=$("$A" -s "$S" install -r "$APK" 2>&1 | tail -2)
echo "install: $out"
case "$out" in *Success*) ;; *) echo "STOPPED: install failed (see above); nothing was uninstalled."; exit 1 ;; esac

# The app makes its own folders on first start (the menu); let it, rather than the shell.
sh_ "monkey -p net.openbve -c android.intent.category.LAUNCHER 1" >/dev/null 2>&1
sleep 8

push "$ROOT/android/testcontent" Railway "$F"
push "$ROOT/android/testcontent" Train "$F"
push "$ROOT/content" Railway "$F"
push "$ROOT/content/Train" "MTR R-Train EMU 2023" "$F/Train"
# NO_TML=1: phones with little storage or RAM get the light route and East Rail only.
if [ -z "$NO_TML" ] && [ -n "$TML_SOURCE" ]; then
  sh_ "mkdir -p $F/TML"
  push "$TML_SOURCE" Railway "$F/TML"
  push "$TML_SOURCE" Train "$F/TML"
elif [ -z "$NO_TML" ]; then
  echo "  (TML_SOURCE not set: the Tuen Ma Line pack was not copied)"
fi
echo "content: $(sh_ "find $F -type f | wc -l") files"
sh_ "ls $F/Railway/Route $F/Train $F/TML/Railway/Route $F/TML/Train" 2>/dev/null | grep -vE "\.png$" | head -30
echo "free after: $(sh_ df -h /sdcard | tail -1 | awk '{print $4}')"
