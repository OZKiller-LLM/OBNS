#!/bin/bash
# Usage: [TML_SOURCE=<folder>] [NO_TML=1] run_device.sh <serial> <name> [skip_prepare]
# A benchmark device end to end: install the test build and copy the content (prepare_device.sh;
# USB is fastest), then the core suite (core6_suite.sh; USB or wireless).
# Results: results/core6_<name>.txt (and results/prepare_<name>.txt).
D="$(cd "$(dirname "$0")" && pwd)"; S="$1"; NAME="$2"
F=/storage/emulated/0/Android/data/net.openbve/files
mkdir -p "$D/results"
if [ -z "$3" ]; then
  bash "$D/prepare_device.sh" "$S" | tee "$D/results/prepare_$NAME.txt" || exit 1
fi
rm -f "$D/results/core6_$NAME.txt"
bash "$D/core6_suite.sh" "$S" "$D/results/core6_$NAME.txt" "$F" "$F/TML" $NO_TML
cat "$D/results/core6_$NAME.txt"
