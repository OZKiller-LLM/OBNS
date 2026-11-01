#!/bin/bash
# OBNS: builds OpenBVE.app and its disk image on a Mac (needs Xcode's command line tools), from upstream's app bundle and a
# finished desktop build, as upstream's Makefile does.
#
#   make-app.sh <program folder> <MacBundle.tgz> <MenuKit folder> <licence file> <licenses folder> <version> <output folder>
#
# Ad-hoc signed: Apple silicon needs a signature to run. Without a Developer ID, Gatekeeper refuses it until the user
# chooses Open Anyway in Privacy & Security.
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
program=$1; bundle=$2; menukit=$3; licence=$4; licenses=$5; version=$6; out=$7

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
tar -C "$work" -xzf "$bundle"
find "$work" -name '._*' -delete
app="$work/OpenBVE.app"

cp -R "$program"/. "$app/Contents/Resources/"
rm -rf "$app/Contents/Resources/DevTools"
find "$app/Contents/Resources" -name '*.pdb' -delete
test -f "$app/Contents/Resources/OpenTK.dll.config"

sh "$menukit/build.sh" "$app/Contents/Resources"

cp "$here/OpenBVE" "$app/Contents/MacOS/OpenBVE"
chmod 755 "$app/Contents/MacOS/OpenBVE"

# BSD-2's second condition, and the libraries' own: a binary release carries the notices.
cp "$licence" "$app/Contents/Resources/LICENSE.txt"
cp -R "$licenses" "$app/Contents/Resources/licenses"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleIdentifier</key>
	<string>net.openbve.obns</string>
	<key>CFBundleExecutable</key>
	<string>OpenBVE</string>
	<key>CFBundleIconFile</key>
	<string>OpenBVE.icns</string>
	<key>CFBundleName</key>
	<string>OpenBVE</string>
	<key>CFBundlePackageType</key>
	<string>APPL</string>
	<key>CFBundleShortVersionString</key>
	<string>$version</string>
	<key>CFBundleVersion</key>
	<string>$version</string>
	<key>NSHighResolutionCapable</key>
	<true/>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$app"
codesign --verify --deep "$app"

mkdir -p "$out"
rm -rf "$out/OpenBVE.app" "$out/OpenBVE-$version.dmg"
cp -R "$app" "$out/OpenBVE.app"
hdiutil create "$out/OpenBVE-$version.dmg" -volname "OpenBVE" -fs HFS+ -srcfolder "$app"
hdiutil verify "$out/OpenBVE-$version.dmg"
shasum -a 256 "$out/OpenBVE-$version.dmg"
