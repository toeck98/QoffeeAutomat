<#
.SYNOPSIS
    Applies a saved display profile (monitor layout) with the DisplayConfig module.

.DESCRIPTION
    Template for the command "set_display_profile" (templates/commands/set_display_profile.json).
    Port of the display switching of the QoffeAutomat prototype (docs/migration-from-prototype.md).

    The profile is an XML file that save_display_profile.ps1 created with Export-Clixml. It is looked up in
    these directories, in this order (the first match wins):

      1. the directory in the environment variable QOFFEEAUTOMAT_DISPLAY_PROFILES (set it for the user or
         the machine to choose another location),
      2. %ProgramData%\QoffeeAutomat\display-profiles,
      3. C:\ProgramData\Automation\Modes (the folder of the prototype; existing profiles keep working).

    Run the command as the logged-in user (runAs: user): the display configuration belongs to the
    interactive session and cannot be changed from the service.

    Requires the DisplayConfig module: Install-Module DisplayConfig -Scope AllUsers

.PARAMETER Profile
    The profile name, for example "TV-Mode". The file name is the name plus ".xml"; "Study Mode" and
    "Study-Mode" find the same file. Paths are not accepted.

.PARAMETER UpdateAdapterIds
    Passes -UpdateAdapterIds to Use-DisplayConfig. Use it when the profile was saved before a graphics
    driver update or a change of the graphics card, which changes the adapter ids.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Profile,

    [bool] $UpdateAdapterIds = $false
)

$ErrorActionPreference = 'Stop'

# The name must be a plain file name: no directory part, no characters that Windows forbids in file names.
if ($Profile -ne [System.IO.Path]::GetFileName($Profile) -or $Profile.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0) {
    throw "Invalid profile name '$Profile'. Use the name only, without a path."
}

$directories = @()
if (-not [string]::IsNullOrWhiteSpace($env:QOFFEEAUTOMAT_DISPLAY_PROFILES)) {
    $directories += $env:QOFFEEAUTOMAT_DISPLAY_PROFILES
}
if (-not [string]::IsNullOrWhiteSpace($env:ProgramData)) {
    $directories += (Join-Path $env:ProgramData 'QoffeeAutomat\display-profiles')
    $directories += (Join-Path $env:ProgramData 'Automation\Modes')
}

$candidates = @(
    $Profile,
    "$Profile.xml",
    ($Profile.Replace(' ', '-') + '.xml'),
    ($Profile.Replace('-', ' ') + '.xml')
) | Select-Object -Unique

$profilePath = $null
foreach ($directory in $directories) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        continue
    }

    foreach ($candidate in $candidates) {
        $path = Join-Path $directory $candidate
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $profilePath = $path
            break
        }
    }

    if ($null -eq $profilePath) {
        # Last resort of the prototype: any profile whose file name contains the name.
        $match = Get-ChildItem -LiteralPath $directory -Filter '*.xml' -File |
            Where-Object { $_.Name.IndexOf($Profile, [System.StringComparison]::OrdinalIgnoreCase) -ge 0 } |
            Select-Object -First 1
        if ($null -ne $match) {
            $profilePath = $match.FullName
        }
    }

    if ($null -ne $profilePath) {
        break
    }
}

if ($null -eq $profilePath) {
    $searched = if ($directories.Count -gt 0) { $directories -join '; ' } else { '(no directory)' }
    throw "Display profile '$Profile' was not found. Searched: $searched"
}

if (-not (Get-Module -ListAvailable -Name DisplayConfig)) {
    throw 'The DisplayConfig module is not installed. Run: Install-Module DisplayConfig -Scope AllUsers'
}
Import-Module DisplayConfig

$configuration = Import-Clixml -LiteralPath $profilePath
if ($UpdateAdapterIds) {
    $configuration | Use-DisplayConfig -UpdateAdapterIds
}
else {
    $configuration | Use-DisplayConfig
}

Write-Output "Applied display profile '$Profile' from $profilePath"
