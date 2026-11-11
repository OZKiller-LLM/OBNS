#!/bin/bash
# OBNS 1.32: Mono for libnx, from mono-nx's build_mono.sh in Release, with NX/patches applied and an LLVM-enabled AOT
# cross-compiler. The libnx runtime itself is built without LLVM: LLVM-compiled AOT code needs nothing more from it
# on ARM64 (ENABLE_LLVM_RUNTIME is WebAssembly's). NX/README.md has the set-up: DEVKITPRO, then mono-nx's env.sh
# (MONO_NX_ROOT, ICU_NX_INSTALL_DIR), and ICU built first.
set -e
: "${DEVKITPRO:?}" "${MONO_NX_ROOT:?}"
PATCHES=$(cd "$(dirname "$0")" && pwd)/patches
cd "$MONO_NX_ROOT"
if git apply --check "$PATCHES/dotnet_runtime-release.patch" 2>/dev/null; then
    git apply "$PATCHES/dotnet_runtime-release.patch" && echo "== patches applied"
else
    echo "== patches already in (or not applicable: check git status)"
fi
log=~/switch-build/mono-build.log
mkdir -p ~/switch-build
: > $log
fail() { echo FAILED; grep -E " error |error MSB|error CS|: error|CMake Error" $log | tail -20; exit 1; }
echo "== runtime, CoreLib, native libraries and libraries for libnx"
ROOTFS_DIR=$DEVKITPRO ./build.sh --subset mono.runtime+mono.corelib+libs.native+libs.sfx --cross -a arm64 --os libnx -c Release \
    /p:MonoEnableLLVM=false /p:MonoAOTEnableLLVM=false >> $log 2>&1 || fail
echo "== AOT offsets for libnx (needs libclang; Ubuntu 26.04's is llvm-21's)"
ROOTFS_DIR=$DEVKITPRO ./build.sh -s mono.aotcross /p:MonoGenerateOffsetsOSGroups=libnx \
    /p:MonoLibClang=${MONO_LIBCLANG:-/usr/lib/llvm-21/lib/libclang.so} -c Release >> $log 2>&1 || fail
echo "== the PC's AOT cross-compiler, with LLVM (Microsoft's Mono LLVM packages, fetched by the build)"
ROOTFS_DIR= ./build.sh -s mono -c Release /p:AotHostArchitecture=x64 /p:AotHostOS=linux /p:MonoCrossAOTTargetOS=libnx \
    /p:SkipMonoCrossJitConfigure=true /p:BuildMonoAOTCrossCompilerOnly=true /p:BuildMonoAOTCrossCompiler=true \
    /p:MonoAOTEnableLLVM=true >> $log 2>&1 || fail
echo "== done"
