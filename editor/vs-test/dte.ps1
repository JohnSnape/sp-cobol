# Get the DTE automation object of one devenv process (from the Running Object Table).
# Dot-source this file, then:  $dte = Get-Dte <pid>
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Runtime.InteropServices.ComTypes;
public static class Rot {
  [DllImport("ole32.dll")] static extern int GetRunningObjectTable(int r, out IRunningObjectTable rot);
  [DllImport("ole32.dll")] static extern int CreateBindCtx(int r, out IBindCtx ctx);
  public static object Find(string name) {
    IRunningObjectTable rot; GetRunningObjectTable(0, out rot);
    IEnumMoniker e; rot.EnumRunning(out e);
    var m = new IMoniker[1];
    while (e.Next(1, m, IntPtr.Zero) == 0) {
      IBindCtx ctx; CreateBindCtx(0, out ctx);
      string n; m[0].GetDisplayName(ctx, null, out n);
      if (n == name) { object o; rot.GetObject(m[0], out o); return o; }
    }
    return null;
  }
}
"@
function Get-Dte([int]$ProcessId) { $d = [Rot]::Find("!VisualStudio.DTE.17.0:$ProcessId"); if (-not $d) { $d = [Rot]::Find("!VisualStudio.DTE.18.0:$ProcessId") }; $d }

# COM calls into a busy VS can be rejected ("application is busy"): retry a few times
function Retry([scriptblock]$Block) {
    for ($i = 0; $i -lt 20; $i++) {
        try { return & $Block } catch { if ($_.Exception.Message -notmatch 'busy|RPC_E_CALL_REJECTED|0x80010001|0x8001010A') { throw }; Start-Sleep -Milliseconds 500 }
    }
    throw "VS stayed busy"
}

function Get-Text($dte) {
    Retry {
        $td = $dte.ActiveDocument.Object("TextDocument")
        $td.StartPoint.CreateEditPoint().GetText($td.EndPoint)
    }
}

function Set-Caret($dte, [int]$Line, [int]$Column1) {
    Retry { $dte.ActiveDocument.Selection.MoveToLineAndOffset($Line, $Column1, $false) }
}

function Get-Caret($dte) {
    Retry { $s = $dte.ActiveDocument.Selection; "{0},{1}" -f $s.ActivePoint.Line, $s.ActivePoint.LineCharOffset }
}

function Run-Command($dte, [string]$Name) { Retry { $dte.ExecuteCommand($Name, "") } }

