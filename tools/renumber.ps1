# Renumber fixed-format COBOL source: columns 1-6 become 000010, 000020, ...
#
#   powershell -ExecutionPolicy Bypass -File tools\renumber.ps1 FILE.cbl [FILE2.cpy ...]
#   powershell -ExecutionPolicy Bypass -File tools\renumber.ps1 -Start 100 -Step 100 FILE.cbl
#
# Every line gets a number, blank lines included (a blank line becomes just the
# number). Columns 7 onward are left exactly as they were, and so are the file's
# line endings (LF or CRLF) and its bytes (read and written as Latin-1).
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)][string[]]$Files,
    [int]$Start = 10,
    [int]$Step = 10
)
$ErrorActionPreference = 'Stop'
$latin1 = [System.Text.Encoding]::GetEncoding(28591)

foreach ($f in $Files) {
    $path = (Resolve-Path $f).Path
    $text = [System.IO.File]::ReadAllText($path, $latin1)
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $endsWithNewline = $text.EndsWith("`n")
    $lines = $text -split "`r?`n"
    if ($endsWithNewline) { $lines = $lines[0..($lines.Count - 2)] }

    $last = $Start + ($lines.Count - 1) * $Step
    if ($last -gt 999999) {
        throw "${f}: $($lines.Count) lines at step $Step would pass 999999. Use a smaller -Step."
    }

    $n = $Start
    $out = foreach ($line in $lines) {
        $rest = if ($line.Length -gt 6) { $line.Substring(6) } else { '' }
        $n.ToString('000000') + $rest
        $n += $Step
    }
    $result = ($out -join $nl)
    if ($endsWithNewline) { $result += $nl }
    [System.IO.File]::WriteAllText($path, $result, $latin1)
    Write-Host ("{0}: {1} lines, {2:000000}-{3:000000}" -f $f, $lines.Count, $Start, $last)
}
