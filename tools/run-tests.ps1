<#
  Regression suite: every samples\*.cbl is compiled two ways and the outputs compared.
    1. stock GnuCOBOL 3.2 (COBOL -> C -> native exe): the reference
    2. ours (COBOL -> C# -> .NET IL)
  Usage:   powershell -File tools\run-tests.ps1            # all samples
           powershell -File tools\run-tests.ps1 HELLO      # one sample
  Expected outputs are cached in samples\expected\NAME.out (delete one to regenerate).
  NAME.in   : fed to the program as standard input (both stock and ours).
  NAME.keys : a SCREEN test. Stock GnuCOBOL here has no curses, so there is no stock
              reference: ours runs with the scripted test renderer (COB_SCREEN=script),
              and the first run's output becomes the expected file -- review it!
              Screen tests also build the Windows (Forms) version, because the
              Screen App default is to build both.
  Work area: D:\vcobol-build\tests (no spaces: MSYS2 tools and libtool need that).
#>
param([string[]]$Only)

$ErrorActionPreference = 'Stop'
$proj    = Split-Path -Parent $PSScriptRoot
$samples = Join-Path $proj 'samples'
$expect  = Join-Path $samples 'expected'
$work    = 'D:\vcobol-build\tests'
$bash    = 'C:\msys64\usr\bin\bash.exe'
$runtime = Join-Path $proj 'runtime\CobolDotNet.Runtime\CobolDotNet.Runtime.csproj'
$forms   = Join-Path $proj 'runtime\CobolDotNet.Forms\CobolDotNet.Forms.csproj'
New-Item -ItemType Directory -Force $expect, $work | Out-Null
$env:MSYSTEM = 'UCRT64'

function Invoke-Bash([string]$cmd) { $ErrorActionPreference = 'Continue'; & $bash -lc $cmd 2>&1 | ForEach-Object { "$_" } }

$names = Get-ChildItem $samples -Filter *.cbl | ForEach-Object { $_.BaseName }
if ($Only) { $names = $names | Where-Object { $Only -contains $_ } }

$pass = 0; $fail = @(); $new = @()
foreach ($n in $names) {
    $dir = Join-Path $work $n
    New-Item -ItemType Directory -Force $dir | Out-Null
    Copy-Item (Join-Path $samples "$n.cbl") $dir -Force
    $unix = '/d/vcobol-build/tests/' + $n
    $inFile = Join-Path $samples "$n.in"
    $keys = Join-Path $samples "$n.keys"
    $isScreen = Test-Path $keys
    $hasIn = Test-Path $inFile
    if ($hasIn) { Copy-Item $inFile $dir -Force }

    # 1. reference output from stock GnuCOBOL (not for screen tests)
    $exp = Join-Path $expect "$n.out"
    #    The program runs in its own empty folder (stockrun); data files it leaves there are
    #    saved as samples\expected\NAME\ and ours must write the same bytes.
    $expFiles = Join-Path $expect $n
    if (-not $isScreen -and -not (Test-Path $exp)) {
        $stdin = if ($hasIn) { " < ../$n.in" } else { " < /dev/null" }
        $r = Invoke-Bash "source '/d/CALUDE CODE/visual-cobol/tools/stock-cobc.sh'; cd $unix && rm -rf stockrun && mkdir stockrun && cobc -x $n.cbl -o stock.exe && cd stockrun && ../stock.exe$stdin > ../stock.out 2>&1; echo rc=`$? >> ../stock.out"
        Copy-Item (Join-Path $dir 'stock.out') $exp
        $made = Get-ChildItem (Join-Path $dir 'stockrun') -File -ErrorAction SilentlyContinue
        if ($made) {
            New-Item -ItemType Directory -Force $expFiles | Out-Null
            $made | Copy-Item -Destination $expFiles
        }
    }

    # 2. ours: COBOL -> C# -> .NET
    $r = Invoke-Bash "cd '/d/CALUDE CODE/visual-cobol' && bash tools/cobnet.sh $unix/$n.cbl $unix/net"
    if ($LASTEXITCODE -ne 0) { $fail += "$n (cobc): $r"; continue }
    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework>
    <Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <StartupObject>CobolPrograms.${n}_Program</StartupObject>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup><ProjectReference Include="$runtime" /></ItemGroup>
</Project>
"@
    Set-Content -Encoding utf8 (Join-Path $dir 'net\test.csproj') $csproj
    $b = dotnet build (Join-Path $dir 'net\test.csproj') -v q -o (Join-Path $dir 'net\bin') 2>&1
    if ($LASTEXITCODE -ne 0) {
        $fail += "$n (C# build): " + (($b | Select-String ' error ' | Select-Object -First 3) -join "`n")
        continue
    }

    if ($isScreen) {
        # the Windows version of the same generated C# must build too
        $wdir = Join-Path $dir 'win'
        New-Item -ItemType Directory -Force $wdir | Out-Null
        $wproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType><TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>disable</Nullable><ImplicitUsings>disable</ImplicitUsings>
    <StartupObject>WinHost</StartupObject>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\net\$n.cs" />
    <ProjectReference Include="$forms" />
  </ItemGroup>
</Project>
"@
        Set-Content -Encoding utf8 (Join-Path $wdir 'win.csproj') $wproj
        $host_cs = "static class WinHost { static void Main(string[] a) => CobolDotNet.Forms.FormsHost.Run(CobolPrograms.${n}_Program.Main, a, `"$n`"); }"
        Set-Content -Encoding utf8 (Join-Path $wdir 'WinHost.cs') $host_cs
        $b = dotnet build (Join-Path $wdir 'win.csproj') -v q -o (Join-Path $wdir 'bin') 2>&1
        if ($LASTEXITCODE -ne 0) {
            $fail += "$n (Windows build): " + (($b | Select-String ' error ' | Select-Object -First 3) -join "`n")
            continue
        }
        $env:COB_SCREEN = 'script'
        $env:COB_SCREEN_KEYS = $keys
    }

    $out = Join-Path $dir 'net.out'
    $redir = if ($hasIn) { " < `"$inFile`"" } else { " < NUL" }
    $runDir = Join-Path $dir 'netrun'
    if (Test-Path $runDir) { Remove-Item -Recurse -Force $runDir }
    New-Item -ItemType Directory $runDir | Out-Null
    Push-Location $runDir
    # /v:on and !ERRORLEVEL!: %ERRORLEVEL% would be expanded before the program runs
    cmd /v:on /c "dotnet `"$dir\net\bin\test.dll`"$redir > `"$out`" 2>&1 & (echo rc=!ERRORLEVEL!)>> `"$out`""
    Pop-Location
    Remove-Item Env:COB_SCREEN, Env:COB_SCREEN_KEYS -ErrorAction SilentlyContinue

    # data files: same names, same bytes as stock GnuCOBOL's
    $fileDiffs = @()
    $ours = @(Get-ChildItem $runDir -File | ForEach-Object Name)
    $theirs = if (Test-Path $expFiles) { @(Get-ChildItem $expFiles -File | ForEach-Object Name) } else { @() }
    foreach ($x in $theirs) {
        if ($ours -notcontains $x) { $fileDiffs += "missing data file $x"; continue }
        $a = [IO.File]::ReadAllBytes((Join-Path $expFiles $x)); $b = [IO.File]::ReadAllBytes((Join-Path $runDir $x))
        if ([Convert]::ToBase64String($a) -ne [Convert]::ToBase64String($b)) {
            $at = 0; while ($at -lt [Math]::Min($a.Length, $b.Length) -and $a[$at] -eq $b[$at]) { $at++ }
            $fileDiffs += "data file $x differs at byte $at (stock $($a.Length) bytes, ours $($b.Length))"
        }
    }
    foreach ($x in $ours) { if ($theirs -notcontains $x) { $fileDiffs += "extra data file $x" } }
    if ($fileDiffs) { $fail += "$n (files): " + ($fileDiffs -join '; '); continue }

    if ($isScreen -and -not (Test-Path $exp)) {
        Copy-Item $out $exp
        $new += $n
        "NEW   $n  (review samples\expected\$n.out; from now on it guards this test)"
        continue
    }
    $d = & C:\msys64\usr\bin\diff.exe -u --strip-trailing-cr $exp $out
    if ($LASTEXITCODE -eq 0) { $pass++; "PASS  $n" } else { $fail += "$n (output differs):`n$($d -join "`n")" }
}

""
$summary = "$pass passed, $($fail.Count) failed"
if ($new.Count) { $summary += ", $($new.Count) new (review)" }
$summary
$fail | ForEach-Object { "FAIL  $_"; "" }
if ($fail.Count) { exit 1 }
