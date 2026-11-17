<#
.SYNOPSIS
    Tests the Windows installer end to end: silent install, health check, silent upgrade, uninstall (PLAN.md S13, T601).

.DESCRIPTION
    Needs an elevated PowerShell on Windows and a machine without QoffeeAutomat: the test installs, upgrades and removes the
    service and DELETES %ProgramData%\QoffeeAutomat at the end. It refuses to run when the service or that folder exists.
    CI runs it on windows-latest (ci-dotnet.yml, job "installer"); on your own PC use a virtual machine.

    Steps and checks:
      1. Silent install of the old installer: files, service (LocalSystem, delayed automatic start, running), data
         directories and their ACLs, setup token, firewall rule (TCP link port, private and domain profiles only), Start menu
         shortcut, /healthz, uninstall entry.
      2. Marker files in config and the install directory, then silent install of the new installer over it (the upgrade):
         the service runs again with another process id, /healthz answers, the configuration, the setup token and the
         other data are untouched, the changed program file was replaced, the firewall rule exists once.
      3. Silent uninstall that keeps the data: service, firewall rule, files and shortcut are gone, the data stays.
      4. Install again over the kept data, then uninstall with /PURGEDATA: the data is gone as well.
      5. With -PortableZip: the zip holds the files and the executable starts.

.PARAMETER OldInstaller
    The installer to install first.

.PARAMETER NewInstaller
    The installer to upgrade to.

.PARAMETER OldVersion
    The version of OldInstaller (checked in the uninstall entry).

.PARAMETER NewVersion
    The version of NewInstaller.

.PARAMETER PortableZip
    Optional portable zip to check.

.PARAMETER UiPort
    Port of the loopback UI (default 8480).

.PARAMETER LinkPort
    Port of the QoffeeLink listener (default 8481).

.PARAMETER LogDirectory
    Where the Setup logs go. Default: artifacts/packaging/logs below the repository root.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$OldInstaller,
    [Parameter(Mandatory)] [string]$NewInstaller,
    [Parameter(Mandatory)] [string]$OldVersion,
    [Parameter(Mandatory)] [string]$NewVersion,
    [string]$PortableZip,
    [int]$UiPort = 8480,
    [int]$LinkPort = 8481,
    [string]$LogDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

