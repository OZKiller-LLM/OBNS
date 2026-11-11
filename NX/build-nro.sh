#!/bin/bash
# OBNS 1.32: builds OBNS for the Switch on Linux (WSL's Ubuntu): the C# (NX/OpenBve.NX and the Android port's projects
# through NX/Switch.targets), trimmed, compiled ahead of time by Mono with LLVM, and linked with NX/runner into OBNS.nro.
#
# Needs DEVKITPRO, MONO_NX_ROOT and ICU_NX_INSTALL_DIR (NX/README.md) and Mono built by NX/build-mono.sh.
# Output, in $OBNS_OUT (default ~/switch-build/obns): sd/switch/OBNS/{OBNS.nro,managed/}, for the SD card.
set -e
NX=$(cd "$(dirname "$0")" && pwd)
: "${DEVKITPRO:?}" "${MONO_NX_ROOT:?}" "${ICU_NX_INSTALL_DIR:?}"
OUT=${OBNS_OUT:-$HOME/switch-build/obns}
DOTNET=$MONO_NX_ROOT/.dotnet/dotnet
CONFIG=Release
ART=$OUT/artifacts
export PATH=$PATH:$DEVKITPRO/devkitA64/bin
mkdir -p "$OUT"
# One build at a time: a second one deleting the first's folders mid-AOT breaks both.
# (A PID file: a flock's handle is inherited by .NET's build servers, which outlive the build.)
if [ -f "$OUT/.pid" ] && kill -0 "$(cat "$OUT/.pid")" 2>/dev/null; then echo "another build-nro.sh is running ($(cat "$OUT/.pid"))"; exit 1; fi
echo $$ > "$OUT/.pid"
trap 'rm -f "$OUT/.pid"' EXIT
# --relink: the link and the SD layout only, with the C# and AOT objects of the last build.
RELINK=; [ "$1" = "--relink" ] && RELINK=1
rm -rf "$OUT/sd"
if [ -z "$RELINK" ]; then
rm -rf "$OUT/managed" "$OUT/runner"
mkdir -p "$OUT/managed" "$OUT/runner/output"

PROPS="-c Release -p:AndroidTfm=net9.0 -p:ObnsX86Tfm=net9.0 -p:CustomAfterMicrosoftCommonTargets=$NX/Switch.targets --artifacts-path $ART -v q -nologo"
echo "== C#"
$DOTNET build "$NX/OpenBve.NX/OpenBve.NX.csproj" $PROPS
# Upstream loads the built-in ATS by path (Data/Plugins/OpenBveAts.dll); under full AOT it must be compiled in too.
$DOTNET build "$NX/../Android/OpenBveAts.Android/OpenBveAts.Android.csproj" $PROPS
BIN=$ART/bin/OpenBve.NX/release
ATS=$ART/bin/OpenBveAts.Android/release/OpenBveAts.dll

