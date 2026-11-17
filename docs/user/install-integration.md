# Install the integration (HACS)

The Home Assistant side of QoffeeAutomat is the custom integration `qoffeeautomat`. You install it with
[HACS](https://hacs.xyz/), the community store for custom integrations. Home Assistant **2026.3 or newer** is required.

!!! info "Custom repository today, default store later"
    Until QoffeeAutomat is part of the HACS default list you add the repository by hand, once. This is a normal HACS
    feature and takes a minute. After that, HACS shows updates like for any other integration.

## One click: My Home Assistant

If you have [My Home Assistant](https://my.home-assistant.io/) set up, this button opens HACS on your instance with the
repository filled in:

[![Open your Home Assistant instance and add the QoffeeAutomat repository to HACS.](https://my.home-assistant.io/badges/hacs_repository.svg)](https://my.home-assistant.io/redirect/hacs_repository/?owner=toeck98&repository=QoffeeAutomat&category=integration)

Confirm the dialog, then continue with [Download](#download) below.

## By hand: add the custom repository

1. Open **HACS** in Home Assistant.
2. Select the three dots at the top right, **Custom repositories**.
3. Repository: `https://github.com/toeck98/QoffeeAutomat`. Type: **Integration**. Select **Add**.

## Download

1. In HACS search for **QoffeeAutomat** and open it.
2. Select **Download** and choose the latest version (use the same version as the agent on your PC if you can).
3. **Restart Home Assistant** (*Settings*, *System*, the power button, *Restart Home Assistant*). Home Assistant only
   finds a new custom integration after a restart.

HACS downloads the file `qoffeeautomat.zip` of the release. It contains only the integration, not the agent.

## Add the PC

After the restart:

[![Open your Home Assistant instance and start setting up the QoffeeAutomat integration.](https://my.home-assistant.io/badges/config_flow_start.svg)](https://my.home-assistant.io/redirect/config_flow_start/?domain=qoffeeautomat)

or *Settings*, *Devices & services*, **Add integration**, **QoffeeAutomat**. Then follow [Pair with Home Assistant](pairing.md).

One configuration entry belongs to one PC. Repeat the steps for every PC that runs an agent.

## Update

HACS shows an update when a newer release exists. Download it and restart Home Assistant. Agent and integration share their
version number and are released together; a version difference of the protocol is tolerated (see
[Overview](overview.md#versions)), but the newest of both is always the best-tested combination. Update the agent by running the newer
installer on the PC ([Install on Windows](install-windows.md#upgrade)).

You can also download a **pre-release** in HACS: open the integration, the three dots, **Redownload**, and choose the version.
Pre-releases are for testing.

## What the integration provides

* **Entities** for every metric, command and event of the PC (see [Overview](overview.md#what-you-see-in-home-assistant)).
* The action **`qoffeeautomat.run_command`**, see [Commands](commands.md#run-a-command-from-home-assistant).
* **Diagnostics** you can download from the device page (host names and tokens are redacted).
* **Repair issues** when the protocol versions do not match.
* **English and German** translations.

## Configuration

The integration has no options. Host and port are changed with **Reconfigure** on the entry (see
[Pairing](pairing.md#several-clients-revoking-re-pairing)). Which metrics produce entities is controlled in Home Assistant
by enabling and disabling entities; a disabled entity is not measured at all. Which commands appear, and with which control, is
configured in the agent.

## Known limitations

* The PC must be reachable from Home Assistant (same network, VPN, or routed). The agent does not connect to the internet.
* Entities of a PC that is off or asleep are *unavailable*; *Connectivity* and *Power state* stay.
* Commands that run in the user session need somebody logged in; see the sensor *Session ready*.
* Waking a sleeping PC (Wake-on-LAN) is not part of the integration in 1.0; use the Home Assistant integration
  [Wake on LAN](https://www.home-assistant.io/integrations/wake_on_lan/), see the
  [example](example-mode-start.md).
* Windows is the only supported platform in 1.0.

## Remove

1. Remove the entry: *Settings*, *Devices & services*, *QoffeeAutomat*, the three dots, **Delete**. Its device and entities
   disappear.
2. In the agent, page *Home Assistant*, **Revoke** the client, so that the old token is no longer valid.
3. Optional: in HACS open **QoffeeAutomat** and select **Remove**, then restart Home Assistant.
4. Optional: uninstall the agent from the PC ([Uninstall](install-windows.md#uninstall)).

## Install without HACS

Download `qoffeeautomat.zip` from the release, extract it into `config/custom_components/qoffeeautomat/` of Home Assistant so
that `manifest.json` lies directly in that folder, and restart Home Assistant. You then update by hand.
