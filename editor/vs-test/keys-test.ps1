param([int]$ProcessId, [string]$Dir)
# Drives the COBOL editor in the experimental VS through DTE commands (no keystrokes)
# and checks the text after each one, then Undo.
. "$PSScriptRoot\dte.ps1"
$here = $Dir
$dte = Get-Dte $ProcessId
$fail = 0

function Activate($name) { Retry { foreach ($d in $dte.Documents) { if ($d.Name -eq $name) { $d.Activate() } } }; Start-Sleep -Milliseconds 500 }
function Lines() { (Get-Text $dte) -split "`r?`n" }
function Check($name, [bool]$ok, $detail) {
    if ($ok) { "PASS  $name" } else { $script:fail++; "FAIL  $name`n      $detail" }
}
function Undo() { Run-Command $dte 'Edit.Undo'; Start-Sleep -Milliseconds 300 }

Retry { $dte.Documents.CloseAll(2) }   # start clean, discarding any earlier run's edits
Retry { $dte.ItemOperations.OpenFile("$here\Numbered.cbl") | Out-Null }
Start-Sleep -Seconds 1
Activate 'Numbered.cbl'
$orig = Get-Text $dte
$L = Lines
$n = $L.Count

# ---- Enter at the end of line 49 ("DISPLAY PAY-SCREEN")
Set-Caret $dte 49 ($L[48].Length + 1)
Run-Command $dte 'Edit.BreakLine'
$A = Lines
Check 'Enter: new line numbered 000500 under the code' ($A[49] -eq '000500         ') "line 50 = '$($A[49])'"
Check 'Enter: next line moved to 000510' ($A[50] -eq '000510         ACCEPT PAY-SCREEN') "line 51 = '$($A[50])'"
Check 'Enter: last line renumbered' ($A[$A.Count - 2] -like '000650*') "last = '$($A[$A.Count - 2])'"
Check 'Enter: caret at column 16' ((Get-Caret $dte) -eq '50,16') "caret $(Get-Caret $dte)"
Undo
Check 'Enter: ONE undo restores the file' ((Get-Text $dte) -eq $orig) 'text differs after one undo'

# ---- Enter in the middle of line 48 (before UNTIL)
$col = $L[47].IndexOf('UNTIL') + 1
Set-Caret $dte 48 $col
Run-Command $dte 'Edit.BreakLine'
$A = Lines
Check 'split: first part' ($A[47] -eq '000480     PERFORM ') "'$($A[47])'"
Check 'split: rest keeps indent' ($A[48] -eq '000490     UNTIL WS-KEY = 2005') "'$($A[48])'"
Undo
Check 'split: one undo' ((Get-Text $dte) -eq $orig) 'text differs'

# ---- Backspace at column 8 of line 50 joins it onto line 49
Set-Caret $dte 50 8
Run-Command $dte 'Edit.DeleteBackwards'
$A = Lines
Check 'Backspace: joined' ($A[48] -eq '000490         DISPLAY PAY-SCREEN        ACCEPT PAY-SCREEN') "'$($A[48])'"
Check 'Backspace: lines below -10' ($A[49] -eq '000500         IF WS-KEY = 0') "'$($A[49])'"
Undo
Check 'Backspace: one undo' ((Get-Text $dte) -eq $orig) 'text differs'

# ---- Delete at the end of line 49 joins line 50 onto it
Set-Caret $dte 49 ($L[48].Length + 1)
Run-Command $dte 'Edit.Delete'
$A = Lines
Check 'Delete: joined' ($A[48] -eq '000490         DISPLAY PAY-SCREEN        ACCEPT PAY-SCREEN') "'$($A[48])'"
Undo
Check 'Delete: one undo' ((Get-Text $dte) -eq $orig) 'text differs'

# ---- Tab at column 8 of line 49: four spaces (to column 12)
Set-Caret $dte 49 8
Run-Command $dte 'Edit.InsertTab'
$A = Lines
Check 'Tab: four spaces' ($A[48] -eq '000490             DISPLAY PAY-SCREEN') "'$($A[48])'"
Check 'Tab: caret at column 12' ((Get-Caret $dte) -eq '49,12') "caret $(Get-Caret $dte)"
Undo

# ---- Home: first to the code, then to column 8
Set-Caret $dte 49 25
Run-Command $dte 'Edit.LineStart'
Check 'Home: to the code' ((Get-Caret $dte) -eq '49,16') "caret $(Get-Caret $dte)"
Run-Command $dte 'Edit.LineStart'
Check 'Home again: to column 8' ((Get-Caret $dte) -eq '49,8') "caret $(Get-Caret $dte)"

# ---- Duplicate line (a command that runs normally, then renumbers)
Set-Caret $dte 49 10
Run-Command $dte 'Edit.Duplicate'
$A = Lines
Check 'Duplicate: copy renumbered' ($A[48] -eq '000490         DISPLAY PAY-SCREEN' -and $A[49] -eq '000500         DISPLAY PAY-SCREEN' -and $A[50] -eq '000510         ACCEPT PAY-SCREEN') "'$($A[48])' / '$($A[49])' / '$($A[50])'"
Undo
Check 'Duplicate: one undo' ((Get-Text $dte) -eq $orig) 'text differs'

# ---- An edit from outside the keyboard (automation inserts a line): the watcher renumbers it
Retry {
    $td = $dte.ActiveDocument.Object('TextDocument')
    $ep = $td.StartPoint.CreateEditPoint(); $ep.MoveToLineAndOffset(49, 1)
    $ep.Insert("       MOVE 1 TO WS-KEY`n")
}
Start-Sleep -Milliseconds 500
$A = Lines
Check 'outside edit: renumbered' ($A[48] -eq '000490 MOVE 1 TO WS-KEY' -and $A[49] -eq '000500         DISPLAY PAY-SCREEN') "'$($A[48])' / '$($A[49])'"
Undo
$undos = 1
if ((Get-Text $dte) -ne $orig) { Undo; $undos = 2 }
Check "outside edit: undo restores ($undos step(s))" ((Get-Text $dte) -eq $orig) 'text differs'

# ---- Renumber COBOL Lines on a file without numbers
Retry { $dte.ItemOperations.OpenFile("$here\Plain.cbl") | Out-Null }
Start-Sleep -Seconds 1
Activate 'Plain.cbl'
Run-Command $dte 'Edit.RenumberCOBOLLines'
Start-Sleep -Milliseconds 500
$want = [IO.File]::ReadAllText("$here\Numbered.cbl")
Check 'Renumber command numbers a plain file' ((Get-Text $dte) -eq $want) ((Lines)[0..2] -join ' | ')

Retry { $dte.Documents.CloseAll(2) }   # vsSaveChangesNo
""
"$fail failed"
exit $fail
