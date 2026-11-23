<#
  Pack the project templates into dist\SpCobol.Templates.<ver>.nupkg (a NuGet template package).
  Users install it with tools\install.ps1, or by hand:  dotnet new install SpCobol.Templates.<ver>.nupkg
#>
$ErrorActionPreference = 'Stop'
$proj = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $proj 'dist'
dotnet pack (Join-Path $proj 'templates\SpCobol.Templates.csproj') -o $dist -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'template pack failed' }
Get-ChildItem $dist -Filter 'SpCobol.Templates.*.nupkg' | Select-Object Name, Length | Format-Table -AutoSize | Out-String
