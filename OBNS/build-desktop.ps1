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

.PARAMETER Package
    Also zips the program (without debug symbols) into OBNS/build/dist/, named after the release in
    OBNS's AssemblyInfo.tt: OpenBVE-<release>-OBNS-win-x64.zip.
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean,
    [switch]$Package
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

# Upstream's plugin projects move their output into Data\Plugins (or Data\InputDevicePlugins) after
# building. The .NET SDK's Move will not replace the copy a previous build left there (Visual Studio's
# does), so those copies are removed first; each project then copies its output out again and moves it.
$output = Join-Path $mirror ('bin_' + $Configuration.ToLowerInvariant())
if (Test-Path $output) {
    Get-ChildItem (Join-Path $mirror 'source') -Recurse -Filter *.csproj | ForEach-Object {
        $project = Get-Content -Raw $_.FullName
        $move = [regex]::Match($project, 'DestinationFolder="\$\(TargetDir\)(Data\\[^"]+?)\\?"')
        $name = [regex]::Match($project, '<AssemblyName>([^<]+)</AssemblyName>')
        if ($move.Success -and $name.Success) {
            $folder = Join-Path $output $move.Groups[1].Value
            if (Test-Path $folder) {
                # Only what the project moves (its .dll, .pdb and .xml): the .dll.config files beside them are
                # upstream's assets, copied by several projects at once.
                foreach ($extension in '.dll', '.pdb', '.xml') {
                    $file = Join-Path $folder ($name.Groups[1].Value + $extension)
                    if (Test-Path $file) { Remove-Item -Force $file }
                }
            }
        }
    }
}

Write-Host "Building OpenBVE ($Configuration)"
# Upstream's OpenBve project writes a 32-bit copy, OpenBve-32.exe, after building, and reports a failure ("Failed to
# write 32-bit executable") without failing the build. Just after OpenBve.exe is rewritten it is often still open
# elsewhere (a virus scan), and the copy is not made properly; building again, once it is free, makes it.
$log = @(Invoke-MSBuild @($solution, "-p:Configuration=$Configuration", '-p:Platform=Any CPU', '-m', '-v:minimal', '-nologo') | ForEach-Object { Write-Host $_; $_ })
if ($LASTEXITCODE -ne 0) { throw "The build failed ($LASTEXITCODE)" }
if ($log -match 'Failed to write 32-bit executable') {
    Write-Host 'OpenBve-32.exe was not written; building again'
    Start-Sleep -Seconds 5
    Invoke-MSBuild @($solution, "-p:Configuration=$Configuration", '-p:Platform=Any CPU', '-m', '-v:minimal', '-nologo')
    if ($LASTEXITCODE -ne 0) { throw "The build failed ($LASTEXITCODE)" }
}

$exe = Join-Path $output 'OpenBve.exe'

Write-Host "Built: $exe"

if ($Package) {
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion
    $dist = Join-Path $build 'dist'
    $staging = Join-Path $build 'package'
    $zip = Join-Path $dist "OpenBVE-$version-win-x64.zip"
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory -Force $dist | Out-Null
    # The program and its data, as built; debug symbols stay behind.
    robocopy $output $staging /E /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Staging the package failed (robocopy $LASTEXITCODE)" }
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $staging
    Write-Host ("Packaged: $zip ({0:N0} MB)" -f ((Get-Item $zip).Length / 1MB))
}
