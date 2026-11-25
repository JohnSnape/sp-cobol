<#
  Compiles and runs every example of the language reference (docs-src\examples\*.cbl)
  and saves what SP COBOL prints in docs-src\examples\output\NAME.out, which
  tools\build-docs.ps1 puts on the page under the example.

  Usage:   powershell -File tools\check-doc-examples.ps1             # all examples
           powershell -File tools\check-doc-examples.ps1 ADD1 MOVE1  # some
           ... -Rebuild    rebuild the runtime copy the examples use first

  Each example, in its own folder under D:\vcobol-build\docs-examples\NAME:
    1. ours: tools\cobnet.sh (COBOL -> C#), dotnet build (warnings are errors), run.
    2. stock GnuCOBOL 3.2 (the reference build with XML/JSON): compile, run, and the screen
       output, exit code and every data file left behind must match ours byte for byte.
  Beside NAME.cbl:
    NAME.in       standard input for both runs
    NAME.keys     a screen example: runs with COB_SCREEN=script, no stock comparison
    NAME.sub\     more .cbl files: CALLed programs, built into the same program
    NAME.expect   one word, for the exceptions:
                    differs        SP COBOL deliberately prints something else (DECISIONS.md)
                    nostock        don't compare (stock has no indexed files, for example)
                    compile-error  the example shows an error message: cobc must refuse it
  docs-src\examples\copy\*.cpy are copied in beside every example (for COPY).
#>
param([switch]$Rebuild, [Parameter(ValueFromRemainingArguments = $true)][string[]]$Only)

$ErrorActionPreference = 'Stop'
$proj    = Split-Path -Parent $PSScriptRoot
$exDir   = Join-Path $proj 'docs-src\examples'
$outDir  = Join-Path $exDir 'output'
$work    = 'D:\vcobol-build\docs-examples'
$rtDir   = Join-Path $work '_runtime'
$bash    = 'C:\msys64\usr\bin\bash.exe'
New-Item -ItemType Directory -Force $outDir, $work | Out-Null
$env:MSYSTEM = 'UCRT64'

function Invoke-Bash([string]$cmd) { $ErrorActionPreference = 'Continue'; & $bash -lc $cmd 2>&1 | ForEach-Object { "$_" } }

$rtDll = Join-Path $rtDir 'CobolDotNet.Runtime.dll'
if ($Rebuild -or -not (Test-Path $rtDll)) {
    $b = dotnet build (Join-Path $proj 'runtime\CobolDotNet.Runtime\CobolDotNet.Runtime.csproj') -v q -o $rtDir 2>&1
    if ($LASTEXITCODE -ne 0) { $b; throw 'runtime build failed' }
}

$names = Get-ChildItem $exDir -Filter *.cbl | ForEach-Object { $_.BaseName }
if ($Only) { $names = $names | Where-Object { $Only -contains $_ } }

$pass = 0; $fail = @(); $notes = @()
foreach ($n in $names) {
    $dir = Join-Path $work $n
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    New-Item -ItemType Directory $dir | Out-Null
    Copy-Item (Join-Path $exDir "$n.cbl") $dir
    $copyDir = Join-Path $exDir 'copy'
    if (Test-Path $copyDir) { Get-ChildItem $copyDir -File | Copy-Item -Destination $dir }
    $unix = '/d/vcobol-build/docs-examples/' + $n
    $subDir = Join-Path $exDir "$n.sub"
    $subs = @(if (Test-Path $subDir) { Get-ChildItem $subDir -Filter *.cbl | ForEach-Object { Copy-Item $_.FullName $dir; $_.Name } })
    $subList = ($subs | ForEach-Object { " $_" }) -join ''
    $inFile = Join-Path $exDir "$n.in"; $hasIn = Test-Path $inFile
    $keys = Join-Path $exDir "$n.keys"; $isScreen = Test-Path $keys
    $expFile = Join-Path $exDir "$n.expect"
    $expect = if (Test-Path $expFile) { (Get-Content $expFile -TotalCount 1).Trim().ToLower() } else { '' }
    if ($hasIn) { Copy-Item $inFile $dir }

    # 1. ours: COBOL -> C#
    $r = Invoke-Bash "export COBCPY='$unix'; cd '/d/CALUDE CODE/visual-cobol' && bash tools/cobnet.sh $unix/$n.cbl $unix/net"
    $rc = $LASTEXITCODE
    $msgs = @($r | Where-Object { $_ -match ': (error|warning|note):' } | ForEach-Object { $_ -replace '^.*/', '' })
    if ($expect -eq 'compile-error') {
        if ($rc -eq 0) { $fail += "$n (expected a compile error, but it compiled)"; continue }
        $msgs | Set-Content -Encoding utf8 (Join-Path $outDir "$n.out")
        $pass++; "PASS  $n  (compile error, as intended)"; continue
    }
    if ($rc -ne 0) { $fail += "$n (cobc):`n$($r -join "`n")"; continue }
    if ($msgs) { $notes += "$n compiler messages: " + ($msgs -join ' | ') }
    $subFail = $false
    foreach ($s in $subs) {
        $r = Invoke-Bash "export COBCPY='$unix'; cd '/d/CALUDE CODE/visual-cobol' && bash tools/cobnet.sh $unix/$s $unix/net sub"
        if ($LASTEXITCODE -ne 0) { $fail += "$n (cobc $s):`n$($r -join "`n")"; $subFail = $true }
    }
    if ($subFail) { continue }

    # C# -> .NET, against the prebuilt runtime (so several checks can run at once)
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <StartupObject>CobolPrograms.${n}_Program</StartupObject>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><Reference Include="CobolDotNet.Runtime"><HintPath>$rtDll</HintPath></Reference></ItemGroup>
</Project>
"@
    Set-Content -Encoding utf8 (Join-Path $dir 'net\ex.csproj') $csproj
    $b = dotnet build (Join-Path $dir 'net\ex.csproj') -v q -o (Join-Path $dir 'net\bin') 2>&1
    if ($LASTEXITCODE -ne 0) {
        $fail += "$n (C# build): " + (($b | Select-String ' error ' | Select-Object -First 3) -join "`n")
        continue
    }

    if ($isScreen) { $env:COB_SCREEN = 'script'; $env:COB_SCREEN_KEYS = $keys }
    $out = Join-Path $dir 'net.out'; $rcFile = Join-Path $dir 'net.rc'
    $redir = if ($hasIn) { " < `"$inFile`"" } else { " < NUL" }
    $runDir = Join-Path $dir 'netrun'
    New-Item -ItemType Directory $runDir | Out-Null
    Push-Location $runDir
    cmd /v:on /c "dotnet `"$dir\net\bin\ex.dll`"$redir > `"$out`" 2>&1 & (echo !ERRORLEVEL!)> `"$rcFile`""
    Pop-Location
    Remove-Item Env:COB_SCREEN, Env:COB_SCREEN_KEYS -ErrorAction SilentlyContinue
    $netRc = (Get-Content $rcFile -TotalCount 1).Trim()
    # the page shows C:\Work where the run folder (D:\vcobol-build\...) appears, e.g. in hints
    $shown = [IO.File]::ReadAllText($out).Replace($runDir, 'C:\Work')
    [IO.File]::WriteAllText((Join-Path $outDir "$n.out"), $shown)
    if ($netRc -ne '0') { $notes += "$n exit code $netRc" }

    if ($isScreen -or $expect -eq 'nostock') { $pass++; "PASS  $n  (no stock comparison)"; continue }

    # 2. stock GnuCOBOL 3.2, same folder layout
    $stdin = if ($hasIn) { " < ../$n.in" } else { " < /dev/null" }
    $r = Invoke-Bash "source '/d/CALUDE CODE/visual-cobol/tools/stock-cobc-ml.sh'; cd $unix && mkdir stockrun && cobc -x $n.cbl$subList -o stock.exe && cd stockrun && ../stock.exe$stdin > ../stock.out 2>&1; echo `$? > ../stock.rc"
    if (-not (Test-Path (Join-Path $dir 'stock.rc'))) { $fail += "$n (stock GnuCOBOL can't compile it):`n$($r -join "`n")"; continue }
    $stockRc = (Get-Content (Join-Path $dir 'stock.rc') -TotalCount 1).Trim()
    $diffs = @()
    $d = & C:\msys64\usr\bin\diff.exe -u --strip-trailing-cr (Join-Path $dir 'stock.out') $out
    if ($LASTEXITCODE -ne 0) { $diffs += "output:`n$($d -join "`n")" }
    if ($stockRc -ne $netRc) { $diffs += "exit code stock $stockRc, ours $netRc" }
    $ours = @(Get-ChildItem $runDir -File | ForEach-Object Name)
    $theirs = @(Get-ChildItem (Join-Path $dir 'stockrun') -File | ForEach-Object Name)
    foreach ($x in $theirs) {
        if ($ours -notcontains $x) { $diffs += "missing data file $x"; continue }
        $a = [IO.File]::ReadAllBytes((Join-Path $dir "stockrun\$x")); $bb = [IO.File]::ReadAllBytes((Join-Path $runDir $x))
        if ([Convert]::ToBase64String($a) -ne [Convert]::ToBase64String($bb)) { $diffs += "data file $x differs" }
    }
    foreach ($x in $ours) { if ($theirs -notcontains $x) { $diffs += "extra data file $x" } }
    if ($diffs -and $expect -eq 'differs') { $pass++; "PASS  $n  (differs from stock, as documented)"; continue }
    if ($expect -eq 'differs') { $notes += "$n is marked 'differs' but matches stock" }
    if ($diffs) { $fail += "$n (vs stock): " + ($diffs -join "`n"); continue }
    $pass++; "PASS  $n"
}

""
"$pass passed, $($fail.Count) failed"
$notes | ForEach-Object { "NOTE  $_" }
$fail | ForEach-Object { "FAIL  $_"; "" }
if ($fail.Count) { exit 1 }
