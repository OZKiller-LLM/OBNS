#!/bin/bash
# Usage: core11_suite.sh <serial> <out> <content base> <TML content base> [skip_tml]
# The 1.1 benchmark set: native Vulkan (the default) on the light route, East Rail + R-Train and
# the Tuen Ma Line, with OpenGL ES (the fallback) beside it on East Rail and the Tuen Ma Line.
# Each run starts cool (bench_device.sh waits for thermal status 0-1, up to 10 minutes).
D="$(dirname "$0")"; S="$1"; OUT="$2"; APP="$3"; TML="$4"
B() { bash "$D/bench_device.sh" "$S" "$@"; }
B "light: Timetable50 + AndroidTest" vulkannative "$APP/Railway/Route/Timetable50.csv" "$APP/Train/AndroidTest" - "$OUT" 900
B "medium: East Rail + R-Train" vulkannative "$APP/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv" "$APP/Train/MTR R-Train EMU 2023" 1200 "$OUT" 1800
B "medium: East Rail + R-Train" opengl "$APP/Railway/Route/EAL2023/東鐵線南行 (羅湖→金鐘).csv" "$APP/Train/MTR R-Train EMU 2023" 1200 "$OUT" 1800
[ -n "$5" ] && exit 0
B "heavy: Tuen Ma Line + SP1900" vulkannative "$TML/Railway/Route/TMLDN 20144.csv" "$TML/Train/MTR TML SP1900 EMU ver2022" 1200 "$OUT" 3600
B "heavy: Tuen Ma Line + SP1900" opengl "$TML/Railway/Route/TMLDN 20144.csv" "$TML/Train/MTR TML SP1900 EMU ver2022" 1200 "$OUT" 3600
