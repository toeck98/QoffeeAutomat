# Pair with Home Assistant

Pairing gives one Home Assistant a long-lived **link token**. You do not copy any token yourself: you compare a fingerprint
and type a six-digit code.

**Before you start**

* The agent is [installed](install-windows.md) and you have [set the password](first-run.md).
* The integration is [installed in Home Assistant](install-integration.md) and Home Assistant was restarted.
* Home Assistant can reach the PC on TCP port 8481 (same network, firewall rule from the installer).

## Step by step

1. **In the agent:** open the page **Home Assistant**, section *Pair with Home Assistant*, and select **Pair**. A window shows
    * the **pairing code** (six digits) and how long it is valid (five minutes),
    * the **certificate fingerprint** (SHA-256) of the agent.
2. **In Home Assistant:** *Settings*, *Devices & services*, **Add integration**, **QoffeeAutomat**.

    * If *Announce this PC on the local network* is on (the default), your PC may already be listed as *discovered*. Select it.
    * Otherwise enter the **host** (name or IP address of the PC) and the **port** (default `8481`).

3. **Compare the fingerprint.** Home Assistant shows *Found <name> at <host>:<port>* and the fingerprint. It must be the same
    as in the window of the agent. If it differs, **stop**: you are not talking to your agent.
4. Enter the **pairing code** within five minutes.
5. Home Assistant creates the device with its entities. In the agent, *Paired clients* now lists *Home Assistant*, and the
   dashboard shows an open connection.

A code can be used once and is blocked after five wrong attempts or when it expires; select **New code** in the agent to
get another.

## What pairing stores

| Where | What |
|-------|------|
| Home Assistant (config entry) | Host, port, the link token and the certificate fingerprint of the agent. Home Assistant trusts only this certificate for this agent. |
| Agent | A SHA-256 **hash** of the token, the name and kind of the client and when it was last seen. The token itself is not stored. |

The agent creates a self-signed certificate on first start (valid for the host name and `<hostname>.local`). Browsers are not
involved: Home Assistant pins the fingerprint instead of asking a certificate authority.

## Several clients, revoking, re-pairing

* More than one Home Assistant (or other client) can be paired with the same agent; each gets its own token.
* **Revoke** a client on the page *Home Assistant*: its connections close at once and it has to pair again.
* If Home Assistant says the saved access is no longer accepted (the token was revoked or the agent's data was removed), it
  starts a **re-authentication**: pair again with a new code. The entities and their history are kept.
* If the PC gets a new address, use **Reconfigure** on the integration entry (*Settings*, *Devices & services*,
  *QoffeeAutomat*, the three dots) and enter the new host and port. It must be the same agent. With zeroconf on, Home Assistant
  also updates the address by itself.

## Problems

| Message in Home Assistant | Cause and remedy |
|---------------------------|------------------|
| *Cannot connect to the agent* | Wrong address or port, the service is not running, the firewall blocks port 8481 (public network profile?), or the certificate changed. See [Troubleshooting](troubleshooting.md#home-assistant-cannot-connect). |
| *The pairing code is wrong* | Typo; or another code was shown meanwhile. |
| *The pairing code has expired or was blocked* | Select *New code* in the agent. |
| *No common protocol version* | Agent and integration are too far apart. Update the older one. |
| The PC is not found automatically | Zeroconf needs multicast in your network (not across VLANs, not through some Wi-Fi guest networks) and a running agent with the announcement switched on. Enter the address by hand. |