if (-not $IsWindows) {
    throw 'The installer test runs on Windows only.'
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run the installer test from an elevated PowerShell.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
if (-not $LogDirectory) {
    $LogDirectory = Join-Path $root 'artifacts' 'packaging' 'logs'
}
$LogDirectory = [System.IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
# Start-Process does not resolve relative paths against the PowerShell location.
$OldInstaller = (Resolve-Path -LiteralPath $OldInstaller).Path
$NewInstaller = (Resolve-Path -LiteralPath $NewInstaller).Path
if ($PortableZip) {
    $PortableZip = (Resolve-Path -LiteralPath $PortableZip).Path
}

$serviceName = 'QoffeeAutomat'
$ruleName = 'QoffeeAutomat QoffeeLink'
$installDirectory = Join-Path $env:ProgramFiles 'QoffeeAutomat'
$dataRoot = Join-Path $env:ProgramData 'QoffeeAutomat'
$startMenuFolder = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\QoffeeAutomat'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{8F3C2A5E-6B1D-4E7A-9C42-1D5B7E0A3F96}_is1'
$healthUri = "http://127.0.0.1:$UiPort/healthz"

function Assert-That {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw "FAILED: $Message"
    }
    Write-Host "  ok: $Message"
}

function Wait-Until {
    param([scriptblock]$Condition, [int]$TimeoutSeconds = 90, [string]$What)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (& $Condition) {
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}

function Get-ServiceInfo {
    Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
}

function Get-Health {
    try {
        $response = Invoke-RestMethod -Uri $healthUri -TimeoutSec 5
        return $response
    }
    catch {
        return $null
    }
}

function Assert-Healthy {
    $healthy = Wait-Until { (Get-Health).status -eq 'ok' } -TimeoutSeconds 90
    Assert-That $healthy "$healthUri answers with status ok"
}

function Invoke-Setup {
    param([string]$Installer, [string]$LogName)
    $log = Join-Path $LogDirectory $LogName
    $process = Start-Process -FilePath $Installer -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$log`"")
    Assert-That ($process.ExitCode -eq 0) "$([IO.Path]::GetFileName($Installer)) exited with 0 (was $($process.ExitCode))"
}

function Invoke-Uninstall {
    param([string]$LogName, [switch]$PurgeData)
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    Assert-That (Test-Path -LiteralPath $uninstaller) 'the uninstaller exists'
    $log = Join-Path $LogDirectory $LogName
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$log`"")
    if ($PurgeData) {
        $arguments += '/PURGEDATA'
    }
    $process = Start-Process -FilePath $uninstaller -Wait -PassThru -ArgumentList $arguments
    Assert-That ($process.ExitCode -eq 0) "the uninstaller exited with 0 (was $($process.ExitCode))"
    # The uninstaller hands over to a copy of itself; wait until it is done with the folder.
    $gone = Wait-Until { -not (Test-Path -LiteralPath (Join-Path $installDirectory 'QoffeeAutomat.Agent.exe')) } -TimeoutSeconds 60
    Assert-That $gone 'the agent executable is removed'
}

function Assert-Installed {
    param([string]$Version)
    foreach ($file in @('QoffeeAutomat.Agent.exe', 'QoffeeAutomat.SessionHost.exe', 'wwwroot\index.html', 'templates\commands\set_volume.json', 'templates\scripts\disk_free_percent.ps1', 'unins000.exe')) {
        Assert-That (Test-Path -LiteralPath (Join-Path $installDirectory $file)) "$file is installed in Program Files"
    }

    $service = Get-ServiceInfo
    Assert-That ($null -ne $service) 'the service exists'
    Assert-That ($service.State -eq 'Running') "the service is running (state: $($service.State))"
    Assert-That ($service.StartName -eq 'LocalSystem') "the service runs as LocalSystem (account: $($service.StartName))"
    Assert-That ($service.PathName -like "*QoffeeAutomat.Agent.exe*run*") "the service starts the agent with 'run' ($($service.PathName))"
    $config = (& sc.exe qc $serviceName) -join "`n"
    Assert-That ($config -match 'AUTO_START\s+\(DELAYED\)') 'the service starts automatically, delayed'

    foreach ($directory in @('config', 'scripts', 'secrets', 'data', 'logs')) {
        Assert-That (Test-Path -LiteralPath (Join-Path $dataRoot $directory)) "the data directory $directory exists"
    }
    foreach ($directory in @('config', 'secrets', 'data', 'logs')) {
        $acl = Get-Acl -LiteralPath (Join-Path $dataRoot $directory)
        $open = @($acl.Access | Where-Object { $_.IdentityReference.Value -match '(^|\\)(Users|Everyone|Authenticated Users)$' })
        Assert-That ($open.Count -eq 0) "$directory grants nothing to standard users"
        Assert-That $acl.AreAccessRulesProtected "$directory does not inherit permissions"
    }
    Assert-That (Test-Path -LiteralPath (Join-Path $dataRoot 'secrets\setup-token.txt')) 'the setup token file exists'

    $rules = @(Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)
    Assert-That ($rules.Count -eq 1) 'the firewall rule exists once'
    $profiles = $rules[0].Profile.ToString()
    Assert-That (($profiles -match 'Private') -and ($profiles -match 'Domain') -and ($profiles -notmatch 'Public') -and ($profiles -ne 'Any')) "the rule covers private and domain only (profiles: $profiles)"
    Assert-That ($rules[0].Direction.ToString() -eq 'Inbound' -and $rules[0].Action.ToString() -eq 'Allow') 'the rule allows inbound traffic'
    $port = ($rules[0] | Get-NetFirewallPortFilter).LocalPort
    Assert-That ("$port" -eq "$LinkPort") "the rule opens TCP port $LinkPort (port: $port)"

    $shortcut = Join-Path $startMenuFolder 'QoffeeAutomat.url'
    Assert-That (Test-Path -LiteralPath $shortcut) 'the Start menu shortcut exists'
    Assert-That ((Get-Content -LiteralPath $shortcut -Raw) -match "URL=http://127\.0\.0\.1:$UiPort/") 'the shortcut opens the UI'

    $entry = Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue
    Assert-That ($null -ne $entry) 'the uninstall entry exists'
    Assert-That ($entry.DisplayVersion -eq $Version) "the uninstall entry shows version $Version (was $($entry.DisplayVersion))"

    Assert-Healthy
}

function Assert-Removed {
    Assert-That ($null -eq (Get-ServiceInfo)) 'the service is removed'
    Assert-That ($null -eq (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) 'the firewall rule is removed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'QoffeeAutomat.Agent.exe'))) 'the agent executable is removed'
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'QoffeeAutomat.SessionHost.exe'))) 'the SessionHost executable is removed'
    Assert-That (-not (Test-Path -LiteralPath $startMenuFolder)) 'the Start menu folder is removed'
    Assert-That ($null -eq (Get-ItemProperty -LiteralPath $uninstallKey -ErrorAction SilentlyContinue)) 'the uninstall entry is removed'
    Assert-That ($null -eq (Get-Health)) "$healthUri no longer answers"
    Assert-That (@(Get-Process -Name 'QoffeeAutomat.Agent', 'QoffeeAutomat.SessionHost' -ErrorAction SilentlyContinue).Count -eq 0) 'no QoffeeAutomat process is left'
}

