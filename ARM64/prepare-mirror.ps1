<#
.SYNOPSIS
    Makes the ARM64 build's mirror of upstream (ARM64/build/upstream) build OpenBve.exe for Windows on ARM64.

.DESCRIPTION
    Run by OBNS/build-desktop.ps1 (-PrepareMirror) after OBNS's files are laid over the mirror. The mirror is the ARM64
    build's own, so these changes never reach upstream's files or the x64 build. OpenBve.exe:

      - targets .NET Framework 4.8.1, the first with ARM64 code: Windows runs a program for 4.8 or earlier as x64 under
        emulation, though 4.8.1 is installed (seen on a Snapdragon X1-26-100 laptop);
      - is built for ARM64 only, so that it never falls back to emulation;
      - no longer runs upstream's LBAHeader after building, which makes 32-bit copies of an AnyCPU program.

    The libraries and plugins stay AnyCPU for 4.8, which a 4.8.1 process loads natively. Win32PluginProxy stays x86, run
    by Windows' x86 emulation.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Mirror
)

$ErrorActionPreference = 'Stop'

function Edit-File([string]$Path, [scriptblock]$Change) {
    $text = [IO.File]::ReadAllText($Path)
    $changed = & $Change $text
    if ($changed -eq $text) { throw "Nothing to change in ${Path}: upstream's file is not as expected" }
    [IO.File]::WriteAllText($Path, $changed, (New-Object Text.UTF8Encoding($true)))
}

# OBNS's project files reach OBNS/shared from OBNS/build/upstream, four folders up from a project; this mirror is
# elsewhere (ARM64/build/upstream), so they are given the folder itself.
$shared = Join-Path (Split-Path -Parent $PSScriptRoot) 'OBNS\shared'
Get-ChildItem (Join-Path $Mirror 'source') -Recurse -Filter *.csproj | Where-Object { (Get-Content -Raw $_.FullName).Contains('..\..\..\..\shared\') } | ForEach-Object {
    Edit-File $_.FullName { param($text) $text.Replace('..\..\..\..\shared\', $shared + '\') }
}

$project = Join-Path $Mirror 'source\OpenBVE\OpenBve.csproj'
Edit-File $project {
    param($text)
    $text = $text.Replace('<TargetFrameworkVersion>v4.8</TargetFrameworkVersion>', '<TargetFrameworkVersion>v4.8.1</TargetFrameworkVersion>')
    $text.Replace('<PlatformTarget>AnyCPU</PlatformTarget>', '<PlatformTarget>ARM64</PlatformTarget>')
}

# Both OpenBve's and RouteViewer's projects run LBAHeader.
foreach ($name in 'OpenBVE\OpenBve.csproj', 'RouteViewer\RouteViewer.csproj') {
    Edit-File (Join-Path $Mirror "source\$name") {
        param($text)
        [regex]::Replace($text, '\s*<Exec Condition="[^"]*" Command="[^"]*LBAHeader\.exe[^"]*" />', '')
    }
}

$config = Join-Path $Mirror 'source\OpenBVE\App.config'
Edit-File $config {
    param($text)
    $text.Replace('sku=".NETFramework,Version=v4.8"', 'sku=".NETFramework,Version=v4.8.1"')
}

Write-Host 'Mirror prepared for Windows on ARM64: OpenBve.exe for .NET Framework 4.8.1, ARM64 only'
