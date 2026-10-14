# Builds PlainTrain.zip: a plain archive (no package.xml) holding a train in a Big5-named folder
# (測試列車), with the names stored as raw Big5 and no UTF-8 flag - what Windows' own zip tool
# writes on a Traditional Chinese system. Python's zipfile cannot do this (it flags any non-ASCII
# name as UTF-8), so .NET's ZipArchive writes it with an explicit entry-name encoding.
param([string]$Out = (Join-Path $PSScriptRoot 'PlainTrain.zip'))

Add-Type -AssemblyName System.IO.Compression
$big5 = [Text.Encoding]::GetEncoding(950)
$folder = -join [char[]](0x6E2C, 0x8A66, 0x5217, 0x8ECA)   # 測試列車
$readme = -join [char[]](0x8AAA, 0x660E)                     # 說明
$train = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\Train\AndroidTest\train.dat'))

if (Test-Path $Out) { Remove-Item $Out }
$stream = [IO.File]::Create($Out)
$zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $false, $big5)
$files = @(
    @("$folder/train.dat", $train),
    @("$folder/$readme.txt", $big5.GetBytes((-join [char[]](0x6E2C, 0x8A66))))
)
foreach ($pair in $files) {
    $entry = $zip.CreateEntry($pair[0])
    $s = $entry.Open()
    $s.Write($pair[1], 0, $pair[1].Length)
    $s.Close()
}
$zip.Dispose()
$stream.Dispose()
