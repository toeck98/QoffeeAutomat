# Troubleshooting

Start with the **Dashboard** of the agent: it shows whether the agent runs, whether the session helper is *Ready*, whether Home
Assistant is paired and connected, the latest runs and events. Then the **History** (what ran and how it ended) and the
**Logs**. Most problems show up in one of these three places.

## Where to look

| What | Where |
|------|-------|
| Agent log | Page *Logs*, or `%ProgramData%\QoffeeAutomat\logs\` |
| Windows Event Log | *Event Viewer*, *Windows Logs*, *Application*, source `QoffeeAutomat` |
| Service state | `Get-Service QoffeeAutomat` |
| Version | `& "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" version` |
| Home Assistant side | *Settings*, *System*, *Logs*, filter `qoffeeautomat`; the device page, *Download diagnostics* |
| Support bundle | Page *Backup*, diagnostics bundle (see [Backup and restore](backup-restore.md#diagnostics-bundle)) |

For more detail set the **log level** to *Debug* in the settings, reproduce the problem and set it back.

To see startup errors directly, stop the service and run the agent in a console (elevated):

```powershell
Stop-Service QoffeeAutomat
& "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" run
# Ctrl+C to stop, then: Start-Service QoffeeAutomat
```

## The agent

### The web interface does not open

* Use `http://127.0.0.1:8480/` **on the PC**. The web interface does not answer on the network address unless *Remote UI* is on.
* `Get-Service QoffeeAutomat` must say `Running`. Start it with `Start-Service QoffeeAutomat`; if it stops again, read the log
  and the Event Log.
* **A port is taken** by another program: the log says so. Change the port with the settings page (needs a working UI), or
  re-register the service with other ports in an elevated prompt:

  ```powershell
  & "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" service uninstall
  & "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" service install --ui-port 8490 --link-port 8491
  & "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" service start
  ```

  Open the firewall for a changed link port and **reconfigure** the integration in Home Assistant.

### The page says a setup token is needed, and I have none

