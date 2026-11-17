<#
.SYNOPSIS
    Compiles the Inno Setup installer from the staged files (PLAN.md S13, T601).

.DESCRIPTION
    Run publish.ps1 first: it publishes the agent and the SessionHost and stages them. This script passes the version and
    the paths to QoffeeAutomat.iss through environment variables and runs the Inno Setup compiler (ISCC.exe, Inno Setup
    6.3 or newer). The result is QoffeeAutomat-Setup-<version>-<arch>.exe in the output directory.

.PARAMETER Arch
    x64 or arm64.

.PARAMETER Version
    SemVer shown in the installer. Default: <Version> of Directory.Build.props. It does not change the executables; those
    carry the version that publish.ps1 stamped.

.PARAMETER StageDirectory
    The staging directory. Default: <OutputDirectory>/stage/win-<arch>.

.PARAMETER OutputDirectory
    Where the installer goes. Default: artifacts/packaging below the repository root.

.PARAMETER IsccPath
    Path of ISCC.exe. Default: the environment variable ISCC, then ISCC.exe on the PATH, then the usual install folders.

.EXAMPLE
    pwsh packaging/windows/publish.ps1 -Arch x64
    pwsh packaging/windows/build-installer.ps1 -Arch x64
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Arch,

    [string]$Version,

    [string]$StageDirectory,

    [string]$OutputDirectory,

    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
if (-not $Version) {
    $props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw
    if ($props -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
        throw 'Directory.Build.props has no <Version>.'
    }
    $Version = $Matches[1]
}
if ($Version -notmatch '^(\d+\.\d+\.\d+)(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a SemVer version (MAJOR.MINOR.PATCH[-prerelease])."
}
$numeric = "$($Matches[1]).0"

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts' 'packaging'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
if (-not $StageDirectory) {
    $StageDirectory = Join-Path $OutputDirectory 'stage' "win-$Arch"
}
$StageDirectory = [System.IO.Path]::GetFullPath($StageDirectory)
foreach ($required in @('QoffeeAutomat.Agent.exe', 'QoffeeAutomat.SessionHost.exe', 'wwwroot\index.html', 'templates\commands\set_volume.json', 'templates\sensors\disk_free_percent.json', 'templates\scripts\disk_free_percent.ps1')) {
    if (-not (Test-Path -LiteralPath (Join-Path $StageDirectory $required))) {
        throw "$StageDirectory lacks $required. Run publish.ps1 -Arch $Arch first."
    }
}

if (-not $IsccPath) {
    $candidates = @($env:ISCC)
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        $candidates += $command.Source
    }
    foreach ($programFiles in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, "$env:LOCALAPPDATA\Programs")) {
        if ($programFiles) {
            $candidates += Join-Path $programFiles 'Inno Setup 6' 'ISCC.exe'
        }
    }
    $IsccPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
}
if (-not $IsccPath) {
    throw 'ISCC.exe (Inno Setup 6.3 or newer) was not found. Install it, put it on the PATH or pass -IsccPath.'
}

$env:QOFFEE_APP_VERSION = $Version
$env:QOFFEE_VERSION_NUMERIC = $numeric
$env:QOFFEE_ARCH = $Arch
$env:QOFFEE_SOURCE_DIR = $StageDirectory
$env:QOFFEE_OUTPUT_DIR = $OutputDirectory

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Write-Host "==> Compile the installer ($Arch, $Version) with $IsccPath"
& $IsccPath (Join-Path $PSScriptRoot 'QoffeeAutomat.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed with exit code $LASTEXITCODE."
}

$installer = Join-Path $OutputDirectory "QoffeeAutomat-Setup-$Version-$Arch.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "The compiler did not produce $installer."
}
Write-Host "==> Installer: $installer"
