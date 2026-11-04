<#
.SYNOPSIS
    Builds OBNS's desktop program for Windows on ARM64, run natively (ARM64/README.md).

.DESCRIPTION
    The same build as OBNS/build-desktop.ps1, in its own folder (ARM64/build), with ARM64/prepare-mirror.ps1's changes:
    OpenBve.exe for .NET Framework 4.8.1 and ARM64 only. Then ARM64/dependencies/arm64 (OpenAL Soft for ARM64) is copied
    beside the program, where OBNS's sound manager looks in an ARM64 process.

    The program is ARM64/build/upstream/bin_release/OpenBve.exe. It runs only on ARM64 Windows with .NET Framework 4.8.1
    (Windows 11 has it from 22H2).

.PARAMETER Configuration
    Release (the default) or Debug.

.PARAMETER Clean
    Deletes the ARM64 mirror first, for a build from nothing.

.PARAMETER Package
    Also zips the program (without debug symbols) into ARM64/build/dist/: OpenBVE-<release>-OBNS-win-arm64.zip.
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$build = Join-Path $PSScriptRoot 'build'

& (Join-Path $root 'OBNS\build-desktop.ps1') -Configuration $Configuration -Clean:$Clean -BuildFolder $build -PrepareMirror (Join-Path $PSScriptRoot 'prepare-mirror.ps1') -NoThirtyTwoBit

$output = Join-Path $build ('upstream\bin_' + $Configuration.ToLowerInvariant())
$exe = Join-Path $output 'OpenBve.exe'

# The PE header's machine: ARM64 (0xAA64), not AnyCPU's 0x14C.
$stream = [IO.File]::OpenRead($exe)
try {
    $reader = New-Object IO.BinaryReader($stream)
    $stream.Seek(0x3C, 'Begin') | Out-Null
    $stream.Seek($reader.ReadInt32() + 4, 'Begin') | Out-Null
    $machine = $reader.ReadUInt16()
}
finally { $stream.Dispose() }
if ($machine -ne 0xAA64) { throw ('OpenBve.exe was built for machine 0x{0:X4}, not ARM64' -f $machine) }
# 32-bit copies an earlier build may have left: an ARM64 program has none.
Get-ChildItem $output -Filter '*-32.exe*' | Remove-Item -Force

# OpenAL Soft for ARM64 (ARM64/dependencies/README.md). Without it the game stops as it starts, as upstream's does
# without its OpenAL: "The required system library ... was not found".
$dependencies = Join-Path $PSScriptRoot 'dependencies\arm64'
if (-not (Test-Path (Join-Path $dependencies 'OpenAL32.dll'))) { throw 'ARM64/dependencies/arm64/OpenAL32.dll is missing: the game would not start' }
New-Item -ItemType Directory -Force (Join-Path $output 'arm64') | Out-Null
Copy-Item (Join-Path $dependencies '*.dll') (Join-Path $output 'arm64') -Force

Write-Host "Built for ARM64: $exe"

if ($Package) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    $dist = Join-Path $build 'dist'
    $staging = Join-Path $build 'package'
    $zip = Join-Path $dist "OpenBVE-$version-win-arm64.zip"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $dist | Out-Null
    # The program and its data, as built; debug symbols stay behind, and so do the x64 DLLs an ARM64 process can't load.
    robocopy $output $staging /E /XF *.pdb /XD (Join-Path $output 'x64') /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Staging the package failed (robocopy $LASTEXITCODE)" }
    if (Test-Path $zip) { Remove-Item -Force $zip }
    # BSD-2's second condition, and the libraries' own (OpenAL Soft's LGPL among them): a binary release carries the notices.
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE.txt') -Force
    Copy-Item (Join-Path $root 'OpenBVE\licenses') (Join-Path $staging 'licenses') -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot 'dependencies\README.md') (Join-Path $staging 'licenses\OpenALSoft-ARM64.md') -Force
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $staging
    Write-Host ("Packaged: $zip ({0:N0} MB)" -f ((Get-Item $zip).Length / 1MB))
}
