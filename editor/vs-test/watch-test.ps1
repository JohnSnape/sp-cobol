# The debugger's COBOL names, checked in real Visual Studio (2022 experimental instance).
# Builds nothing itself: pass a folder holding WatchApp.sln + Program.cbl (samples\WATCH.cbl).
# Drives VS through DTE only (no keystrokes): breakpoint, Debugger.Go, GetExpression.
param([string]$Dir = "$env:TEMP\cobol-watch-test", [int]$Line = 25)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\dte.ps1"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VSSDK -property installationPath
$p = Start-Process "$vs\Common7\IDE\devenv.exe" -ArgumentList "/rootsuffix Exp `"$Dir\WatchApp.sln`"" -PassThru
$fail = 0
try {
    $dte = $null
    for ($i = 0; $i -lt 120 -and -not $dte; $i++) { Start-Sleep -Seconds 1; $dte = Get-Dte $p.Id }
    if (-not $dte) { throw "Visual Studio didn't register for automation" }
    for ($i = 0; $i -lt 90; $i++) { try { if ($dte.Solution.IsOpen -and $dte.Solution.Projects.Count -gt 0) { break } } catch {}; Start-Sleep -Seconds 1 }
    Start-Sleep -Seconds 5
    foreach ($b in @(Retry { $dte.Debugger.Breakpoints })) { Retry { $b.Delete() } }
    Retry { $dte.Debugger.Breakpoints.Add("", "$Dir\Program.cbl", $Line) } | Out-Null
    Retry { $dte.Debugger.Go($false) }
    for ($i = 0; $i -lt 120; $i++) { $m = Retry { $dte.Debugger.CurrentMode }; if ($m -eq 2) { break }; Start-Sleep -Seconds 1 }   # 2 = dbgBreakMode
    if ($m -ne 2) { throw "never stopped at the breakpoint (mode $m)" }
    $where = Retry { $dte.Debugger.CurrentStackFrame.FunctionName }
    Write-Host "stopped in $where, line $(Retry { $dte.ActiveDocument.Selection.ActivePoint.Line }) of $(Retry { $dte.ActiveDocument.Name })"
    $checks = [ordered]@{
        'WS_PAY'               = '0712.50'
        'WS_COUNT'             = '-0012'
        'WS_TOTAL'             = '+0001234.56'
        'CUST_NAME'            = '"ADA       "'
        'MONTH_ENTRY[2]'       = '"FEB200"'
        'MONTH_ENTRY[3].Get("MONTH-SALES")' = '300'
        'COBOL["ws-pay"]'      = '0712.50'
        'RATE'                 = '1.5'
    }
    foreach ($k in $checks.Keys) {
        $e = Retry { $dte.Debugger.GetExpression($k, $true, 5000) }
        $ok = $e.IsValidValue -and $e.Value -eq $checks[$k]
        if (-not $ok) { $fail++ }
        Write-Host ("{0}  {1,-36} = {2,-16} type {3}" -f ($(if ($ok) {'PASS'} else {'FAIL'}), $k, $e.Value, $e.Type))
    }
    $c = Retry { $dte.Debugger.GetExpression('MONTHS', $true, 5000) }
    Write-Host "MONTHS expands to:"
    foreach ($m1 in $c.DataMembers) { Write-Host "   $($m1.Name) = $($m1.Value)  [$($m1.Type)]"; foreach ($m2 in $m1.DataMembers) { Write-Host "      $($m2.Name) = $($m2.Value)  [$($m2.Type)]" } }
    Retry { $dte.Debugger.Stop($true) }
}
finally {
    try { Retry { $dte.Quit() } } catch { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
Write-Host "$fail failed"
exit $fail

