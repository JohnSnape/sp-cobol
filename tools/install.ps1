<#
  SP COBOL installer for Windows.

  From the web (downloads the latest release from GitHub):
    powershell -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/JohnSnape/sp-cobol/main/tools/install.ps1 | iex"

  From a folder that already holds the release files:
    powershell -ExecutionPolicy Bypass -File install.ps1 -From C:\Downloads\sp-cobol

  What it does:
    1. puts CobolDotNet.Sdk.*.nupkg (compiler + build support) in %LOCALAPPDATA%\SP COBOL\feed,
       the folder every new COBOL project looks in (its nuget.config says so);
    2. installs the project templates (dotnet new cobol-screen, and Visual Studio's New Project);
    3. starts the Visual Studio editor extension's installer, if Visual Studio is present
       (skip with -NoEditor).
  Needs the .NET 9 SDK (https://dot.net). Nothing goes outside your user profile except what the
  Visual Studio extension installer itself does.
#>
param(
    [string]$From,
    [switch]$NoEditor,
    [string]$Repo = 'JohnSnape/sp-cobol'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# .NET SDK 9 or later
$sdks = @(& dotnet --list-sdks 2>$null)
if (-not ($sdks | Where-Object { [int]($_.Split('.')[0]) -ge 9 })) {
    throw "SP COBOL needs the .NET 9 SDK (or later). Get it from https://dot.net, then run this again."
}

# the release files: from a folder, or the latest GitHub release
if (-not $From) {
    $From = Join-Path ([IO.Path]::GetTempPath()) ("sp-cobol-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $From | Out-Null
    $rel = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases?per_page=1" -Headers @{ 'User-Agent' = 'sp-cobol-installer' }
    $rel = @($rel)[0]
    Write-Host "Downloading SP COBOL $($rel.tag_name)..."
    foreach ($a in $rel.assets) {
        Invoke-WebRequest $a.browser_download_url -OutFile (Join-Path $From $a.name) -Headers @{ 'User-Agent' = 'sp-cobol-installer' }
    }
}
function Find-File([string]$Pattern) {
    Get-ChildItem $From -Filter $Pattern | Sort-Object LastWriteTime | Select-Object -Last 1
}
$sdkPkg  = Find-File 'CobolDotNet.Sdk.*.nupkg'
$tplPkg  = Find-File 'SpCobol.Templates.*.nupkg'
$editor  = Find-File 'CobolDotNet.Editor.vsix'
if (-not $sdkPkg) { throw "No CobolDotNet.Sdk.*.nupkg in $From" }
if (-not $tplPkg) { throw "No SpCobol.Templates.*.nupkg in $From" }

# 1. the package folder every COBOL project reads
$feed = Join-Path $env:LOCALAPPDATA 'SP COBOL\feed'
New-Item -ItemType Directory -Force $feed | Out-Null
Copy-Item $sdkPkg.FullName $feed -Force
# a re-install of the same version must replace NuGet's cached copy
if ($sdkPkg.Name -match '^CobolDotNet\.Sdk\.(.+)\.nupkg$') {
    $cache = Join-Path $env:USERPROFILE ".nuget\packages\coboldotnet.sdk\$($Matches[1])"
    if (Test-Path $cache) { Remove-Item -Recurse -Force $cache }
}
Write-Host "Compiler package: $feed\$($sdkPkg.Name)"

# 2. templates
& dotnet new install $tplPkg.FullName --force | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Installing the project templates failed" }
Write-Host "Project templates installed (dotnet new cobol-screen)"

# 3. Visual Studio editor
if (-not $NoEditor -and $editor) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vs = if (Test-Path $vswhere) { & $vswhere -latest -products * -property installationPath } else { $null }
    if ($vs) {
        Write-Host "Starting the Visual Studio extension installer (close Visual Studio first)..."
        Start-Process (Join-Path $vs 'Common7\IDE\VSIXInstaller.exe') -ArgumentList "`"$($editor.FullName)`"" -Wait
    } else {
        Write-Host "Visual Studio not found; skipped the editor extension."
    }
}

Write-Host ""
Write-Host "Done. Try it:"
Write-Host "  dotnet new cobol-screen -n Hello"
Write-Host "  cd Hello"
Write-Host "  dotnet run"
