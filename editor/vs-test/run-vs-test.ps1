# The COBOL editor tested inside real Visual Studio (2022 experimental instance).
#
#   powershell -ExecutionPolicy Bypass -File tools\build-editor.ps1 -Deploy   # first
#   powershell -ExecutionPolicy Bypass -File editor\vs-test\run-vs-test.ps1
#
# Opens a Visual Studio window, drives the editor through DTE automation commands
# (Edit.BreakLine, Edit.DeleteBackwards, ...): NO keystrokes or mouse input, so
# it can't type into anything else on the desktop. Closes Visual Studio after.
# Not covered here: typing in columns 1-7, and paste/cut (they'd use the clipboard).
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
. "$PSScriptRoot\dte.ps1"

# test files: the template's program numbered, and the same with columns 1-6 blank
$dir = Join-Path $env:TEMP 'cobol-vs-test'
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$root\samples\WELCOME.cbl" "$dir\Numbered.cbl" -Force
$plain = Get-Content "$root\samples\WELCOME.cbl" | ForEach-Object { if ($_.Length -le 6) { '' } else { '      ' + $_.Substring(6) } }
[IO.File]::WriteAllText("$dir\Plain.cbl", ($plain -join "`n") + "`n")

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VSSDK -property installationPath
$p = Start-Process "$vs\Common7\IDE\devenv.exe" -ArgumentList "/rootsuffix Exp `"$dir\Numbered.cbl`"" -PassThru
try {
    $dte = $null
    for ($i = 0; $i -lt 120 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = Get-Dte $p.Id }
    if (-not $dte) { throw "Visual Studio didn't register for automation" }
    for ($i = 0; $i -lt 60; $i++) {
        try { if ($dte.Documents.Count -gt 0) { break } } catch {}
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 3
    & "$PSScriptRoot\keys-test.ps1" -ProcessId $p.Id -Dir $dir
    $failed = $LASTEXITCODE
}
finally {
    try { Retry { $dte.Quit() } } catch { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
exit $failed
