<#
.SYNOPSIS
    Publishes the agent and the SessionHost for Windows, stages them and builds the portable zip (PLAN.md S13, T601).

.DESCRIPTION
    Runs the publish profiles win-x64 or win-arm64 of src/QoffeeAutomat.Agent and src/QoffeeAutomat.SessionHost
    (self-contained, single file, ReadyToRun, version stamped, icon assets/brand/app.ico), copies both into one staging
    directory (the agent expects QoffeeAutomat.SessionHost.exe next to itself, PLAN.md S09 item 7) and writes
    QoffeeAutomat-<version>-win-<arch>.zip. The installer is built from the staging directory by build-installer.ps1.

    Output (below -OutputDirectory, default artifacts/packaging):
      stage/win-<arch>/                         what the installer and the zip contain
      QoffeeAutomat-<version>-win-<arch>.zip    portable distribution

.PARAMETER Arch
    x64 or arm64.

.PARAMETER Version
    SemVer to stamp into the executables. Default: <Version> of Directory.Build.props.

.PARAMETER OutputDirectory
    Where the staging directory and the zip go. Default: artifacts/packaging below the repository root.

.PARAMETER SkipZip
    Stage only.

.EXAMPLE
    pwsh packaging/windows/publish.ps1 -Arch x64
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('x64', 'arm64')]
    [string]$Arch,

    [string]$Version,

    [string]$OutputDirectory,

    [switch]$SkipZip
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
if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "'$Version' is not a SemVer version (MAJOR.MINOR.PATCH[-prerelease])."
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $root 'artifacts' 'packaging'
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$rid = "win-$Arch"
$publishRoot = Join-Path $root 'artifacts' 'publish'
$agentDir = Join-Path $publishRoot 'agent' $rid
$sessionHostDir = Join-Path $publishRoot 'sessionhost' $rid
$stage = Join-Path $OutputDirectory 'stage' $rid

function Invoke-Native {
    param([string]$Description, [scriptblock]$Command)
    Write-Host "==> $Description"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

foreach ($directory in @($agentDir, $sessionHostDir, $stage)) {
    if (Test-Path -LiteralPath $directory) {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}

# The agent is built for net10.0 and net10.0-windows; only the Windows build contains the Windows platform and the
# service verb (QoffeeAutomat.Agent.csproj), so the framework is named explicitly.
Invoke-Native "Publish the agent ($rid, $Version)" {
    dotnet publish (Join-Path $root 'src' 'QoffeeAutomat.Agent') -c Release -f net10.0-windows -r $rid `
        "-p:PublishProfile=$rid" "-p:Version=$Version" --nologo
}
Invoke-Native "Publish the SessionHost ($rid, $Version)" {
    dotnet publish (Join-Path $root 'src' 'QoffeeAutomat.SessionHost') -c Release -r $rid `
        "-p:PublishProfile=$rid" "-p:Version=$Version" --nologo
}

$agentExe = Join-Path $agentDir 'QoffeeAutomat.Agent.exe'
$sessionHostExe = Join-Path $sessionHostDir 'QoffeeAutomat.SessionHost.exe'
foreach ($required in @($agentExe, $sessionHostExe, (Join-Path $agentDir 'wwwroot' 'index.html'), (Join-Path $agentDir 'templates' 'commands' 'set_volume.json'), (Join-Path $agentDir 'templates' 'sensors' 'disk_free_percent.json'), (Join-Path $agentDir 'templates' 'scripts' 'disk_free_percent.ps1'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "The publish output lacks $required."
    }
}

Write-Host "==> Stage into $stage"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -Path (Join-Path $agentDir '*') -Destination $stage -Recurse -Force
foreach ($file in Get-ChildItem -LiteralPath $sessionHostDir -File -Recurse) {
    $relative = $file.FullName.Substring($sessionHostDir.Length).TrimStart('\', '/')
    $target = Join-Path $stage $relative
    if (Test-Path -LiteralPath $target) {
        # Files that both publishes produce (none today) must be identical, or one of the programs would break.
        if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "$relative differs between the agent and the SessionHost publish output."
        }
        continue
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $stage 'LICENSE')

# Version stamp check: the file version of both executables starts with the numeric part of the version.
$numeric = ($Version -split '-')[0]
foreach ($name in @('QoffeeAutomat.Agent.exe', 'QoffeeAutomat.SessionHost.exe')) {
    $info = (Get-Item -LiteralPath (Join-Path $stage $name)).VersionInfo
    if (-not $info.FileVersion.StartsWith($numeric)) {
        throw "$name has file version '$($info.FileVersion)', expected $numeric."
    }
    Write-Host ("    {0}: file version {1}, product version {2}" -f $name, $info.FileVersion, $info.ProductVersion)
}

# A smoke run when the executable can run on this machine.
$osArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($osArchitecture -eq $Arch) {
    Invoke-Native 'Smoke run: qoffeeautomat version' { & (Join-Path $stage 'QoffeeAutomat.Agent.exe') version }
}

if (-not $SkipZip) {
    $zipName = "QoffeeAutomat-$Version-$rid"
    $zipSource = Join-Path $OutputDirectory 'zip' $zipName
    $zipPath = Join-Path $OutputDirectory "$zipName.zip"
    foreach ($path in @($zipSource, $zipPath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $zipSource -Force | Out-Null
    Copy-Item -Path (Join-Path $stage '*') -Destination $zipSource -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-portable.txt') -Destination (Join-Path $zipSource 'README-portable.txt')

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($zipSource, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Remove-Item -LiteralPath $zipSource -Recurse -Force
    Write-Host "==> Portable zip: $zipPath"
}

Write-Host "==> Staged: $stage"
