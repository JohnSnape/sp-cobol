# Build the COBOL editor extension (VSIX) and run its logic tests.
#
#   powershell -ExecutionPolicy Bypass -File tools\build-editor.ps1            # build + test
#   powershell -ExecutionPolicy Bypass -File tools\build-editor.ps1 -Deploy    # also install into the
#                                                                             # experimental instance
# Output: dist\CobolDotNet.Editor.vsix
param([switch]$Deploy, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# logic tests first (no Visual Studio needed)
dotnet run --project "$root\editor\CobolDotNet.Editor.Tests" -c Release -- $root
if ($LASTEXITCODE -ne 0) { throw "editor logic tests failed" }

# a VSIX needs Visual Studio's MSBuild (the one with the VS SDK workload, else any 2022+)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VSSDK -property installationPath
if (-not $vs) { $vs = & $vswhere -version '[17.0,19.0)' -latest -products * -property installationPath }
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
Write-Host "MSBuild: $msbuild"

$deployArg = if ($Deploy) { '-p:DeployExtension=true' } else { '-p:DeployExtension=false' }
& $msbuild "$root\editor\CobolDotNet.Editor\CobolDotNet.Editor.csproj" -restore -nologo -v:m `
    "-p:Configuration=$Configuration" $deployArg
if ($LASTEXITCODE -ne 0) { throw "VSIX build failed" }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$vsix = Get-ChildItem "$root\editor\CobolDotNet.Editor\bin\$Configuration" -Recurse -Filter *.vsix | Select-Object -First 1
Copy-Item $vsix.FullName "$root\dist\CobolDotNet.Editor.vsix" -Force
Write-Host "VSIX: $root\dist\CobolDotNet.Editor.vsix"
