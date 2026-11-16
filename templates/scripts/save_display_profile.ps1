<#
.SYNOPSIS
    Saves the current display configuration (monitor layout) as a named profile.

.DESCRIPTION
    Template for the command "save_display_profile" (templates/commands/save_display_profile.json). Arrange
    the monitors the way you want them, then run the command with a name; set_display_profile.ps1 applies the
    profile later.

    The profile is written with Export-Clixml to "<name>.xml" in:

      1. the directory in the environment variable QOFFEEAUTOMAT_DISPLAY_PROFILES, if it is set,
      2. otherwise %ProgramData%\QoffeeAutomat\display-profiles.

    The directory is created when it does not exist.

    Requires the DisplayConfig module: Install-Module DisplayConfig -Scope AllUsers

.PARAMETER Name
    The profile name, for example "TV-Mode": letters, digits, space, dash and underscore.

.PARAMETER Overwrite
    Replaces an existing profile of the same name. Without it the script fails instead.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9 _-]{0,63}$')]
    [string] $Name,

    [bool] $Overwrite = $false
)

$ErrorActionPreference = 'Stop'

if (-not [string]::IsNullOrWhiteSpace($env:QOFFEEAUTOMAT_DISPLAY_PROFILES)) {
    $directory = $env:QOFFEEAUTOMAT_DISPLAY_PROFILES
}
elseif (-not [string]::IsNullOrWhiteSpace($env:ProgramData)) {
    $directory = Join-Path $env:ProgramData 'QoffeeAutomat\display-profiles'
}
else {
    throw 'Neither QOFFEEAUTOMAT_DISPLAY_PROFILES nor ProgramData is set.'
}

if (-not (Get-Module -ListAvailable -Name DisplayConfig)) {
    throw 'The DisplayConfig module is not installed. Run: Install-Module DisplayConfig -Scope AllUsers'
}
Import-Module DisplayConfig

if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$path = Join-Path $directory ($Name + '.xml')
if ((Test-Path -LiteralPath $path) -and -not $Overwrite) {
    throw "The profile '$Name' already exists. Run the command with Overwrite to replace it."
}

Get-DisplayConfig | Export-Clixml -LiteralPath $path

Write-Output "Saved display profile '$Name' to $path"