echo "== trimming (the framework only: OBNS's own assemblies are kept whole)"
ILLINK=$MONO_NX_ROOT/artifacts/bin/Mono.Linker/$CONFIG/net9.0/illink.dll
LIB_ROOT=$MONO_NX_ROOT/artifacts/bin/mono/libnx.arm64.$CONFIG/
FRAMEWORK_ROOT=$MONO_NX_ROOT/artifacts/bin/runtime/net9.0-libnx-$CONFIG-arm64/
keep=()
roots=()
for dll in "$BIN"/*.dll "$ATS"; do
    name=$(basename "$dll" .dll)
    # OpenBVE's and OBNS's own assemblies are kept whole. Third-party ones are trimmed to what's reached: AOT code a
    # direct branch can't span (128 MB; Mono's method tables come after all code and read-only data) fails to link,
    # as mono-nx found, and whole libraries (SharpCompress, Silk.NET's Vulkan bindings) take most of it. That also
    # takes care of NAudio 1.10, a .NET Framework build whose playback classes reference the real Windows Forms, which
    # the AOT compiler can't load (Android leaves it to the JIT; the Switch has none): the plugins reach only its readers.
    case "$name" in
        OpenBve*|OpenBveApi|OpenBveAts|LibRender2|RouteManager2|TrainManager|SoundManager|Route.*|Object.*|Train.*|Texture.*|Sound.*|Formats.*|AssimpParser*)
            keep+=(--action copy "$name") ;;
    esac
    # The content plugins are found by name at start-up (AndroidPlugins): nothing references them, so they are roots of
    # their own. Until 2026-11-08 they came through anyway, with the PDBs; without them the trimmer left all 18 out.
    case "$name" in
        Route.*|Object.*|Train.*|Texture.*|Sound.*) roots+=(-a "$dll") ;;
    esac
done
echo "${#roots[@]} root arguments for the content plugins"
$DOTNET "$ILLINK" -x "$MONO_NX_ROOT/src/mono/System.Private.CoreLib/src/ILLink/ILLink.Descriptors.xml" \
    -x "$MONO_NX_ROOT/src/mono/System.Private.CoreLib/src/ILLink/ILLink.LinkAttributes.xml" \
    --feature System.Resources.UseSystemResourceKeys true --feature System.Globalization.Invariant true \
    -d "$LIB_ROOT" -d "$FRAMEWORK_ROOT" -d "$BIN" -d "$(dirname "$ATS")" --trim-mode link --skip-unresolved true "${keep[@]}" \
    -a "$BIN/OpenBve.NX.dll" -a "$ATS" "${roots[@]}" -out "$OUT/managed" 2>&1 | grep -vE "Trim analysis|IL2[0-9]{3}" || true
count=$(ls "$OUT/managed"/*.dll 2>/dev/null | wc -l); echo "$count assemblies"; [ "$count" -gt 0 ] || { echo "trimming produced nothing"; exit 1; }

echo "== AOT, with LLVM"
COMPILER=$MONO_NX_ROOT/artifacts/bin/mono/linux.x64.$CONFIG/cross/linux-x64/libnx-arm64/mono-aot-cross
LLVM_TOOLS=$MONO_NX_ROOT/artifacts/obj/mono/linux.x64.$CONFIG/llvm/x64/bin
aot_one() {
    name=$(basename "$1")
    o="$OUT/runner/output"
    "$COMPILER" --llvm --path="$OUT/managed/" \
        --aot=full,static,direct-icalls,direct-pinvoke,tool-prefix=aarch64-none-elf-,outfile=$o/$name.o,llvm,llvmllc=-relocation-model=pic,llvm-path=$LLVM_TOOLS/,llvm-outfile=$o/$name-llvm.o \
        "$1" > "$o/$name.log" 2>&1 || { echo "AOT failed: $name"; tail -15 "$o/$name.log"; return 1; }
}
export -f aot_one
export COMPILER OUT LLVM_TOOLS
ls "$OUT/managed"/*.dll | xargs -P 6 -I{} bash -c 'aot_one "$@"' _ {}
cat "$OUT/runner/output"/*.log | grep "Linking symbol:" | sed "s/Linking symbol: '\([^']*\)'\./STATIC_MONO_SYM(\1);/" | sort -u > "$OUT/runner/mono_symbols.h"
echo "$(wc -l < "$OUT/runner/mono_symbols.h") assemblies compiled"
fi
rm -f "$OUT/runner/OBNS.elf" "$OUT/runner/OBNS.nro"

link_nro() {
    echo "== linking"
    cp -r "$NX/runner/source" "$NX/runner/Makefile" "$OUT/runner/"
    # deko3d's shaders: GLSL compiled by uam into .dksh, linked in through the Makefile's data folder (bin2o).
    rm -rf "$OUT/runner/data" && mkdir -p "$OUT/runner/data"
    for shader in "$NX"/runner/shaders/*.vert "$NX"/runner/shaders/*.frag; do
        name=$(basename "$shader")
        "$DEVKITPRO/tools/bin/uam" -s "${name##*.}" -o "$OUT/runner/data/${name%.*}_${name##*.}.dksh" "$shader" || { echo "uam failed: $name"; exit 1; }
    done
    # The renderer's programs (DekoGL): Android's native Vulkan shaders, the same source, made deko3d's - no descriptor
    # sets, the clip block a uniform buffer, GL's gl_VertexID.
    mkdir -p "$OUT/runner/shaders"
    for name in default.vert default.frag rectangle.vert rectangle.frag text.vert; do
        sed -e 's/set = [0-9], //' -e 's/layout(push_constant) uniform ClipBlock/layout(std140, binding = 2) uniform ClipBlock/' \
            -e 's/gl_VertexIndex/gl_VertexID/g' "$NX/../Android/OpenBve.Vulkan/Shaders/$name" > "$OUT/runner/shaders/$name"
        "$DEVKITPRO/tools/bin/uam" -s "${name##*.}" -o "$OUT/runner/data/${name%.*}_${name##*.}.dksh" "$OUT/runner/shaders/$name" || { echo "uam failed: $name"; exit 1; }
    done
cp "$OUT/runner/mono_symbols.h" "$OUT/runner/source/"
cp -r "$MONO_NX_ROOT/../native/shared" "$OUT/runner/nxshared"
: > "$OUT/runner/source/obns_stubs.c"
cd "$OUT/runner"
if ! make -j12 > link.log 2>&1; then
    # Calls compiled in but never made on the Switch (GLES, EGL, Windows): logged no-ops, so the NRO links.
    grep -o "undefined reference to \`[A-Za-z_][A-Za-z0-9_@]*'" link.log | sed "s/undefined reference to \`\(.*\)'/\1/" | sort -u > stubs.txt
    echo "$(wc -l < stubs.txt) unresolved native calls stubbed"
    {
        echo '// Generated by NX/build-nro.sh: native calls compiled in but not made on the Switch. Each logs once.'
        echo '#include <stdio.h>'
        echo 'static void obns_stub(const char *name, int *seen) { if (!*seen) { *seen = 1; printf("OBNS: unsupported native call %s\n", name); } }'
        while read -r s; do echo "static int seen_$s; void *$s(void) { obns_stub(\"$s\", &seen_$s); return 0; }"; done < stubs.txt
    } > source/obns_stubs.c
    make -j12 > link.log 2>&1 || { grep -E "error|undefined" link.log | head -30; exit 1; }
fi
grep -E "built \.\.\." link.log || true
}
link_nro

SD=$OUT/sd/switch/OBNS
mkdir -p "$SD/managed" "$SD/Data/Plugins"
cp OBNS.nro "$SD/"
cp "$OUT/managed"/*.dll "$SD/managed/"
cp "$NX/runner/obns.ini" "$SD/managed/"
cp "$ICU_NX_INSTALL_DIR/share/icu/77.1/icudt77l.dat" "$SD/managed/"
cp "$OUT/managed/OpenBveAts.dll" "$SD/Data/Plugins/"
ls -la "$SD/OBNS.nro"
du -sh "$SD/managed"
