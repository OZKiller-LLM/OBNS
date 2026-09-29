<#
.SYNOPSIS
  Builds OpenBVE for Android for distribution: a signed APK (and/or App Bundle) in dist\<version>\,
  with checksums and a read-me for the people installing it.

.EXAMPLE
  android\release.ps1 -PortRelease 1
  The release build, signed with the release key: dist\1.14.0.3-android.1\OpenBVE-1.14.0.3-android.1.apk

.EXAMPLE
  android\release.ps1 -PortRelease 2 -Beta
  The beta testers' build, with the data log (Documents/OpenBVE): OpenBVE-1.14.0.3-android.2-beta.apk

.EXAMPLE
  android\release.ps1 -PortRelease 1 -Format both
  The APK for sideloading and the App Bundle (.aab) for Google Play.

.NOTES
  The release key. Every published build must be signed with the same key: Android will not
  update an installed app from a file signed with another, so a lost key means every user must
  uninstall (losing the app's data) to move on. Make it once, keep the file and its password safe
  and backed up, and never put either in the source tree:

    New-Item -ItemType Directory -Force "$HOME\.openbve"
    & "$env:ProgramFiles\Android\Android Studio\jbr\bin\keytool.exe" -genkeypair -v `
      -keystore "$HOME\.openbve\openbve-release.jks" -alias openbve -keyalg RSA -keysize 4096 `
      -validity 36500 -dname "CN=The OpenBVE Android port contributors"

  The script asks for the password, or reads OPENBVE_KEYSTORE_PASS (and OPENBVE_KEY_PASS, which
  defaults to the same - keytool's PKCS12 stores use one password for both). -KeyStore or
  OPENBVE_KEYSTORE picks another file. -DebugKey builds with the SDK's debug key instead, for
  trying the packaging only: such files are marked "debugkey" and are not for handing out.
#>
param(
	# This port's release number for the upstream version: 1, 2, ... Each published build needs a
	# higher one than the last, as Android only updates to a higher version code.
	[Parameter(Mandatory = $true)][ValidateRange(1, 99)][int]$PortRelease,
	# The beta testers' build, with the data log.
	[switch]$Beta,
	# apk (sideloading), aab (Google Play) or both.
	[ValidateSet('apk', 'aab', 'both')][string]$Format = 'apk',
	# arm64 phones only, leaving out x86_64 (Chromebooks, emulators): a smaller file.
	[switch]$Arm64Only,
	[string]$KeyStore = $(if ($env:OPENBVE_KEYSTORE) { $env:OPENBVE_KEYSTORE } else { Join-Path $HOME '.openbve\openbve-release.jks' }),
	[string]$KeyAlias = 'openbve',
	# Sign with the SDK's debug key (packaging trials only).
	[switch]$DebugKey,
	# Build from clean rather than incrementally.
	[switch]$Clean
)

$ErrorActionPreference = 'Stop'
$android = $PSScriptRoot
$root = Split-Path -Parent $android
$project = Join-Path $android 'OpenBve.Android\OpenBve.Android.csproj'
$sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk'
$jbr = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr'
$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory | Sort-Object { [version]($_.Name -replace '[^0-9.].*$', '') } | Select-Object -Last 1
# A Java 8 runtime sits early on PATH on this machine; the signing tools need a current one.
$env:JAVA_HOME = $jbr
$env:PATH = (Join-Path $jbr 'bin') + ';' + $env:PATH

# --- the key ---
$buildArgs = @('build', $project, '-c', 'Release', "-p:PortRelease=$PortRelease", '-nologo', '-v:minimal')
$setPasswords = $false
if ($DebugKey -and $Beta) {
	Write-Warning 'Signing the beta with the SDK debug key: testers must uninstall it before the release-signed version.'
} elseif ($DebugKey) {
	Write-Warning 'Signing with the SDK debug key: for trying the packaging only, not for distribution.'
} else {
	if (-not (Test-Path $KeyStore)) {
		Write-Host "No release key at $KeyStore." -ForegroundColor Red
		Write-Host 'Make one (once; keep it and its password safe and backed up) - see the notes: Get-Help android\release.ps1 -Full'
		exit 1
	}

	if (-not $env:OPENBVE_KEYSTORE_PASS) {
		$secure = Read-Host "Password for $KeyStore" -AsSecureString
		$env:OPENBVE_KEYSTORE_PASS = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
		$setPasswords = $true
	}

	if (-not $env:OPENBVE_KEY_PASS) {
		$env:OPENBVE_KEY_PASS = $env:OPENBVE_KEYSTORE_PASS
		$setPasswords = $true
	}

	# Check the password opens the key now, rather than after minutes of building (the signing
	# step would fail then with only "java.exe exited with code 2").
	$check = & (Join-Path $jbr 'bin\keytool.exe') -list -keystore $KeyStore -alias $KeyAlias -storepass:env OPENBVE_KEYSTORE_PASS 2>&1
	if ($LASTEXITCODE -ne 0) {
		if ($setPasswords) {
			Remove-Item Env:OPENBVE_KEYSTORE_PASS, Env:OPENBVE_KEY_PASS -ErrorAction SilentlyContinue
		}

		Write-Host "That password does not open $KeyStore (or it has no key named '$KeyAlias')." -ForegroundColor Red
		Write-Host "keytool said: $(($check | Select-Object -Last 1) -as [string])"
		Write-Host 'Run the script again and type the password you chose when making the key.'
		exit 1
	}

	$buildArgs += "-p:OpenBveKeyStore=$KeyStore"
	$buildArgs += "-p:OpenBveKeyAlias=$KeyAlias"
}

if ($Beta) { $buildArgs += '-p:BetaLog=true' }
if ($Arm64Only) { $buildArgs += '-p:RuntimeIdentifiers=android-arm64' }
if ($Clean) { $buildArgs += '--no-incremental' }

$formats = if ($Format -eq 'both') { @('apk', 'aab') } else { @($Format) }
$built = @{}
try {
	foreach ($packageFormat in $formats) {
		Write-Host "Building the $packageFormat (release $PortRelease$(if ($Beta) { ', beta' }))..." -ForegroundColor Cyan
		$started = Get-Date
		& dotnet @buildArgs "-p:AndroidPackageFormat=$packageFormat"
		if ($LASTEXITCODE -ne 0) { throw "The $packageFormat build failed." }
		$file = Get-ChildItem (Join-Path $android 'OpenBve.Android\bin\Release') -Recurse -Filter "net.openbve-Signed.$packageFormat" |
			Where-Object { $_.LastWriteTime -ge $started.AddSeconds(-5) } | Sort-Object LastWriteTime | Select-Object -Last 1
		if (-not $file) { throw "The build made no signed $packageFormat." }
		$built[$packageFormat] = $file.FullName
	}
} finally {
	if ($setPasswords) {
		Remove-Item Env:OPENBVE_KEYSTORE_PASS, Env:OPENBVE_KEY_PASS -ErrorAction SilentlyContinue
	}
}

# --- check what came out ---
$apkForInfo = $built['apk']
if ($apkForInfo) {
	$badging = & (Join-Path $buildTools.FullName 'aapt2.exe') dump badging $apkForInfo
	$versionName = [regex]::Match(($badging -join "`n"), "versionName='([^']+)'").Groups[1].Value
	$versionCode = [regex]::Match(($badging -join "`n"), "versionCode='([^']+)'").Groups[1].Value
	$abis = [regex]::Match(($badging -join "`n"), "native-code: (.+)").Groups[1].Value -replace "'", ''
	$certificate = & (Join-Path $buildTools.FullName 'apksigner.bat') verify --print-certs $apkForInfo 2>&1
	if ($LASTEXITCODE -ne 0) { throw "The APK's signature does not verify:`n$certificate" }
	$signer = ($certificate | Select-String 'certificate DN:' | Select-Object -First 1).ToString() -replace '^.*certificate DN: ', ''
	$fingerprint = ($certificate | Select-String 'SHA-256 digest:' | Select-Object -First 1).ToString() -replace '^.*SHA-256 digest: ', ''
} else {
	[xml]$props = Get-Content $project
	$upstream = ($props.Project.PropertyGroup | Where-Object { $_.UpstreamVersion } | Select-Object -First 1).UpstreamVersion
	$versionName = "$upstream-android.$PortRelease$(if ($Beta) { '-beta' })"
	$versionCode = [int]($upstream -replace '\.', '') * 100 + $PortRelease
	$abis = if ($Arm64Only) { 'arm64-v8a' } else { 'arm64-v8a x86_64' }
	$verify = & (Join-Path $jbr 'bin\jarsigner.exe') -verify -certs $built['aab'] 2>&1
	if ($LASTEXITCODE -ne 0 -or -not ($verify -match 'jar verified')) { throw "The bundle's signature does not verify:`n$verify" }
	$signer = ($verify | Select-String 'X.509, ' | Select-Object -First 1).ToString().Trim() -replace '^.*X\.509, ', ''
	$fingerprint = '(see jarsigner -verify -certs)'
}

if ($DebugKey) { $versionName += '-debugkey' }

# --- dist\<version>\ ---
$dist = Join-Path $root "dist\$versionName"
if (Test-Path $dist) {
	Write-Warning "$dist exists; its files are replaced. A build handed out already must not be replaced by another with the same version."
}
New-Item -ItemType Directory -Force $dist | Out-Null
# Only this run's packages, so SHA256SUMS.txt covers everything in the folder.
Get-ChildItem $dist -File | Where-Object { $_.Extension -in '.apk', '.aab' } | Remove-Item
$outputs = @()
foreach ($packageFormat in $built.Keys) {
	$target = Join-Path $dist "OpenBVE-$versionName.$packageFormat"
	Copy-Item $built[$packageFormat] $target -Force
	$outputs += $target
}

Copy-Item (Join-Path $root 'LICENSE') (Join-Path $dist 'LICENSE.txt') -Force
Copy-Item (Join-Path $root 'RELEASE_NOTES.md') (Join-Path $dist 'RELEASE_NOTES.txt') -Force

$sums = foreach ($file in $outputs) {
	'{0}  {1}' -f (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
}
Set-Content (Join-Path $dist 'SHA256SUMS.txt') $sums -Encoding ascii

$betaSection = if ($Beta) { @"

This is a beta build
--------------------
It keeps a data log of each session, for finding problems: the route and train, the graphics
backend (OpenGL ES or Vulkan), phone or desktop mode, frame rates, load time and every error
message, with the phone's model, Android version, memory and temperature state. No personal data
(no location, accounts, contacts, device identifiers or names of paired devices).

The logs are plain text files named OpenBVE_beta_<date>_<time>_game.txt in Documents/OpenBVE -
on the SD card if the phone has one, otherwise in the phone's own storage (open it with any file
manager). Read them if you like, and send them with your report. Nothing is sent anywhere by the
app itself.
"@ } else { '' }

$debugSection = if ($DebugKey -and $Beta) { @"

Signed with a development key for testing. When the release version comes out, uninstall this
beta first (routes, trains and logs in Documents/OpenBVE are kept).

"@ } elseif ($DebugKey) { @"

NOT FOR DISTRIBUTION: signed with the Android SDK's debug key, for trying the packaging only.
"@ } else { '' }

$readme = @"
OpenBVE for Android $versionName
$('=' * (20 + $versionName.Length))

OpenBVE $($versionName -replace '-android.*$', ''), the free train simulator, ported to Android.
Android 7.0 or later with OpenGL ES 3.0; built for $abis.
Version code $versionCode. Signed by: $signer
Signing certificate SHA-256: $fingerprint
$debugSection$betaSection
Installing
----------
1. Copy OpenBVE-$versionName.apk to the phone (or download it there) and open it.
2. Android asks to allow installing apps from that source (the file manager or browser): allow it.
3. Open OpenBVE and allow it access to all files when asked: routes and trains then live in
   Documents/OpenBVE (Railway, Train) on the SD card, or on the phone if it has no SD card. Copy
   them there, or install them as packages under Package Management.

Updating: install the new file over the old one; routes, trains and settings stay.

If Android says the app conflicts with an existing one, the installed copy was signed with another
key (a development build). Uninstall it first. Documents/OpenBVE is kept, but uninstalling deletes
the app's own folder, Android/data/net.openbve, where earlier builds kept routes and trains - copy
any there to Documents/OpenBVE beforehand.

What is new, and the known limitations: RELEASE_NOTES.txt.
Check the download: the SHA-256 of each file is in SHA256SUMS.txt.

Licences
--------
The port is under the Simplified BSD License and OpenBVE under its own terms; see LICENSE.txt,
and in the app About > Licences for every included library. The app includes OpenAL Soft,
unmodified, under the GNU LGPL 2: as built by the MonoGame.Library.OpenAL 1.25.2.1 package,
from https://github.com/kcat/openal-soft (packaging: https://github.com/MonoGame/MonoGame.Library.OpenAL).
"@
# UTF-8 without a byte order mark (Windows PowerShell's utf8 adds one), CRLF for Notepad.
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $dist 'README.txt'), ($readme -replace "`r?`n", "`r`n") + "`r`n", $utf8)
[IO.File]::AppendAllText((Join-Path $root 'dist\RELEASES.txt'),
	("{0:yyyy-MM-dd HH:mm}  {1}  code {2}  {3}  {4}`r`n" -f (Get-Date), $versionName, $versionCode, ($built.Keys -join '+'), $fingerprint), $utf8)

Write-Host ''
Write-Host "Done: $dist" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { '  {0,-44} {1,8:N1} MB' -f $_.Name, ($_.Length / 1MB) }
Write-Host "  version $versionName (code $versionCode), $abis, signed by $signer"
