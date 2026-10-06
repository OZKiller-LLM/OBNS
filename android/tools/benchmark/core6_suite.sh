#!/bin/bash
# Usage: core6_suite.sh <serial> <out> <app content base> <TML content base> [skip_tml]
# The core benchmark set: light route (GL), East Rail + R-Train (GL, Vulkan via ANGLE), TML (GL).
D="$(dirname "$0")"; S="$1"; OUT="$2"; APP="$3"; TML="$4"
B() { bash "$D/bench_device.sh" "$S" "$@"; }
B "light: Timetable50 + AndroidTest" opengl "$APP/Railway/Route/Timetable50.csv" "$APP/Train/AndroidTest" - "$OUT" 900
B "medium: East Rail + R-Train" opengl "$APP/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv" "$APP/Train/MTR R-Train EMU 2023" 1200 "$OUT" 1800
B "medium: East Rail + R-Train" vulkan "$APP/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv" "$APP/Train/MTR R-Train EMU 2023" 1200 "$OUT" 1800
[ -n "$5" ] && exit 0
B "heavy: Tuen Ma Line + SP1900" opengl "$TML/Railway/Route/TMLDN 20144.csv" "$TML/Train/MTR TML SP1900 EMU ver2022" 1200 "$OUT" 3600
