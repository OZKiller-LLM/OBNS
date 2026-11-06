<#
.SYNOPSIS
    Draws the Switch's menu into PNG files on Windows (OBNS 1.31N), for checking its layout without the console.

.PARAMETER Content
    A content folder (Railway\Route and Train within) for the Start page's lists. Use sample content, not private packs.

.PARAMETER Output
    Where the PNG files (and the menu's options file) are written.

.PARAMETER Script
    The presses and shots (see Program.cs); script.txt beside this file by default.
#>
param(
    [Parameter(Mandatory = $true)][string]$Content,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Script = (Join-Path $PSScriptRoot 'script.txt'))

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$targets = Join-Path $root 'NX\Switch.targets'
$project = Join-Path $PSScriptRoot 'MenuPreview.csproj'
$built = dotnet build $project -c Release -p:AndroidTfm=net9.0 -p:ObnsX86Tfm=net9.0 "-p:CustomAfterMicrosoftCommonTargets=$targets" `
    --artifacts-path D:\dev\tools\nx-preview -v q -nologo 2>&1
if ($LASTEXITCODE -ne 0) { $built | Where-Object { $_ -match ' error ' } | Sort-Object -Unique; exit 1 }
& D:\dev\tools\nx-preview\bin\MenuPreview\release\MenuPreview.exe (Join-Path $root 'OpenBVE\assets') $Content $Output $Script
