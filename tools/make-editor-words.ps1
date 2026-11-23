# Regenerate editor\CobolDotNet.Editor\Core\CobolWords.cs (the COBOL editor's
# keyword and function lists) from our own cobc, so the colors match the compiler.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-editor-words.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tmp = Join-Path $env:TEMP 'cobc-words'
New-Item -ItemType Directory -Force $tmp | Out-Null
$bash = 'C:\msys64\usr\bin\bash.exe'
$env:MSYSTEM = 'UCRT64'
$t = ($tmp -replace '\\', '/') -replace '^([A-Za-z]):', { '/' + $_.Groups[1].Value.ToLower() }
& $bash -lc "cd /d/vcobol-cs/bin && export COB_CONFIG_DIR=/d/vcobol-cs/share/gnucobol/config && ./cobc.exe --list-reserved > '$t/reserved.txt' && ./cobc.exe --list-intrinsics > '$t/intrinsics.txt'"

$words = New-Object System.Collections.Generic.SortedSet[string]
Get-Content "$tmp\reserved.txt" | Select-Object -Skip 2 | ForEach-Object {
    if ($_ -match '^(\S+)\s') { [void]$words.Add($matches[1]) }
    if ($_ -match 'aliased with ([^)]+)\)') { $matches[1].Split(',') | ForEach-Object { [void]$words.Add($_.Trim()) } }
}
$funcs = New-Object System.Collections.Generic.SortedSet[string]
Get-Content "$tmp\intrinsics.txt" | Select-Object -Skip 2 | ForEach-Object {
    if ($_ -match '^(\S+)\s') { [void]$funcs.Add($matches[1]) }
}

function Wrap($set) {
    $sb = New-Object System.Text.StringBuilder
    $line = '            '
    foreach ($w in $set) {
        $item = '"' + $w + '", '
        if ($line.Length + $item.Length -gt 100) { [void]$sb.AppendLine($line.TrimEnd()); $line = '            ' }
        $line += $item
    }
    [void]$sb.Append($line.TrimEnd())
    $sb.ToString()
}

$cs = @"
// GENERATED from our cobc (--list-reserved / --list-intrinsics) by tools\make-editor-words.ps1.
// Do not edit by hand: regenerate when the compiler's word list changes.
using System;
using System.Collections.Generic;

namespace CobolDotNet.Editor.Core
{
    public static class CobolWords
    {
        public static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
$(Wrap $words)
        };

        public static readonly HashSet<string> Functions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
$(Wrap $funcs)
        };
    }
}
"@
[System.IO.File]::WriteAllText((Join-Path $root 'editor\CobolDotNet.Editor\Core\CobolWords.cs'), $cs)
Write-Host "CobolWords.cs: $($words.Count) reserved words, $($funcs.Count) functions"
