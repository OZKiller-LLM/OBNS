param(
    [Parameter(Mandatory = $true)][string]$Root,   # the folder holding build\, TML\, EAL\ and Uchibo\
    [string[]]$Bundles = @('Uchibo', 'EAL', 'TML'),
    [string[]]$Renderers = @('opengl', 'vulkan', 'direct3d9', 'direct3d11', 'direct3d12'),
    [string[]]$Shadows = @('Off', 'High'),
    [int]$Stationary = 40,   # seconds standing, after the load
    [int]$Drive = 150,       # seconds under the AI driver
    [int]$LoadTimeout = 1200
)
# OBNS benchmark for one PC: each route and train on each renderer, with shadows off and on High, from a fresh settings folder.
# Keys go to the game's own window (PostMessage), so nothing else on the desktop receives them. No screen capture: in-game
# snapshots only. Results and a summary go to <Root>\results-<time>. Installs nothing and changes no system setting.
$ErrorActionPreference = 'Continue'
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Bench {
public delegate bool EnumProc(IntPtr h, IntPtr p);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
[DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
[DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
public static IntPtr Largest(uint pid) { IntPtr best = IntPtr.Zero; long area = 0;
  EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q); if (q == pid && IsWindowVisible(h)) { RECT r; GetWindowRect(h, out r); long a = (long)(r.R - r.L) * (r.B - r.T); if (a > area) { area = a; best = h; } } return true; }, IntPtr.Zero); return best; }
public static void Key(IntPtr h, uint vk, bool down) { uint scan = MapVirtualKey(vk, 0); long l = 1 | ((long)scan << 16); if (!down) l |= 0xC0000000L; PostMessage(h, down ? 0x100u : 0x101u, (IntPtr)vk, (IntPtr)l); } }
"@
function CtrlA($h) { [Bench]::Key($h, 0x11, $true); Start-Sleep -Milliseconds 100; [Bench]::Key($h, 0x41, $true); Start-Sleep -Milliseconds 120; [Bench]::Key($h, 0x41, $false); Start-Sleep -Milliseconds 100; [Bench]::Key($h, 0x11, $false) }
function Tap($h, $vk, $n) { for ($i = 0; $i -lt $n; $i++) { [Bench]::Key($h, $vk, $true); Start-Sleep -Milliseconds 120; [Bench]::Key($h, $vk, $false); Start-Sleep -Milliseconds 200 } }