function Show-Diagnostics {
    Write-Host '::group::Diagnostics'
    Write-Host "Service: $((Get-ServiceInfo | Format-List Name, State, StartName, PathName | Out-String).Trim())"
    foreach ($logFile in Get-ChildItem -LiteralPath $LogDirectory -Filter '*.log' -ErrorAction SilentlyContinue) {
        Write-Host "--- $($logFile.Name) (last 60 lines)"
        Get-Content -LiteralPath $logFile.FullName -Tail 60 -ErrorAction SilentlyContinue
    }
    $agentLogs = Join-Path $dataRoot 'logs'
    foreach ($logFile in Get-ChildItem -LiteralPath $agentLogs -File -ErrorAction SilentlyContinue | Select-Object -Last 3) {
        Write-Host "--- agent log $($logFile.Name) (last 60 lines)"
        Get-Content -LiteralPath $logFile.FullName -Tail 60 -ErrorAction SilentlyContinue
    }
    Write-Host '--- Event Log (source QoffeeAutomat, last 10)'
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $serviceName } -MaxEvents 10 -ErrorAction SilentlyContinue |
        Format-List TimeCreated, LevelDisplayName, Message
    Write-Host '::endgroup::'
}

# A machine with QoffeeAutomat on it is not a test machine: the test deletes the data at the end.
if ($null -ne (Get-ServiceInfo) -or (Test-Path -LiteralPath $dataRoot) -or (Test-Path -LiteralPath $installDirectory)) {
    throw "Refusing to run: the service, $installDirectory or $dataRoot exists, and this test deletes them. Use a clean machine or virtual machine."
}

try {
    Write-Host "== 1. Silent install of $OldVersion"
    Invoke-Setup -Installer $OldInstaller -LogName 'setup-install.log'
    Assert-Installed -Version $OldVersion

    Write-Host '== 2. Silent upgrade to' $NewVersion
    $marker = Join-Path $dataRoot 'config\installer-test.marker'
    Set-Content -LiteralPath $marker -Value 'configuration that must survive the upgrade'
    $tokenFile = Join-Path $dataRoot 'secrets\setup-token.txt'
    $tokenBefore = (Get-FileHash -LiteralPath $tokenFile).Hash
    $index = Join-Path $installDirectory 'wwwroot\index.html'
    Add-Content -LiteralPath $index -Value '<!-- tampered by the installer test -->'
    $pidBefore = (Get-ServiceInfo).ProcessId
    Assert-That ($pidBefore -gt 0) "the service has a process before the upgrade (pid $pidBefore)"

    Invoke-Setup -Installer $NewInstaller -LogName 'setup-upgrade.log'
    Assert-Installed -Version $NewVersion
    $pidAfter = (Get-ServiceInfo).ProcessId
    Assert-That ($pidAfter -gt 0 -and $pidAfter -ne $pidBefore) "the service was restarted (pid $pidBefore -> $pidAfter)"
    Assert-That (Test-Path -LiteralPath $marker) 'the configuration survived the upgrade'
    Assert-That ((Get-FileHash -LiteralPath $tokenFile).Hash -eq $tokenBefore) 'the setup token survived the upgrade'
    Assert-That (-not ((Get-Content -LiteralPath $index -Raw) -match 'tampered by the installer test')) 'the program files were replaced'

    Write-Host '== 3. Silent uninstall that keeps the data'
    Invoke-Uninstall -LogName 'setup-uninstall-keep.log'
    Assert-Removed
    Assert-That (Test-Path -LiteralPath $marker) 'the configuration is kept'
    Assert-That (Test-Path -LiteralPath (Join-Path $dataRoot 'secrets')) 'the secrets folder is kept'

    Write-Host '== 4. Install again over the kept data, then uninstall with /PURGEDATA'
    Invoke-Setup -Installer $NewInstaller -LogName 'setup-reinstall.log'
    Assert-Installed -Version $NewVersion
    Assert-That (Test-Path -LiteralPath $marker) 'the kept configuration is still there after the reinstall'
    Invoke-Uninstall -LogName 'setup-uninstall-purge.log' -PurgeData
    Assert-Removed
    $purged = Wait-Until { -not (Test-Path -LiteralPath $dataRoot) } -TimeoutSeconds 30
    Assert-That $purged 'the data folder is deleted'

    if ($PortableZip) {
        Write-Host '== 5. Portable zip'
        $extract = Join-Path ([IO.Path]::GetTempPath()) "qoffee-portable-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $extract | Out-Null
        try {
            Expand-Archive -LiteralPath $PortableZip -DestinationPath $extract
            $top = @(Get-ChildItem -LiteralPath $extract -Directory)
            Assert-That ($top.Count -eq 1) 'the zip has one top-level folder'
            foreach ($file in @('QoffeeAutomat.Agent.exe', 'QoffeeAutomat.SessionHost.exe', 'wwwroot\index.html', 'templates\commands\set_volume.json', 'README-portable.txt', 'LICENSE')) {
                Assert-That (Test-Path -LiteralPath (Join-Path $top[0].FullName $file)) "the zip contains $file"
            }
            $version = (& (Join-Path $top[0].FullName 'QoffeeAutomat.Agent.exe') version) -join ' '
            Assert-That ($LASTEXITCODE -eq 0 -and $version -match '\d+\.\d+\.\d+') "the portable agent starts and reports its version ($version)"
        }
        finally {
            Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host 'Installer test passed.'
}
catch {
    Write-Host "::error::$($_.Exception.Message)"
    Show-Diagnostics
    throw
}
