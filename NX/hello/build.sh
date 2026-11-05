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
$DOTNET $ILLINK -x $ILLINK_CFG -x $ILLINK_CFG1 --feature System.Resources.UseSystemResourceKeys true \
    -d $LIB_ROOT -d $FRAMEWORK_ROOT --trim-mode link -a managed/bin/Release/net9.0/hello.dll -out output

echo "AOT compiling..."
MONO_COMPILER=$MONO_NX_ROOT/artifacts/bin/mono/linux.x64.$CONFIG/cross/linux-x64/libnx-arm64/mono-aot-cross
export PATH=$PATH:$DEVKITPRO/devkitA64/bin/
: > mono_aot.log
for file in output/*.dll; do
    $MONO_COMPILER --path=output/ --aot=full,static,direct-icalls,direct-pinvoke,tool-prefix=aarch64-none-elf- "$file" >> mono_aot.log 2>&1 \
        || { echo "AOT failed on $file"; tail -20 mono_aot.log; exit 1; }
done

cp output/*.dll romfs/
cp $ICU_NX_INSTALL_DIR/share/icu/77.1/icudt77l.dat romfs/
grep -r "Linking symbol:" mono_aot.log | sed "s/Linking symbol: '\([^']*\)'\./STATIC_MONO_SYM(\1);/" > source/mono_symbols.h
echo "$(wc -l < source/mono_symbols.h) assemblies compiled"

echo "Linking..."
# mono-nx's start-up code (MIT), copied in: the Makefile takes source folders relative to this one.
rm -rf nxshared && cp -r "$MONO_NX_ROOT/../native/shared" nxshared
make -j12 2>&1 | grep -E "error|warning: undefined|built \.\.\." || true
ls -la OBNS-hello.nro
