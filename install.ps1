#requires -Version 5.1
<#
    SqlHelper installer.

    Safe to run any number of times: it checks each prerequisite before acting, then always
    rebuilds and republishes so the installed copy matches whatever source is on disk right now.
    Nothing here talks to anything but dotnet's own package feed (to fetch build dependencies)
    and, once, winget (only if the .NET SDK isn't already present) - the built application itself
    makes no network calls of its own.
#>

$ErrorActionPreference = 'Stop'
$repoRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $repoRoot 'src\SqlHelper.App\SqlHelper.App.csproj'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\SqlHelper'
$exePath    = Join-Path $installDir 'SqlHelper.App.exe'
$minSdkMajor = 10

function Write-Step  { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok    { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn2 { param([string]$Message) Write-Host "    $Message" -ForegroundColor Yellow }
function Write-Err2  { param([string]$Message) Write-Host "    $Message" -ForegroundColor Red }

function Fail {
    param([string]$Message)
    Write-Host ''
    Write-Err2 $Message
    exit 1
}

Write-Host ''
Write-Host 'SqlHelper installer' -ForegroundColor White
Write-Host '--------------------'

if (-not (Test-Path $appProject)) {
    Fail "Can not find $appProject - run this script from inside the SqlHelper repository."
}

# ---------------------------------------------------------------------------
# 1. .NET SDK (build-time only - the published app is self-contained and
#    needs no runtime installed on the machine that finally runs it).
# ---------------------------------------------------------------------------
Write-Step "Checking for the .NET SDK ($minSdkMajor.0+)"

function Test-DotnetSdk {
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    if ($LASTEXITCODE -ne 0) { return $false }
    foreach ($line in $sdks) {
        if ($line -match '^(\d+)\.') {
            if ([int]$Matches[1] -ge $minSdkMajor) { return $true }
        }
    }
    return $false
}

if (Test-DotnetSdk) {
    Write-Ok 'Found.'
}
else {
    Write-Warn2 ".NET $minSdkMajor SDK not found."
    $wingetCmd = Get-Command winget -ErrorAction SilentlyContinue
    if ($wingetCmd) {
        Write-Step 'Installing the .NET SDK via winget (this can take a few minutes)'
        & winget install --id Microsoft.DotNet.SDK.$minSdkMajor -e --accept-package-agreements --accept-source-agreements
        # winget updates PATH for new shells, not this one - refresh from machine/user values.
        $env:Path = [System.Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                    [System.Environment]::GetEnvironmentVariable('Path', 'User')
    }
    if (-not (Test-DotnetSdk)) {
        Fail ("Could not find or install the .NET $minSdkMajor SDK automatically. " +
              "Install it from https://dotnet.microsoft.com/download/dotnet/$minSdkMajor.0 and re-run this script.")
    }
    Write-Ok 'Installed.'
}

# ---------------------------------------------------------------------------
# 2. Build - the fastest way to prove "everything is OK" on a re-run.
# ---------------------------------------------------------------------------
Write-Step 'Building SqlHelper (Release)'
& dotnet build $appProject -c Release -v quiet --nologo
if ($LASTEXITCODE -ne 0) {
    Fail 'Build failed - see the errors above.'
}
Write-Ok 'Build succeeded.'

# ---------------------------------------------------------------------------
# 3. Publish a self-contained, single-file build - this copy needs no .NET
#    runtime on whatever PC it ends up on.
# ---------------------------------------------------------------------------
Write-Step "Publishing to $installDir"

# A running copy holds its own .exe open, which makes the publish fail with a
# confusing MSBuild stack trace. Close it first so re-running this is painless.
$running = Get-Process -Name 'SqlHelper.App' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '    SqlHelper is open - closing it so the files can be replaced.'
    $running | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

& dotnet publish $appProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $installDir -v quiet --nologo
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
    Fail 'Publish failed - see the errors above.'
}
Write-Ok "Published $exePath"

# ---------------------------------------------------------------------------
# 4. Start Menu shortcut (idempotent - overwritten every run).
# ---------------------------------------------------------------------------
Write-Step 'Creating a Start Menu shortcut'
$startMenu = [Environment]::GetFolderPath('Programs')
$shortcutPath = Join-Path $startMenu 'SqlHelper.lnk'
$shellCom = New-Object -ComObject WScript.Shell
$shortcut = $shellCom.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'SqlHelper - local multi-database query and deployment tool'
$shortcut.Save()
Write-Ok "Shortcut: $shortcutPath"

Write-Host ''
Write-Host 'SqlHelper is installed and ready.' -ForegroundColor Green
Write-Host '  Start Menu -> SqlHelper, or run directly:'
Write-Host "    $exePath"
Write-Host '  All data (the connection registry, backups, audit log) stays under %APPDATA%\SqlHelper on this PC.'
Write-Host ''
