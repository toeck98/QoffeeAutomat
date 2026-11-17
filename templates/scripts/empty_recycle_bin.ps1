<#
.SYNOPSIS
    Empties the recycle bin of the logged-in user.

.DESCRIPTION
    Template for the command "empty_recycle_bin" (templates/commands/empty_recycle_bin.json).
    The recycle bin belongs to the user, so run the command as the logged-in user (runAs: user).
    The files are deleted for good; there is no way back.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Clear-RecycleBin -Force -ErrorAction Stop
Write-Output 'The recycle bin was emptied.'
