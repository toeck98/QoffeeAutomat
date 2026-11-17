# First-run setup

The web interface of the agent is protected by a password. You set it once, with a **setup token** that proves you have
administrator access to the PC.

## Set the password

1. Open the web interface: Start menu, **QoffeeAutomat**, or `http://127.0.0.1:8480/` in a browser **on the PC**.
2. The page *First-run setup* appears. Open the file
   `C:\ProgramData\QoffeeAutomat\secrets\setup-token.txt`.

    Only administrators can read it. Start Notepad (or any editor) with *Run as administrator* and open the file from there;
    a normal Explorer window will refuse.

3. Copy the text of the file into *Setup token*, enter a password twice and select **Set password and sign in**.

The file disappears when the password is set. You are signed in and land on the *Dashboard*.

!!! tip "Choose a strong password"
    The agent runs scripts with the rights of the system. Whoever knows the password can run them. Use a long, unique password
    from a password manager. Passwords are stored only as salted hashes.

## Look around

| Page | What it is for |
|------|---------------|
| **Dashboard** | State of the agent, the session helper and the Home Assistant link, live charts of the numeric metrics, recent runs and events. |
| **Commands** | The commands of this PC: enable, show in Home Assistant, run, edit, duplicate, delete. See [Commands](commands.md). |
| **Metrics** | Every value the agent can measure; switch metrics on and off, change their interval. |
| **Sensors** | Your own sensors. See [Sensors](sensors.md). |
| **Home Assistant** | Pair, see paired clients, revoke them. See [Pairing](pairing.md). |
| **History** | Every command run with its source, result and output. |
| **Logs** | The agent log. |
| **Backup** | Export and import the configuration, earlier revisions, the diagnostics bundle. See [Backup and restore](backup-restore.md). |
| **Settings** | Agent name, ports, remote UI, tray icon, log level, retention, password. |

The language of the interface (English or German) is chosen in the menu at the top.

## Settings you may want to change now

* **Agent name.** The name Home Assistant shows for this PC (default: the host name).
* **Tray icon.** A small icon in the notification area of the signed-in user shows the connection state (green: connected,
  amber: not paired, red: paused, grey: the agent is not reachable) and offers *Open UI* and *Pause commands*.
* **Ports and remote UI.** Leave them alone unless a port is taken. Changing a port or *Remote UI* needs a restart of the
  agent, which the page offers. If you change the link port, also change the firewall rule.

## Sign in again

A session ends when you sign out, after eight hours without a request, seven days after you signed in, or when the agent
restarts. Sign in again with the password on the page *Sign in*.

After too many wrong attempts the agent locks the address out: five failures from one address within 15 minutes block that
address for 15 minutes; 30 failures from all addresses together block everybody for 15 minutes. Wait, then try again.

## Change the password

*Settings*, section *Password*. You need the current password. Changing it ends all other sessions; the one you use stays signed in.

## Forgotten password

On the PC, in an **elevated** prompt:

```powershell
& "$env:ProgramFiles\QoffeeAutomat\QoffeeAutomat.Agent.exe" reset-password
```

This writes a new `setup-token.txt`. Your old password keeps working until you set a new one on the setup page (the sign-in
page then offers *A setup token is waiting. Set a new password instead.*). Setting the new password ends all sessions. Commands,
sensors and the pairing with Home Assistant are not affected.

## If the page does not open

* Use `http://127.0.0.1:8480/` (not `localhost` if that resolves to IPv6 on your PC, and not the PC's network address:
  the web interface only answers on the PC itself unless *Remote UI* is on).
* Check that the service runs: `Get-Service QoffeeAutomat`. More in [Troubleshooting](troubleshooting.md).
