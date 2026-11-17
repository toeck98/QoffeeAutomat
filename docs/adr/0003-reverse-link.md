# ADR 0003: Reverse link for roaming devices (the agent dials Home Assistant)

* Status: accepted
* Task: T710 (PLAN.md S03 D-005, D-006, D-008, D-043, S07, S09, S10, S17; reused by T905 and S20)
* Code: `src/QoffeeAutomat.Agent/Link/Outbound/`, `custom_components/qoffeeautomat/reverse.py`
* Tests: `tests/QoffeeAutomat.Agent.Tests/Link/Outbound/` (`Category=OutboundLink`), `tests_integration/test_reverse_link.py`
* Fixtures: `protocol/fixtures/reverse/`

## Context

QoffeeLink (D-005) is dialled by Home Assistant: the integration opens a WebSocket to
`wss://<pc>:8481/link/v1`. That is right for a desktop PC on the home network, and it is what zeroconf
discovery, pairing and Wake-on-LAN assume. It does not work for a **roaming device**, a laptop that sits
behind another network's NAT (office, hotel, tethering), changes its address all the time and has no inbound
port that Home Assistant could reach. D-006 made the protocol symmetric after the handshake for exactly this
reason, and D-008 kept "agent dials Home Assistant" as the later option T710.

Requirements: R-06 (state delivery in both routes), R-11 (quality of life), R-12 (the hub link of T905 dials out
as well, so the dial-out code has to be reusable). Constraints from S09: no credential inside a frame, a
credential per client that can be revoked, no weakening of authentication.

## Options considered

| Option | Assessment |
|--------|------------|
| A. The agent uses Home Assistant's own WebSocket API (`/api/websocket`) with a long-lived access token and exchanges events and service calls. | This is the option D-008 mentions. It needs a long-lived token on every roaming laptop, and such a token is a full account of a Home Assistant user: a stolen laptop is a stolen administrator. It also carries none of the QoffeeLink messages (manifest, subscribe, run with results), so everything would be built twice. Rejected. |
| B. The agent dials a **dedicated WebSocket endpoint of the integration** with a token that only opens that endpoint, and speaks QoffeeLink v1 on it. | Chosen. The token can do nothing except be an agent; it is revocable per device; the whole integration (entities, run, events, diagnostics) stays unchanged because it receives the same frames. |
| C. MQTT. | Rejected in D-008 (needs a broker, no request/response). |
| D. A VPN or overlay (WireGuard, Tailscale) between the laptop and Home Assistant. | Works with the unchanged direct link and is the better answer for people who run one anyway. It is documented as an alternative; it is not a replacement because it needs an extra component on both ends. |
| E. The hub (S20, M9). | The hub is the multi-PC solution. The reverse link is the one-Home-Assistant solution without extra infrastructure. Both share the dial-out code (below). |

## Decision

The agent has an **outbound mode** next to the inbound listener. In outbound mode the agent opens a WebSocket
to an endpoint that the Home Assistant integration exposes. As soon as the WebSocket is open, the connection is
an ordinary QoffeeLink connection, with the agent in the protocol role of the agent and Home Assistant in the
role of the client (D-006). Both modes can run at the same time.

### Endpoint exposed by the integration

* `GET /api/qoffeeautomat/reverse/v1`, a WebSocket upgrade on the HTTP(S) port of Home Assistant itself. There
  is no entry id in the path, so a probe learns nothing about the configuration; the token selects the entry.
* WebSocket sub-protocol `qoffeelink.v1`, `permessage-deflate` offered, frames and limits exactly as in S07
  (1 MiB, ping every 30 s from both sides, 75 s without traffic ends the connection).
* The view does not use Home Assistant's own authentication (`requires_auth = False`): the agent has no Home
  Assistant account. It does its own check, below, and answers HTTP 401 without upgrading when it fails.
  A 401 is raised as `HTTPUnauthorized`, so Home Assistant's IP ban (`ip_ban_enabled`) counts failed attempts
  like it does for failed logins.
* The view is registered when the first reverse entry is set up; a Home Assistant that has no reverse entry
  exposes nothing.

### Handshake and what differs from the inbound link

