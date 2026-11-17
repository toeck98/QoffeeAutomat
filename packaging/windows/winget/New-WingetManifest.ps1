#Requires -Version 7.0
<#
.SYNOPSIS
    Renders the winget manifest of a published QoffeeAutomat release from the templates in this directory.

.DESCRIPTION
    PLAN.md S12, task T607. The templates (*.yaml.in) hold the fixed facts of the package; this script fills in the version, the
    release date and the SHA-256 of the two installers, which it reads from the SHA256SUMS.txt of the release. The result is the
    multi-file manifest that microsoft/winget-pkgs expects. It is used for the FIRST submission (the workflow
    .github/workflows/winget.yml can only update a package that already exists upstream); see docs/maintainers/winget.md.

.PARAMETER Version
    The release version without the leading v, for example 1.0.0. Prereleases are not published to winget.

.PARAMETER ChecksumsFile
    The SHA256SUMS.txt of the release (a release asset). Download it with
    gh release download v<version> --pattern SHA256SUMS.txt.

.PARAMETER OutputDir
    Where the three manifest files are written. Default: artifacts/winget/<version> below the current directory.

.PARAMETER ReleaseDate
    Release date as yyyy-MM-dd. Default: today (UTC).

.EXAMPLE
    gh release download v1.0.0 --repo toeck98/QoffeeAutomat --pattern SHA256SUMS.txt --dir artifacts/winget
    pwsh packaging/windows/winget/New-WingetManifest.ps1 -Version 1.0.0 -ChecksumsFile artifacts/winget/SHA256SUMS.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [Parameter(Mandatory)]
    [string]$ChecksumsFile,

    [string]$OutputDir = (Join-Path $PWD "artifacts/winget/$Version"),

    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
    [string]$ReleaseDate = ([DateTime]::UtcNow.ToString('yyyy-MM-dd'))
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ChecksumsFile -PathType Leaf)) {
    throw "The checksum file '$ChecksumsFile' does not exist."
}

# "<64 hex digits>  <file name>" (two spaces, or " *" for binary mode), as sha256sum writes it.
$sums = @{}
foreach ($line in Get-Content -LiteralPath $ChecksumsFile) {
    if ($line -match '^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>\S+)$') {
        $sums[$Matches['name']] = $Matches['hash'].ToUpperInvariant()
    }
}

$hashes = @{}
foreach ($arch in 'x64', 'arm64') {
    $name = "QoffeeAutomat-Setup-$Version-$arch.exe"
    if (-not $sums.ContainsKey($name)) {
        throw "$ChecksumsFile has no checksum for $name."
    }
    $hashes[$arch] = $sums[$name]
}

$values = @{
    '@VERSION@'      = $Version
    '@RELEASE_DATE@' = $ReleaseDate
    '@X64_SHA256@'   = $hashes['x64']
    '@ARM64_SHA256@' = $hashes['arm64']
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$templates = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'toeck98.QoffeeAutomat*.yaml.in')
if ($templates.Count -ne 3) {
    throw "Expected three manifest templates in $PSScriptRoot, found $($templates.Count)."
}

foreach ($template in $templates) {
    $text = Get-Content -LiteralPath $template.FullName -Raw
    foreach ($key in $values.Keys) {
        $text = $text.Replace($key, $values[$key])
    }
    if ($text -match '@[A-Z0-9_]+@') {
        throw "$($template.Name) still has the marker $($Matches[0])."
    }
    $target = Join-Path $OutputDir ($template.Name -replace '\.in$', '')
    [System.IO.File]::WriteAllText($target, $text, $utf8)
    Write-Host "Wrote $target"
}

Write-Host ''
Write-Host "Next: winget validate --manifest `"$OutputDir`""
Write-Host "      wingetcreate submit --token <token> `"$OutputDir`"   (see docs/maintainers/winget.md)"
