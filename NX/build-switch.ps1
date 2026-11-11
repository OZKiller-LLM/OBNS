<#
.SYNOPSIS
    Builds an Android port project (and what it references) for the Switch's runtime, as a compile check on Windows.

.DESCRIPTION
    OBNS 1.32. The Android projects are built unchanged for net9.0 with NX/Switch.targets (Compat without Skia, C# 13,
    the game project as a library). Output goes to D:\dev\tools\nx-artifacts. Prints each distinct error once.

.PARAMETER Project
    The project file; the Android game project by default.
#>
param([string]$Project = (Join-Path $PSScriptRoot '..\Android\OpenBve.Android\OpenBve.Android.csproj'))

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path + '\'
$targets = Join-Path $PSScriptRoot 'Switch.targets'
$lines = dotnet build $Project -c Release -p:AndroidTfm=net9.0 -p:ObnsX86Tfm=net9.0 "-p:CustomAfterMicrosoftCommonTargets=$targets" `
    --artifacts-path D:\dev\tools\nx-artifacts -v q -nologo 2>&1
$errors = $lines | Where-Object { $_ -match ' error ' } | ForEach-Object { ($_ -replace '\s*\[[A-Z]:\\[^\]]*\]\s*$', '').Replace($root, '') } | Sort-Object -Unique
if ($errors) {
    $errors
    "$(@($errors).Count) distinct error(s)"
    exit 1
}
"Built: $(Split-Path -Leaf $Project)"
