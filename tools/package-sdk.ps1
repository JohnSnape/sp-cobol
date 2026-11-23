<#
  Build the CobolDotNet.Sdk NuGet package into dist\feed (a local package source).
    1. runtime DLLs (Release)          -> stage\cobolrt
    2. our cobc.exe + the DLLs it needs -> stage\tools\bin   (no MSYS2 needed on the user's PC)
       GnuCOBOL config + copybooks      -> stage\tools\config, stage\tools\copy
    3. dotnet pack                      -> dist\feed\CobolDotNet.Sdk.<ver>.nupkg
  The installed copy in the NuGet cache is removed so the next restore picks up the new build.
  Run after: bash tools/build-cobc-cs.sh   (cobc)   and any runtime change.
#>
$ErrorActionPreference = 'Stop'
$proj  = Split-Path -Parent $PSScriptRoot
$sdk   = Join-Path $proj 'sdk\CobolDotNet.Sdk'
$stage = Join-Path $sdk 'stage'
$feed  = Join-Path $proj 'dist\feed'
$tree  = 'D:\vcobol-build\gnucobol-cs'
$ucrt  = 'C:\msys64\ucrt64\bin'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force "$stage\cobolrt", "$stage\tools\bin", "$stage\tools\config", "$stage\tools\copy", $feed | Out-Null

# 1. runtime
dotnet build (Join-Path $proj 'runtime\CobolDotNet.Forms\CobolDotNet.Forms.csproj') -c Release -v q -o "$stage\cobolrt" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'runtime build failed' }
Get-ChildItem "$stage\cobolrt" | Where-Object { $_.Name -notmatch '^CobolDotNet\.(Runtime|Forms)\.(dll|pdb|xml)$' } | Remove-Item -Force

# 2. compiler toolset
Copy-Item 'D:\vcobol-cs\bin\cobc.exe', 'D:\vcobol-cs\bin\libcob-4.dll' "$stage\tools\bin"
foreach ($d in 'libgmp-10.dll', 'libintl-8.dll', 'libiconv-2.dll', 'libwinpthread-1.dll') { Copy-Item (Join-Path $ucrt $d) "$stage\tools\bin" }
Copy-Item "$tree\config\*.conf", "$tree\config\*.words" "$stage\tools\config" -ErrorAction SilentlyContinue
Copy-Item "$tree\config\*.conf" "$stage\tools\config"
Copy-Item "$tree\copy\*" "$stage\tools\copy"
Copy-Item "$tree\COPYING" "$stage\tools\COPYING.GPL-3.txt"
Copy-Item "$tree\COPYING.LESSER" "$stage\tools\COPYING.LGPL-3.txt"
Set-Content -Encoding utf8 "$stage\tools\README.txt" @"
cobc.exe is GnuCOBOL 3.2's compiler front end with a C# back end added (-fcsharp).
GnuCOBOL is free software under the GNU GPL v3 (COPYING.GPL-3.txt); libcob is LGPL v3.
The modified source is published with this project (compiler\gnucobol-3.2-csharp.patch).
Other DLLs here come from MSYS2 (GMP: LGPL, gettext/libiconv: LGPL, winpthreads: MIT/ZPL).
"@

# 3. pack
$ver = ([xml](Get-Content (Join-Path $sdk 'CobolDotNet.Sdk.csproj'))).Project.PropertyGroup.Version
dotnet pack (Join-Path $sdk 'CobolDotNet.Sdk.csproj') -o $feed -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'pack failed' }
$cache = Join-Path $env:USERPROFILE ".nuget\packages\coboldotnet.sdk\$ver"
if (Test-Path $cache) { Remove-Item -Recurse -Force $cache }
"packed CobolDotNet.Sdk $ver -> $feed"
Get-ChildItem $feed | Select-Object Name, Length | Format-Table -AutoSize | Out-String
