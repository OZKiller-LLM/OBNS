#!/bin/sh
# OBNS: builds the native menu's library on a Mac (needs Xcode's command line tools): libOBNSMenuKit.dylib, for both Mac
# architectures, beside this script or in the folder given. Copy it beside OpenBve.exe (OBNS/GLFORMS.md).
set -e
cd "$(dirname "$0")"
out="${1:-.}"
swiftc -O -emit-library -module-name MenuKit -target arm64-apple-macos11 MenuKit.swift -o /tmp/libOBNSMenuKit-arm64.dylib
swiftc -O -emit-library -module-name MenuKit -target x86_64-apple-macos11 MenuKit.swift -o /tmp/libOBNSMenuKit-x86_64.dylib
lipo -create /tmp/libOBNSMenuKit-arm64.dylib /tmp/libOBNSMenuKit-x86_64.dylib -output "$out/libOBNSMenuKit.dylib"
rm -f /tmp/libOBNSMenuKit-arm64.dylib /tmp/libOBNSMenuKit-x86_64.dylib
echo "built $out/libOBNSMenuKit.dylib"
