<#
.SYNOPSIS
    Reports the free space of a drive in percent (one number, the last line of the output).

.DESCRIPTION
    Template for the sensor "disk_free_percent" (templates/sensors/disk_free_percent.json).
    Change the default of -DriveLetter to watch another drive.

.PARAMETER DriveLetter
    The drive letter without colon, for example "D".
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z]$')]
    [string] $DriveLetter = 'C'
)

$ErrorActionPreference = 'Stop'

$volume = Get-Volume -DriveLetter $DriveLetter -ErrorAction Stop
if ($volume.Size -le 0) {
    throw "The drive $DriveLetter has no size."
}

[math]::Round(100.0 * $volume.SizeRemaining / $volume.Size, 1).ToString([System.Globalization.CultureInfo]::InvariantCulture)