Run `QoffeeAutomat.Agent.exe reset-password` as an administrator and read the new `secrets\setup-token.txt`; see
[First-run setup](first-run.md#forgotten-password).

### The tray icon is missing

The icon belongs to the session helper of the logged-in user and appears a few seconds after logon. It can be hidden in the
settings (*Show the tray icon*), and Windows may keep it in the overflow area of the notification area. A grey icon means the
agent is not reachable.

### "Configuration file(s) are skipped because they are invalid"

The Dashboard names how many. A command or sensor file that was edited by hand or came from a newer version is ignored instead
of breaking everything. The log names the file and the problem. Fix it in the editor, restore an earlier revision (page
*Backup*) or remove the file.

## Home Assistant cannot connect

Work through the list from the top:

1. **Is the agent running?** `Get-Service QoffeeAutomat` on the PC. Is the PC awake and past the sign-in screen? The service starts
   with Windows (delayed automatic start; it can take a minute or two after the boot).
2. **Can Home Assistant reach the port?** From another PC in the same network:
   `Test-NetConnection <pc-address> -Port 8481`. `TcpTestSucceeded: False` means a firewall, a wrong address or the wrong network.
3. **Firewall and network profile.** The installer's rule *QoffeeAutomat QoffeeLink* applies to **private and domain** networks
   only. If Windows classes your network as *Public*, change it to *Private* (*Settings*, *Network & internet*, the connection,
   *Network profile*), or add a rule for the public profile if you accept the risk.
4. **Did the address change?** With a router that hands out changing addresses, use a DHCP reservation, or a host name. Fix the
   address in Home Assistant with **Reconfigure** on the integration entry. With zeroconf the address follows by itself.
5. **Did the certificate change?** Home Assistant trusts exactly the certificate it saw at pairing. If the agent's data was
   deleted (uninstall with *delete data*, a fresh install), the agent has a new certificate and identity: Home Assistant says the
   saved access is no longer accepted. **Pair again** (see [Pairing](pairing.md)); if the new agent has a new identity, remove the
   old device in Home Assistant.
6. **Was the client revoked?** Look at *Paired clients* in the agent. Pair again.
7. **Protocol versions.** Home Assistant shows a repair issue when agent and integration share no protocol version. Update the
   older of the two.
8. **A VPN, VLAN or mesh Wi-Fi** between the two may block traffic or multicast. Zeroconf discovery needs multicast; a manual
   address does not.

After you fixed the cause, Home Assistant retries by itself (after one second at first, up to a minute). *Reload* of the
integration entry makes it try at once.

## Entities

| Symptom | Reason and remedy |
|---------|-------------------|
| All entities are *unavailable*, *Connectivity* is off | The PC is off, asleep or unreachable. *Power state* says why when the agent said goodbye (`sleep`, `hibernate`, `shutdown`, `restart`); `unknown` means the connection was lost without notice. |
| Only *Session* or *Audio* entities are unavailable | They come from the session helper and need a logged-in user. The sensor *Session ready* tells. |
| An entity I expected does not exist | Metrics: the entity may be disabled by default (the active window title and process, for example; enable it in Home Assistant, and in the agent's *Metrics* page the metric must be on). Commands: *Enabled* and *In Home Assistant* must be on and the *Control* must be valid. Sensors: *Enabled* and *Show in Home Assistant*. |
| A battery entity is missing | Only computers with a battery have it. |
| A disk or network entity is missing or new | One set exists per fixed volume and network adapter; the set follows the PC. |
| A removed command still shows as an entity | Home Assistant removes it from the registry when the agent's manifest changes. If it stays, reload the integration entry. |
| A value does not change | The agent only sends changes, rounded by `precision` and `deadband`, at most once per second. Check the interval on the *Metrics* page. |

## Commands

The **History** page shows every run with its error code. The codes and their causes:

| Error code | Meaning and remedy |
|------------|--------------------|
| `no_session` | The command runs as *User session* and nobody is logged in, or the session helper has not connected yet. Wait for *Session ready*; set up automatic sign-in for unattended PCs. |
| `elevation_unavailable` | *User session, elevated* needs a logged-in user that is an administrator (and UAC on). Use *User session* or log in as an administrator. |
| `unknown_command`, `command_disabled` | The id is wrong, or *Enabled* is off. |
| `invalid_vars` | A value is missing, unknown or out of range. The message names the variable. Check the parameter definition and the template that produced the value. |
| `busy` | The command is running and its mode is *Refuse the new run*. Use *Queue* or *Stop the old run and start again* if that is what you want. |
| `cooldown` | The cooldown after the previous run has not passed. |
| `paused` | Commands were paused with the tray icon (*Resume commands*), or the *Maintenance mode* switch in Home Assistant is on. Restarting the agent clears it. |
| `condition_not_met` | The command has *Conditions* (session state or time window) and they do not hold right now, for example *Unlocked* while the screen is locked. |
| `timeout` / `timed_out` | The run exceeded *Timeout* and its process tree was killed. Raise the timeout or make the script faster. |
| `cancelled` | Another run cancelled it (*restart* mode), or you stopped it. |
| `internal` | Something unexpected, or a built-in action that this version does not have. See the log. |

Frequent causes when a script does not do what it should:

* **The script runs as a different account.** *System* is not you: it has no user profile, no mapped drives and no access to your
  desktop. Use *User session* for anything personal.
* **The script file is not found or refused.** Script files live in `%ProgramData%\QoffeeAutomat\scripts\`. The agent refuses
  to start a *System* script whose file or folder non-administrators can change and logs a security warning; fix the permissions of
  the folder (inherit from `ProgramData`, no `Users: Modify`).
* **A parameter does not arrive.** The `param()` block of the script must use the exact names and fitting types of the command's
  parameters (case does not matter to PowerShell, but spelling does).
* **A module is missing.** `Install-Module <name> -Scope AllUsers` in an elevated PowerShell. Modules installed for your user only
  are not visible to the *System* context.
* **PowerShell 7 or 5.1?** *Engine: automatic* uses `pwsh` when it is installed. A script that needs one of them must say so in
  the *Engine* field.
* **No output.** Switch on *Capture the output*. Output is capped at 64 KiB per stream.
* **Windows prompts.** A script runs with `-NonInteractive`; anything that asks a question fails. Add the necessary parameters.

### A command does not appear in Home Assistant

*Enabled* **and** *Show in Home Assistant* must be on, and the *Control* must match the parameters (a *switch* needs a yes/no
parameter, a *select* a choice, a *button* needs defaults for all required parameters). The editor's *Home Assistant preview*
tells you what entity to expect. A command with *Control: None* never gets an entity; call it with
`qoffeeautomat.run_command`.

### The action `qoffeeautomat.run_command` fails

The message names the reason; the table of error codes above explains them. Messages beginning *The agent is not connected* mean the
PC is off or unreachable; see [Home Assistant cannot connect](#home-assistant-cannot-connect).

## Sensors

Open the sensor on the page *Sensors* and use **Test**: it shows the value, or why none could be produced (the script failed, wrote
no output, the last line did not parse, the Windows object does not exist). A sensor that fails is unavailable in Home Assistant;
the others are unaffected. See [Sensors](sensors.md).

## Events

* **`booted` did not fire.** It fires when Home Assistant connects and sees a **new boot id**. A restart of the agent alone does
  not change it. If Home Assistant was off while the PC started, it fires when Home Assistant has connected.
* **A custom event does not arrive.** `emit` must run from a script that the agent started (or as an administrator). It exits with an
  error otherwise; run it in a console and read the message. See [Events](events.md#custom-events).

## Windows

* **SmartScreen warns about the installer.** See [Verify your download](verify-download.md#smartscreen).
* **Antivirus flags the agent.** The agent runs scripts and starts programs by design, which some products dislike. Verify the
  download (checksum, attestation) and add an exclusion for `C:\Program Files\QoffeeAutomat` if you trust it.
* **The PC does not wake from sleep over the network.** Wake-on-LAN settings, see [the example](example-mode-start.md#what-you-need).

## Still stuck?

1. Download the **diagnostics bundle** (page *Backup*) and, from Home Assistant, the device's **diagnostics**.
2. Search the [issues](https://github.com/toeck98/QoffeeAutomat/issues) and the
   [discussions](https://github.com/toeck98/QoffeeAutomat/discussions).
3. Open a new issue with the version of the agent and the integration, what you did, what you expected, what happened and the
   relevant log lines. Do not post tokens or passwords; for a security problem use the
   [private channel](https://github.com/toeck98/QoffeeAutomat/security/policy).
