<#
.SYNOPSIS
    Builds upstream OpenBVE's desktop program (Windows x64) with OBNS's changes in it.

.DESCRIPTION
    OpenBVE/ is upstream exactly as published and is never edited, nor built in place (its projects
    write obj/ and bin_release/ beside their sources). So the build works on a mirror:

      1. OpenBVE/ is mirrored into OBNS/build/upstream/ (the restored packages, obj and bin folders
         are kept between runs, so builds after the first are incremental);
      2. OBNS/overrides/ is laid over the mirror: each file there replaces upstream's file at the same
         path (a copy of upstream's with OBNS's change, headed "OBNS: a copy of upstream's ...");
      3. upstream's NuGet packages are restored and its solution built: with Visual Studio's MSBuild
         when Visual Studio has the C# compiler (the .NET desktop workload), else with the .NET SDK's
         (dotnet msbuild), which builds upstream's .NET Framework 4.8 projects as well.

    The program is OBNS/build/upstream/bin_release/OpenBve.exe (AnyCPU, not preferring 32-bit, as
    upstream: a 64-bit process on Windows x64), with upstream's data folders beside it.

.PARAMETER Configuration
    Release (the default) or Debug.

.PARAMETER Clean
    Deletes the mirror first, for a build from nothing.
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$upstream = Join-Path $root 'OpenBVE'
$build = Join-Path $PSScriptRoot 'build'
$mirror = Join-Path $build 'upstream'
$overrides = Join-Path $PSScriptRoot 'overrides'

if (-not (Test-Path (Join-Path $upstream 'OpenBVE.sln'))) {
    throw "Upstream's OpenBVE.sln was not found in $upstream"
}

if ($Clean -and (Test-Path $mirror)) {
    Write-Host 'Removing the previous mirror...'
    Remove-Item -Recurse -Force $mirror
}

# 1. The mirror. Robocopy's exit codes below 8 are success (they say what was copied). The restored
#    NuGet folder is excluded by its full path: by name, upstream's source/OpenBveApi/Packages would go too.
Write-Host "Mirroring $upstream"
robocopy $upstream $mirror /MIR /XD bin_release bin_debug obj .git .vs (Join-Path $mirror 'packages') /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Mirroring upstream failed (robocopy $LASTEXITCODE)" }

# 2. OBNS's replacements for upstream files.
if (Test-Path $overrides) {
    $count = (Get-ChildItem -Recurse -File $overrides | Measure-Object).Count
    Write-Host "Laying $count OBNS file(s) over the mirror"
    robocopy $overrides $mirror /E /IS /IT /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Laying OBNS's files over the mirror failed (robocopy $LASTEXITCODE)" }
}

# 3. MSBuild: Visual Studio's if it has the C# compiler, else the .NET SDK's.
$msbuild = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path $vswhere) {
    # (No -requires: a Community install may not list the MSBuild component, though it has MSBuild.)
    $candidate = & $vswhere -latest -products * -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    if ($candidate -and (Test-Path (Join-Path (Split-Path $candidate) 'Roslyn\Microsoft.CSharp.Core.targets'))) {
        $msbuild = $candidate
    }
}

function Invoke-MSBuild([string[]]$Arguments) {
    if ($msbuild) {
        & $msbuild @Arguments
    }
    else {
        & dotnet msbuild @Arguments
    }
}

if ($msbuild) { Write-Host "MSBuild: $msbuild" } else { Write-Host 'MSBuild: the .NET SDK (dotnet msbuild)' }
$solution = Join-Path $mirror 'OpenBVE.sln'

Write-Host 'Restoring upstream NuGet packages'
Invoke-MSBuild @($solution, '-t:restore', '-p:RestorePackagesConfig=true', "-p:Configuration=$Configuration", '-v:minimal', '-nologo')
if ($LASTEXITCODE -ne 0) { throw "Restoring the packages failed ($LASTEXITCODE)" }

Write-Host "Building OpenBVE ($Configuration)"
Invoke-MSBuild @($solution, "-p:Configuration=$Configuration", '-p:Platform=Any CPU', '-m', '-v:minimal', '-nologo')
if ($LASTEXITCODE -ne 0) { throw "The build failed ($LASTEXITCODE)" }

$exe = Join-Path $mirror ('bin_' + $Configuration.ToLowerInvariant() + '\OpenBve.exe')
Write-Host "Built: $exe"
