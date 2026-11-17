# Quick start

From download to a working entity in Home Assistant, in about ten minutes. You need:

* a Windows 10 (1809) or Windows 11 PC, 64-bit Intel/AMD or ARM64, and administrator rights on it,
* Home Assistant 2026.3 or newer with [HACS](https://hacs.xyz/) installed,
* both devices in the same network.

!!! tip "No source code needed"
    You never build anything yourself. Everything below uses the downloads of a release and the web interfaces.

## 1. Install the agent on the PC

1. Open the [latest release](https://github.com/toeck98/QoffeeAutomat/releases/latest) and download
   `QoffeeAutomat-Setup-<version>-x64.exe` (use `-arm64` on an ARM PC).
2. Optional but recommended: [verify the download](verify-download.md).
3. Run the installer and accept the Windows elevation prompt. If SmartScreen warns about an unknown publisher, see
   [Verify your download](verify-download.md#smartscreen).
4. The last page of the installer names the **setup token file**:
   `C:\ProgramData\QoffeeAutomat\secrets\setup-token.txt`. Keep the page open or note the path.

The installer registers the service *QoffeeAutomat*, starts it, opens the port 8481 for Home Assistant on private and
domain networks and adds the Start menu entry *QoffeeAutomat*. Details: [Install on Windows](install-windows.md).

## 2. Set a password

1. Start menu, **QoffeeAutomat** (it opens `http://127.0.0.1:8480/` in your browser).
2. The page *First-run setup* asks for the **setup token**. Open `setup-token.txt` from an elevated program (for example
   Notepad, started with *Run as administrator*), copy the text and paste it.
3. Choose a password and select **Set password and sign in**.

See [First-run setup](first-run.md) if something does not work.

## 3. Install the integration in Home Assistant

1. In Home Assistant open **HACS**, menu (three dots), **Custom repositories**.
2. Add `https://github.com/toeck98/QoffeeAutomat` with the type **Integration**, then find **QoffeeAutomat** in HACS
   and **Download** it.
3. **Restart Home Assistant.**

Or use the button: [![Add the repository to HACS](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=toeck98&repository=QoffeeAutomat&category=integration)

Details: [Install the integration (HACS)](install-integration.md).

## 4. Pair

1. In the agent's web interface open **Home Assistant** and select **Pair**. A six-digit code and the certificate
   fingerprint appear.
2. In Home Assistant: **Settings, Devices & services, Add integration, QoffeeAutomat**. Select your PC (or enter its
   address and the port 8481).
3. Compare the fingerprint that Home Assistant shows with the one in the agent's window. Continue only if they are equal.
4. Enter the six-digit code within five minutes.

Details: [Pair with Home Assistant](pairing.md).

## 5. Look at your first entity

Open **Settings, Devices & services, QoffeeAutomat**, your PC. Among others you now have:

| Entity | Shows |
|--------|-------|
| *Connectivity* | Whether the PC is reachable. It also works while the PC is off (then it is off). |
| *CPU usage* | The load of the processor, in percent. |
| *Memory usage* | The share of memory in use. |
| *Power state* | `running`, `sleep`, `hibernate`, `shutdown`, `restart` or `unknown`. |

Open *CPU usage* and watch it change while you work on the PC. That is the whole chain from the agent to Home Assistant.

## What next

* Make Home Assistant **do** something: in the agent, open the page *Commands* and switch on *Enabled* and
  *In Home Assistant* for the default command *Sleep*; a button for it appears in Home Assistant. See
  [Commands](commands.md).
* Add your own values: [Sensors](sensors.md).
* Read [Security model and recommendations](security.md) before you expose commands that start programs.