```
agent                                              Home Assistant
  | -- GET /api/qoffeeautomat/reverse/v1 ----------> |  Authorization: Bearer <reverse token>
  |                                                  |  Sec-WebSocket-Protocol: qoffeelink.v1
  | <- 101 Switching Protocols ---------------------- |  401 if the token is unknown (no upgrade)
  | -- hello ----------------------------------------> |  the agent sends hello first, no matter who dialled (S07)
  | <- client_hello ---------------------------------  |
  |                  ... identical to QoffeeLink v1 ... |
```

1. The agent still sends `hello` first and Home Assistant answers with `client_hello` (S07, S20). The
   frames are the ones in `protocol/fixtures/`; `protocol/fixtures/reverse/` holds the transcript of the
   reverse handshake and the two ways it can fail.
2. Home Assistant checks that `hello.agent.id` is the id of the entry that owns the token. A different id
   (a token copied to another PC) ends the connection with close code 4401.
3. **One connection per entry; the newest wins.** After a change of network the old TCP connection of a
   laptop can stay half open for minutes. A new connection with a valid token replaces the old one, which
   is closed with code 1000.
4. The agent reconnects by itself, with exponential backoff and jitter from 1 s to 5 minutes (the same
   numbers as the hub link in S20). The backoff restarts at one second when the agent starts, when the
   network address of the PC changes (`NetworkChange`) and after a successful handshake. The roaming
   laptop therefore appears within seconds of joining a network without anybody triggering it.
5. Home Assistant cannot dial. `trigger_connect()` and the fast-retry mode of the Wake-on-LAN button
   change nothing on the transport of a reverse entry; the "a wake is in progress" bookkeeping still works,
   so `qoffeeautomat.run_command` with `wake: true` (T712) does not send duplicate magic packets.
   Wake-on-LAN itself only helps when Home Assistant and the laptop share a broadcast domain.
6. `booted`, `bye`, the connectivity sensor and the power state behave as on the inbound link, because
   they only depend on frames and on the connection.

### Authentication

* **The reverse token** has 32 random bytes, written `qalr_` plus base64url. It is created by Home Assistant
  when the user adds a reverse entry (or rotates the token of an entry) and is shown **once**, inside the
  enrollment string. It opens only this endpoint and only for this entry.
* Home Assistant keeps the SHA-256 hash (`reverse_token_hash`, lower-case hex) in the config entry data and
  compares in constant time (`hmac.compare_digest`). The token is not stored in clear text and never logged.
  The diagnostics redact the hash.
* The agent keeps the token in the secret store (S09 item 6, DPAPI on Windows, key file on Linux) under the
  name `reverse_link.token`. It is sent only in the `Authorization` header of the upgrade request, never in a
  frame (S07). The settings document holds the URL and the fingerprint, not the token.
* **Revocation:** removing the entry or rotating its token makes the old token unknown. A connected agent
  is closed with 4401; an agent that dials with the old token gets HTTP 401. In both cases the agent enters the
  state `rejected`, writes one warning and **stops dialling** until a new enrollment (like the revoked hub
  credential in S20). Re-enrolling replaces the token.
* **Enrollment string:** `qalr1.<base64url(JSON {"url","token","fp"})>`. `url` is the WebSocket URL of the
  endpoint, `token` the reverse token, `fp` (optional) the SHA-256 fingerprint of Home Assistant's
  certificate. Home Assistant shows it in the config flow; the agent takes it with
  `ReverseLinkManager.JoinAsync` (the CLI verb and the UI form that call it come with the hub join of T905,
  which uses the same pattern). The string contains a credential: treat it like a password.
* **TLS.** The agent requires `wss://`. `ws://` is accepted only for a loopback host (development and tests),
  because the token would travel in the clear otherwise. Without `fp` the certificate is validated against the
  operating system's trust store, which is right for a public certificate (Let's Encrypt, Nabu Casa) and
  rotates without any action. With `fp` the agent accepts exactly that certificate and nothing else, which is
  for self-signed certificates and private CAs. Pinning a public certificate is possible but breaks at every
  renewal, so Home Assistant offers the fingerprint only when the user asks for it.

