# Install on Windows

## Requirements

* Windows 10 version 1809 or newer, or Windows 11; 64-bit Intel/AMD (`x64`) or ARM64 (`arm64`).
* Administrator rights for the installation.
* Nothing else: the agent is self-contained and brings its own .NET runtime. PowerShell commands use PowerShell 7 (`pwsh`)
  if it is installed and Windows PowerShell 5.1 otherwise.

## Download and verify

Download the installer for your PC from the [latest release](https://github.com/toeck98/QoffeeAutomat/releases/latest):

| File | For |
|------|-----|
| `QoffeeAutomat-Setup-<version>-x64.exe` | 64-bit Intel or AMD |
| `QoffeeAutomat-Setup-<version>-arm64.exe` | ARM64 (for example Snapdragon PCs) |
| `QoffeeAutomat-<version>-win-x64.zip`, `...-win-arm64.zip` | Portable version without installer (see below) |

The agent runs with the highest rights of the system, so check what you downloaded: [Verify your download](verify-download.md)
explains checksums, build attestations and signatures, and what to do about the SmartScreen warning.

## Install

1. Run the installer and accept the elevation prompt (User Account Control).
2. Follow the pages. There is no choice of folder: the agent is installed to `C:\Program Files\QoffeeAutomat`, a place that
   standard users cannot change. This matters because the service runs as LocalSystem and starts every file in its folder.
3. The last page shows the path of the **setup token** and the address of the web interface. Select *Open the QoffeeAutomat
   UI* to continue with the [first-run setup](first-run.md).

The installer

* installs the files and registers the Windows service **QoffeeAutomat** (account LocalSystem, automatic delayed start,
  restart after a failure) and starts it,
* creates `%ProgramData%\QoffeeAutomat` with its sub-folders and restricts `config`, `scripts` and `secrets` to `SYSTEM` and
  Administrators,
* adds a Windows firewall rule *QoffeeAutomat QoffeeLink* that opens TCP port 8481 for the **private and domain** network
  profiles (not for public networks),
* adds the Start menu entry **QoffeeAutomat**, which opens the web interface (`http://127.0.0.1:8480/`).

!!! note "No reboot"
    A restart of the PC is not needed.

## Silent installation

For software distribution or scripts, run an **elevated** prompt:

```powershell
.\QoffeeAutomat-Setup-<version>-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=setup.log
```

| Option | Effect |
|--------|--------|
| `/LINKPORT=<port>` | QoffeeLink port (default `8481`); also used for the firewall rule. |
| `/UIPORT=<port>` | Port of the loopback web interface (default `8480`). |
| `/HUBENROLL=<string>` | Joins a central hub after the installation with the enrollment string (`qahub1.…`) that the hub shows. Joining gives the hub full control over the PC. The string contains a token: the installer does not log it, but Inno Setup records its own command line in the `/LOG` file, so protect that file or use a token with a short expiry and one use. A failed join does not fail the installation; run `qoffeeautomat hub join` afterwards. |
| `/LOG=<file>` | Writes the installer log. |

A silent installation does not show the setup token page. Read the token from `%ProgramData%\QoffeeAutomat\secrets\setup-token.txt`.

## Upgrade

Run a newer installer over the old one (also silently). It stops the service and the session helpers, replaces the files and
starts the service again. Configuration, secrets, history and logs are kept, and Home Assistant reconnects by itself.
Update the integration in HACS to the same version (see [Install the integration](install-integration.md#update)).

Without `/LINKPORT` and `/UIPORT` an upgrade leaves the service registration and the firewall rule as they are, so ports that
you changed in the settings stay. If you change the port in the settings, change the firewall rule `QoffeeAutomat QoffeeLink`
as well.

## Uninstall

*Settings, Apps*, **QoffeeAutomat**, *Uninstall*; or silently:

```powershell
& "$env:ProgramFiles\QoffeeAutomat\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

This removes the service, the firewall rule, the shortcut and the files. The interactive uninstaller asks whether to delete
the data in `%ProgramData%\QoffeeAutomat` as well (configuration, commands, scripts, secrets, history, logs); the default is
to keep it, so that a later installation finds everything as it was. The silent uninstaller keeps the data unless you add
`/PURGEDATA`.

After you uninstall for good, remove the integration in Home Assistant: see
[Remove the integration](install-integration.md#remove).

## Portable zip

The zip holds the same files as the installer. It is for trying the agent out, or for people who prefer to manage services
themselves:

```powershell
# Try it: runs in your account, in the console, until you press Ctrl+C
.\QoffeeAutomat.Agent.exe run

# Install it as a service from a folder that only administrators can change (elevated prompt)
.\QoffeeAutomat.Agent.exe service install
.\QoffeeAutomat.Agent.exe service start
```

Copy the folder to `C:\Program Files\QoffeeAutomat` first. Open the firewall yourself if Home Assistant runs on another device:

```powershell
netsh advfirewall firewall add rule name="QoffeeAutomat QoffeeLink" dir=in action=allow protocol=TCP localport=8481 profile=private,domain
```

`QoffeeAutomat.SessionHost.exe` must stay next to the agent. Started with `run` instead of as a service, the agent has no
system rights and cannot control other users' sessions; use it only to look around. `--data-dir <folder>` keeps all files in a
folder of your choice. `QoffeeAutomat.Agent.exe --help` lists all options.

## Command line of the agent

| Command | Purpose |
|---------|---------|
| `QoffeeAutomat.Agent.exe run` | Run the agent in the console (default). |
| `... version` | Print the version. |
| `... service install`, `uninstall`, `start`, `stop`, `status` | Manage the Windows service (administrator). `service --help` lists options such as `--ui-port` and `--link-port`. |
| `... reset-password` | Write a new setup token so that the password can be set again (administrator). See [First-run setup](first-run.md#forgotten-password). |
| `... emit <name> [--data <json>]` | Send a custom [event](events.md#custom-events) to Home Assistant; for scripts that the agent runs. |
