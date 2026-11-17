# Roaming devices: the PC connects to Home Assistant

Normally Home Assistant opens the connection to the agent ([Pair with Home Assistant](pairing.md)). That needs a PC that Home
Assistant can reach: same network, a fixed address, an open port. A **laptop that roams** (office, hotel, tethering) has none of
that. For such a device the direction is reversed: the **agent dials Home Assistant** and, once the connection is open, both speak
the same QoffeeLink protocol as always. Entities, actions, events and diagnostics work as for any other PC.

Use the reverse link when

* the PC is behind another network's NAT, or changes its address all the time, and
* Home Assistant is reachable from the internet over **HTTPS** (your own address, a reverse proxy, Nabu Casa Remote UI or a
  tunnel).

If you already run a VPN between the PC and Home Assistant, the normal link over the VPN is simpler. The design and the
alternatives that were rejected are in the architecture decision record 0003 (`docs/adr/0003-reverse-link.md` in the repository).

!!! warning "The agent side is not wired to a button yet"
    The Home Assistant side is complete. The agent can join and leave the reverse link (`ReverseLinkManager`), and it redials by
    itself, but the form in the web interface and the command-line verb that take the enrollment string come with the hub link
    (T905), which uses the same pattern. Until then the join is done through the agent's management code, not through a page.

## Add a roaming PC in Home Assistant

1. **Settings → Devices & services → Add integration → QoffeeAutomat**, choose **The PC connects to Home Assistant**.
2. Enter the address that the PC can reach Home Assistant at. The external address is proposed. It has to start with `https://`;
   a plain `http://` address is refused because the token would travel unencrypted. (`http://` works for `localhost` and other
   loopback addresses, for development.)
3. Optional: the **fingerprint** (SHA-256) of the certificate that the PC should accept. Leave it empty if your address has a
   public certificate (Let's Encrypt, Nabu Casa): the PC then checks it against the operating system's trust store, and renewals
   need no action. Enter it only for a self-signed certificate or a private CA. Behind a proxy that ends TLS it is the fingerprint
   of the **proxy's** certificate.
4. Home Assistant shows the **enrollment string** (`qalr1.…`) and waits. It contains a secret: it is shown only now, so handle it
   like a password.
5. Give the string to the agent on the PC. When the PC connects, this page continues by itself and creates the entry. The device
   and its entities appear after the first connection and are unavailable while the PC is away.

If no PC uses the string within 15 minutes, the setup ends; start again when the agent is ready.

## What is different from a normal entry

| | Normal entry | Reverse entry |
|-|--------------|---------------|
| Who opens the connection | Home Assistant | The agent |
| Stored in the entry | host, port, link token, fingerprint | only the SHA-256 hash of the reverse token |
| After a network change | Home Assistant reconnects | the agent redials (backoff from 1 second to 5 minutes, restarted when the address changes) |
| Wake-on-LAN | works | only helps if Home Assistant and the PC share a broadcast domain |
| `wake` and fast retry | send packets and retry every second | send packets; the agent decides when to dial, Home Assistant cannot |
| Discovery, re-pairing, address change | yes | no |

One connection per PC is accepted; a newer connection replaces an older one. This matters after a change of network, when the
old connection of a laptop can stay half open for minutes.

## Replace or revoke the token

* **Replace:** open the entry and choose **Reconfigure**. Home Assistant creates a new token and shows a new enrollment string. The
  old token stops working when you confirm; a PC that still uses it is rejected until it is enrolled with the new string.
* **Revoke:** remove the entry. The token is unknown from then on. The agent that still dials with it gets HTTP 401, writes one
  warning and **stops dialling** until it is enrolled again.

While Home Assistant is starting or reloading the entry, the agent gets HTTP 503 and tries again; only an unknown token is
rejected for good.

## Reverse proxies, remote access and security

* The endpoint is `wss://<your address>/api/qoffeeautomat/reverse/v1`. If the Home Assistant frontend works through your proxy,
  this works too, because the frontend needs WebSocket upgrades as well. Keep the idle timeout of the proxy above about 60 seconds;
  the connection sends a ping every 30 seconds.
* A tunnel with an interactive login in front of Home Assistant (Cloudflare Access, an identity-aware proxy) blocks the agent, which
  cannot log in. Exclude `/api/qoffeeautomat/reverse/v1` from the login or give the agent a service credential there.
* The reverse token opens **only** this endpoint and only for this PC. It is not a Home Assistant access token. Home Assistant
  keeps only its hash; the agent keeps it in its secret store (DPAPI on Windows) and sends it only in the header of the upgrade
  request, never in a frame.
* A request with an unknown token costs one hash comparison and is answered with HTTP 401 before anything is allocated. Failed
  attempts count towards the IP ban of Home Assistant when you have it enabled (`ip_ban_enabled`).
* A token that is copied to another PC does not work: the agent id in the first message has to be the one of the entry, otherwise
  the connection is closed.

See also [Security model and recommendations](security.md) and the action `qoffeeautomat.run_command` with `wake`, `wait_for` and
`ready_timeout` in [Example: start the PC in a mode](example-mode-start.md).
