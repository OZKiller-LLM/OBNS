#!/bin/bash
# OBNS 1.31N: builds NX/hello on Linux (WSL's Ubuntu), from mono-nx's native/aot/build_aot.sh (MIT) in Release:
# the C# built, trimmed by ILLink against the libnx framework, each assembly compiled by Mono's AOT cross-compiler
# into a static object, then linked by devkitA64 into OBNS-hello.nro.
#
# Needs DEVKITPRO, MONO_NX_ROOT (the dotnet_runtime build) and ICU_NX_INSTALL_DIR (NX/README.md).
set -e
cd "$(dirname "$0")"
: "${DEVKITPRO:?}" "${MONO_NX_ROOT:?}" "${ICU_NX_INSTALL_DIR:?}"
DOTNET=$MONO_NX_ROOT/.dotnet/dotnet
CONFIG=Release

rm -rf output managed/bin managed/obj
echo "Building the C#..."
$DOTNET build managed/hello.csproj -c Release -v q -nologo

echo "Trimming..."
ILLINK=$MONO_NX_ROOT/artifacts/bin/Mono.Linker/$CONFIG/net9.0/illink.dll
ILLINK_CFG=$MONO_NX_ROOT/src/mono/System.Private.CoreLib/src/ILLink/ILLink.Descriptors.xml
ILLINK_CFG1=$MONO_NX_ROOT/src/mono/System.Private.CoreLib/src/ILLink/ILLink.LinkAttributes.xml
LIB_ROOT=$MONO_NX_ROOT/artifacts/bin/mono/libnx.arm64.$CONFIG/
FRAMEWORK_ROOT=$MONO_NX_ROOT/artifacts/bin/runtime/net9.0-libnx-$CONFIG-arm64/
$DOTNET $ILLINK -x $ILLINK_CFG -x $ILLINK_CFG1 --feature System.Resources.UseSystemResourceKeys true --feature System.Globalization.Invariant true \
    -d $LIB_ROOT -d $FRAMEWORK_ROOT --trim-mode link -a managed/bin/Release/net9.0/hello.dll -out output

MONO_COMPILER=$MONO_NX_ROOT/artifacts/bin/mono/linux.x64.$CONFIG/cross/linux-x64/libnx-arm64/mono-aot-cross
export PATH=$PATH:$DEVKITPRO/devkitA64/bin/
# LLVM=1 (the default when Mono's LLVM tools are there): methods compiled by LLVM, as NroForge does, with
# position-independent code (llc's static relocation doesn't link into an NRO). LLVM=0: Mono's own code generator.
LLVM_TOOLS=$MONO_NX_ROOT/artifacts/obj/mono/linux.x64.$CONFIG/llvm/x64/bin
if [ -z "$LLVM" ]; then [ -x "$LLVM_TOOLS/llc" ] && LLVM=1 || LLVM=0; fi
echo "AOT compiling (LLVM=$LLVM)..."
: > mono_aot.log
for file in output/*.dll; do
    name=$(basename "$file")
    options=full,static,direct-icalls,direct-pinvoke,tool-prefix=aarch64-none-elf-,outfile=output/$name.o
    if [ "$LLVM" = 1 ]; then
        options=$options,llvm,llvmllc=-relocation-model=pic,llvm-path=$LLVM_TOOLS/,llvm-outfile=output/$name-llvm.o
        $MONO_COMPILER --llvm --path=output/ --aot=$options "$file" >> mono_aot.log 2>&1 \
            || { echo "AOT failed on $file"; tail -20 mono_aot.log; exit 1; }
    else
        $MONO_COMPILER --path=output/ --aot=$options "$file" >> mono_aot.log 2>&1 \
            || { echo "AOT failed on $file"; tail -20 mono_aot.log; exit 1; }
    fi
done
grep -h "LLVM:" mono_aot.log | head -3 || true

cp output/*.dll romfs/
cp $ICU_NX_INSTALL_DIR/share/icu/77.1/icudt77l.dat romfs/
grep -r "Linking symbol:" mono_aot.log | sed "s/Linking symbol: '\([^']*\)'\./STATIC_MONO_SYM(\1);/" > source/mono_symbols.h
echo "$(wc -l < source/mono_symbols.h) assemblies compiled"

echo "Linking..."
# mono-nx's start-up code (MIT), copied in: the Makefile takes source folders relative to this one.
rm -rf nxshared && cp -r "$MONO_NX_ROOT/../native/shared" nxshared
make -j12 2>&1 | grep -E "error|warning: undefined|built \.\.\." || true
ls -la OBNS-hello.nro