$bin = Join-Path $Root 'build'
$content = @{
    TML    = @{ Route = "$Root\TML\Railway\Route\TMLDN 20144.csv"; Train = "$Root\TML\Train\MTR TML SP1900 EMU ver2022" }
    EAL    = @{ Route = "$Root\EAL\Railway\Route\EAL2023\" + [char]0x6771 + [char]0x9435 + [char]0x7DDA + [char]0x5357 + [char]0x884C + " (" + [char]0x7F85 + [char]0x6E56 + [char]0x2192 + [char]0x91D1 + [char]0x9418 + ").csv"; Train = "$Root\EAL\Train\MTR R-Train EMU 2023" }
    Uchibo = @{ Route = "$Root\Uchibo\Scenarios\Uchibo_3793F_ATS-P.txt"; Train = "$Root\Uchibo\Scenarios\Uchibo20\E217r\Vehicle_3793F.txt" }
}
$results = Join-Path $Root ('results-' + (Get-Date -Format yyyyMMdd-HHmmss))
$settings = Join-Path $results 'Settings'
New-Item -ItemType Directory -Force "$settings\1.5.0" | Out-Null
$fs = Join-Path $results 'filesystem.cfg'
[IO.File]::WriteAllText($fs, "Version=2`r`nData=`$[AssemblyFolder]\Data`r`nSettings=$settings`r`n")
$summary = Join-Path $results 'summary.csv'
'bundle,renderer,shadows,drew,load_s,standing_ms,standing_fps,standing_worst_ms,driving_ms,driving_fps,driving_worst_ms,private_GB,status' | Set-Content $summary -Encoding UTF8
$env:OBNS_FRAMESTATS = '1'; $env:OBNS_GPUSTATS = '1'; $env:OBNS_SNAPSHOT_EVERY = '60'; $env:OBNS_MEMSTATS = '0'

function Stats($lines) {
    $values = foreach ($l in $lines) { if ($l -match 'frame stats: \d+ frames, ([\d.]+) ms a frame \(([\d.]+) fps\), worst ([\d.]+) ms') { [pscustomobject]@{ ms = [double]$Matches[1]; fps = [double]$Matches[2]; worst = [double]$Matches[3] } } }
    if (-not $values) { return @('', '', '') }
    $sorted = @($values | Sort-Object ms)
    $mid = $sorted[[int][Math]::Floor($sorted.Count / 2)]
    return @($mid.ms, $mid.fps, (($values | Measure-Object worst -Maximum).Maximum))
}

foreach ($bundle in $Bundles) {
    $c = $content[$bundle]
    if (-not (Test-Path -LiteralPath $c.Route) -or -not (Test-Path -LiteralPath $c.Train)) { "$bundle,,,,,,,,,,,,content not found" | Add-Content $summary -Encoding UTF8; continue }
    foreach ($shadow in $Shadows) {
        foreach ($renderer in $Renderers) {
            $name = "$bundle-$renderer-$shadow"
            Write-Host "--- $name"
            $text = "[display]`r`npreferNativeBackend = true`r`nmode = window`r`nvsync = false`r`nwindowWidth = 1920`r`nwindowHeight = 1080`r`n[quality]`r`nshadowresolution = $shadow`r`nshadowdrawdistance = Medium`r`nshadowcascades = 3`r`n"
            [IO.File]::WriteAllText("$settings\1.5.0\options.cfg", $text)
            [IO.File]::WriteAllText("$settings\1.5.0\obns.cfg", "chosen = true`r`nrenderer = $renderer`r`n")
            Remove-Item "$settings\log.txt", "$settings\snapshot-*.png" -ErrorAction SilentlyContinue
            $status = 'ok'; $loadSeconds = ''; $private = ''
            $started = Get-Date
            $p = Start-Process -FilePath "$bin\OpenBve.exe" -WorkingDirectory $bin -ArgumentList @("/filesystem=`"$fs`"", "/route=`"$($c.Route)`"", "/train=`"$($c.Train)`"") -PassThru
            $loaded = $false
            while (((Get-Date) - $started).TotalSeconds -lt $LoadTimeout) {
                Start-Sleep 2
                if ($p.HasExited) { $status = "exited during the load, code $($p.ExitCode)"; break }
                if (Select-String -LiteralPath "$settings\log.txt" -Pattern 'Loading complete' -Quiet -ErrorAction SilentlyContinue) { $loaded = $true; $loadSeconds = [Math]::Round(((Get-Date) - $started).TotalSeconds, 1); break }
            }
            if (-not $loaded -and $status -eq 'ok') { $status = "not loaded in $LoadTimeout s" }
            $mark = 0
            if ($loaded) {
                Start-Sleep ($Stationary + 5)
                $mark = @(Get-Content -LiteralPath "$settings\log.txt" -Encoding UTF8).Count
                if ($p.HasExited) { $status = "exited while standing, code $($p.ExitCode)" }
                else {
                    $h = [Bench]::Largest([uint32]$p.Id)
                    Tap $h 0x46 1   # reverser forward
                    CtrlA $h        # the AI driver
                    for ($i = 0; $i -lt $Drive -and -not $p.HasExited; $i += 5) { Start-Sleep 5 }
                    if ($p.HasExited) { $status = "exited while driving, code $($p.ExitCode)" }
                    else { $p.Refresh(); $private = [Math]::Round($p.PrivateMemorySize64 / 1GB, 2) }
                }
            }
            if (-not $p.HasExited) { Stop-Process -Id $p.Id -Confirm:$false -ErrorAction SilentlyContinue }
            Start-Sleep 3
            Get-CimInstance Win32_Process -Filter "Name='Win32PluginProxy.exe' AND ParentProcessId=$($p.Id)" | ForEach-Object { Stop-Process -Id $_.ProcessId -Confirm:$false -ErrorAction SilentlyContinue }
            Copy-Item "$settings\log.txt" "$results\$name-log.txt" -ErrorAction SilentlyContinue
            Copy-Item "$settings\1.5.0\obns.cfg" "$results\$name-obns.cfg" -ErrorAction SilentlyContinue
            $shot = Get-ChildItem "$settings\snapshot-*.png" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
            if ($shot) { Copy-Item $shot.FullName "$results\$name-last.png" }
            $lines = @(Get-Content -LiteralPath "$results\$name-log.txt" -Encoding UTF8 -ErrorAction SilentlyContinue)
            $standing = Stats ($lines | Select-Object -First $mark | Select-Object -Skip 0)
            $driving = Stats ($lines | Select-Object -Skip $mark)
            $drew = (Select-String -LiteralPath "$results\$name-obns.cfg" -Pattern '^lastdrawn\s*=\s*(.*)$' -ErrorAction SilentlyContinue | ForEach-Object { $_.Matches[0].Groups[1].Value }) -join ''
            $trouble = @($lines | Select-String -Pattern 'DEVICE_LOST|DEVICE_REMOVED|DXGI_ERROR|Exception|layer stopped|world layer off' | Select-Object -First 1 | ForEach-Object { $_.Line })
            if ($trouble.Count -gt 0) { $status = "$status; " + ($trouble[0] -replace ',', ' ').Substring(0, [Math]::Min(160, $trouble[0].Length)) }
            "$bundle,$renderer,$shadow,$drew,$loadSeconds,$($standing[0]),$($standing[1]),$($standing[2]),$($driving[0]),$($driving[1]),$($driving[2]),$private,$status" | Add-Content $summary -Encoding UTF8
            Get-Content $summary -Tail 1 | Write-Host
            Start-Sleep 15   # the GPU and the disks settle between runs
        }
    }
}
Write-Host "Done: $results"
