#!/bin/bash
# OBNS: tests Metal on an Intel Mac or a Hackintosh, with nothing but this script (OBNS/METAL.md): OpenGL and Metal, shadows
# off and on High, then Metal under Apple's validation layer. Installs nothing and changes no settings of the game's: it
# keeps its own settings and results in ~/obns-intel-test. Ctrl+C stops it. Needs Mono and SDL2.
#
#   bash intel-mac-test.sh obns-desktop-buildN.zip "/path/to/route.csv" "/path/to/train folder"
#
# Then send back the report it names at the end: a summary, the logs and pictures of the game, the Mac's model, macOS and GPU.
set -u

if [ $# -ne 3 ]; then
	echo "usage: bash intel-mac-test.sh <build zip> <route file> <train folder>" >&2
	exit 2
fi

zip_file=$1
route=$2
train=$3
hold_stats=5        # frame statistics lines to wait for, one each 10 s once the game is running
load_timeout=900    # seconds a run may take in all

work="$HOME/obns-intel-test"
bin="$work/build"
settings="$work/Settings"
results="$work/results-$(date +%Y%m%d-%H%M%S)"

export PATH="$PATH:/Library/Frameworks/Mono.framework/Versions/Current/Commands:/usr/local/bin:/opt/homebrew/bin"
export DYLD_FALLBACK_LIBRARY_PATH="/usr/local/lib:/opt/homebrew/lib:/opt/local/lib:/Library/Frameworks/Mono.framework/Versions/Current/lib:/usr/lib"

missing=0
if ! command -v mono >/dev/null 2>&1; then
	echo "Mono isn't installed (the .pkg from mono-project.com, or Homebrew's mono)." >&2
	missing=1
fi

sdl=0
for folder in /usr/local/lib /opt/homebrew/lib /opt/local/lib; do
	[ -e "$folder/libSDL2.dylib" ] && sdl=1
done

if [ "$sdl" = 0 ]; then
	echo "SDL2 isn't installed (Homebrew's sdl2, or MacPorts' libsdl2)." >&2
	missing=1
fi

for needed in "$zip_file" "$route" "$train"; do
	if [ ! -e "$needed" ]; then
		echo "Not found: $needed" >&2
		missing=1
	fi
done

[ "$missing" = 0 ] || exit 1

[ "$(uname -m)" = "x86_64" ] || echo "Note: this isn't an Intel Mac ($(uname -m)); the results won't say how Intel's GPUs do."

mkdir -p "$results" "$settings/1.5.0"
summary="$results/summary.txt"

{
	echo "model: $(sysctl -n hw.model)"
	echo "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion))"
	echo "CPU: $(sysctl -n machdep.cpu.brand_string)"
	echo "Mono: $(mono --version | head -1)"
	echo "build sha256: $(shasum -a 256 "$zip_file" | cut -d' ' -f1)"
	system_profiler SPDisplaysDataType | grep -E "Chipset Model|VRAM|Metal|Vendor|Resolution"
	echo
} > "$summary"
cat "$summary"

rm -rf "$bin"
mkdir -p "$bin"
if ! unzip -q "$zip_file" -d "$bin"; then
	echo "The build wouldn't unpack: $zip_file" >&2
	exit 1
fi

printf 'version = 2\nsettings = %s\n' "$settings" > "$work/filesystem.cfg"

game=0
stop_game() {
	if [ "$game" != 0 ]; then
		kill "$game" 2>/dev/null
		for _ in 1 2 3 4 5; do
			kill -0 "$game" 2>/dev/null || break
			sleep 1
		done
		kill -9 "$game" 2>/dev/null
		wait "$game" 2>/dev/null
		game=0
	fi
}
trap 'stop_game; exit 130' INT TERM

# run NAME METAL(0|1) SHADOWS(Off|High) [VARIABLE=VALUE...]
run() {
	local name=$1 metal=$2 shadows=$3
	shift 3
	echo "--- $name: starting (it can take a few minutes to load)"
	printf '[display]\npreferNativeBackend = true\n[quality]\nshadowresolution = %s\nshadowdrawdistance = Medium\nshadowcascades = 3\n' "$shadows" > "$settings/1.5.0/options.cfg"
	rm -f "$settings/1.5.0/obns.cfg" "$settings/log.txt" "$settings"/snapshot-*.png
	(
		cd "$bin" || exit 1
		env "$@" OBNS_METAL="$metal" OBNS_START_VIEW=exterior OBNS_FRAMESTATS=1 OBNS_SNAPSHOT_EVERY=20 \
			mono OpenBve.exe "/filesystem=$work/filesystem.cfg" "/route=$route" "/train=$train"
	) > "$results/$name-output.txt" 2>&1 &
	game=$!

	local waited=0 ended=""
	while [ "$waited" -lt "$load_timeout" ]; do
		sleep 5
		waited=$((waited + 5))
		if ! kill -0 "$game" 2>/dev/null; then
			ended="the game stopped by itself after $waited s"
			break
		fi

		if [ "$(grep -c 'OBNS frame stats' "$settings/log.txt" 2>/dev/null || true)" -ge "$hold_stats" ]; then
			break
		fi
	done

	[ -n "$ended" ] || [ "$waited" -lt "$load_timeout" ] || ended="no frames after $load_timeout s"
	stop_game

	cp "$settings/log.txt" "$results/$name-log.txt" 2>/dev/null
	cp "$settings/1.5.0/obns.cfg" "$results/$name-obns.cfg" 2>/dev/null
	local shot
	for shot in "$settings"/snapshot-*.png; do
		[ -e "$shot" ] && cp "$shot" "$results/$name-$(basename "$shot")"
	done

	{
		echo "=== $name (shadows $shadows)${ended:+: $ended}"
		grep -hE "world layer" "$results/$name-log.txt" 2>/dev/null | grep -v "frame stats" | cut -c1-240
		grep -h "OBNS frame stats" "$results/$name-log.txt" 2>/dev/null | tail -2 | cut -c1-500
		grep -hiE "failed assertion|validation|MTLDebug|Unhandled|SIGSEGV|SIGABRT" "$results/$name-output.txt" 2>/dev/null | head -10 | cut -c1-400
		echo
	} | tee -a "$summary"
}

run opengl-off 0 Off
run opengl-high 0 High
run metal-off 1 Off
run metal-high 1 High
run metal-high-validated 1 High MTL_DEBUG_LAYER=1

pmset -g therm >> "$summary" 2>/dev/null

report="$work/$(basename "$results").tar.gz"
tar -czf "$report" -C "$work" "$(basename "$results")"
echo
echo "Done. Please send back: $report"
echo "It holds pictures of the route you tested, and your user name appears in the logs' paths."