### The URL that Home Assistant exposes, reverse proxies and remote access

* The config flow proposes `get_url(hass, prefer_external=True)` (the external URL, which includes Nabu Casa
  Remote UI) and shows it in an editable field; `https` becomes `wss`, a plain `http` URL is refused with an
  explanation because of the TLS rule above. The user can enter any other URL that reaches Home Assistant,
  for example one behind a tunnel.
* **Reverse proxy** (nginx, Caddy, Traefik, Apache): if the Home Assistant frontend works through it, the
  endpoint works, because the frontend needs WebSocket upgrades too. The same `Upgrade` and `Connection`
  headers apply to this path. The 30 s ping keeps the connection below the idle timeouts of common proxies
  (nginx `proxy_read_timeout` 60 s). Compression is negotiated end to end and is optional: when a proxy removes
  it the frames are simply larger. Home Assistant needs `use_x_forwarded_for` and `trusted_proxies` anyway, and
  they decide which address the IP ban sees.
* **TLS termination at the proxy:** the agent validates the certificate of the proxy, so `fp` (if used) is the
  fingerprint of the proxy's certificate, not of Home Assistant's.
* **Nabu Casa Remote UI and tunnels (Cloudflare Tunnel, Tailscale Funnel):** they forward HTTPS and WebSocket
  traffic to Home Assistant and need no change. A tunnel with an interactive login in front (Cloudflare
  Access, an identity-aware proxy) blocks the agent, which cannot log in; the path
  `/api/qoffeeautomat/reverse/v1` has to be excluded or given a service credential at the proxy.
* **Load and abuse.** The endpoint is reachable from the internet when Home Assistant is. Unauthenticated
  requests cost a hash comparison and answer 401 before anything is allocated. The token has 256 bits, so
  guessing is not a realistic attack; the ban counter covers the noise.

### Reusable dial-out code (T905)

`src/QoffeeAutomat.Agent/Link/Outbound/` separates what is the same for every outbound link from what is
specific to Home Assistant:

| Part | Generic or specific |
|------|---------------------|
| `OutboundEndpoint` (URL, bearer credential, optional pinned fingerprint) | generic |
| `IOutboundWebSocketConnector` and `ClientWebSocketConnector` (TLS rules, pin, sub-protocol, bearer header, compression, keep-alive) | generic |
| `OutboundBackoff` (1 s to 5 min, jitter, reset) | generic |
| `OutboundConnector` (the loop: resolve endpoint, dial, hand the socket to a session handler, classify the failure, report the state) | generic |
| `ReverseLinkEnrollment`, `ReverseLinkManager`, `ReverseLinkService` | specific (URL rules, `qalr1.` string, token in the secret store, `hello`/`client_hello` through `LinkServer.ServeOutboundAsync`) |

The hub link of T905 supplies another endpoint resolver (hub URL, agent credential, pinned hub certificate) and
another session handler (a client of kind `hub` with the hub features) and reuses everything above. The
connector reports a rejected credential (HTTP 401, close code 4401) through its state and stops dialling, which
is also what the revoked-credential rule of S20 needs.

## Consequences

* A roaming laptop is a normal device in Home Assistant, with the same entities, actions and events, as soon as
  it has connected once. A reverse entry has no host, port, fingerprint or link token; its device and entities
  exist after the first connection and stay unavailable while the laptop is away.
* The integration gets a second kind of config entry (`reverse: true`). Zeroconf rediscovery, address
  reconfiguration and re-pairing do not apply to it; the options flow can rotate its token.
* The agent has to be told the enrollment string. Until the CLI verb and the UI form exist, the join is done
  through `ReverseLinkManager` (used by the tests and by the T905 verbs); the ADR fixes the format so they can
  be added without a protocol change.
* Security: one more credential type. It is narrower than any alternative (one endpoint, one entry), stored
  hashed on the Home Assistant side and protected on the agent side, transported only in the upgrade header
  over TLS, and revocable.
* Not solved here: reaching a roaming laptop when Home Assistant itself is not reachable from the internet
  (use a VPN, option D), and several Home Assistant instances for one laptop (one reverse link per agent).
