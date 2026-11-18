# ADR 0001: Zeroconf advertisement (mDNS and DNS-SD) from .NET

* Status: accepted
* Task: T216 (PLAN.md S03 D-016, S07, S19)
* Code: `src/QoffeeAutomat.Agent/Discovery/`
* Tests: `tests/QoffeeAutomat.Agent.Tests/Discovery/` (`Category=Discovery`)

## Context

The Home Assistant integration finds an agent through the DNS-SD service type
`_qoffeeautomat._tcp.local.` and uses the announcement that the agent sends after a PC start or
a resume to reconnect at once (PLAN.md S07, requirement R-11). The agent therefore has to
**advertise** a service, not only browse for one: PTR, SRV, TXT and A records, an unsolicited
announcement, answers to queries and a goodbye (records with TTL 0) when it stops.

Requirements that shape the choice:

* Windows first, Linux later (S17), on .NET 10, with one code path where possible.
* The agent is a service without a desktop. It must not require Bonjour, iTunes or any other
  software to be installed, and it must not need elevated rights beyond what the service already has.
* Few and well-maintained dependencies (S19 lists "mDNS libraries for .NET are thinly maintained"
  as a risk and asks T216 to start with this evaluation). A manual host entry in Home Assistant
  always works, so a failure of discovery must never affect the agent.
* The feature can be switched off in the settings, and then **no multicast socket may be opened**
  (T216 acceptance).
* Tests must not touch the network (CI runners, and developers on VPNs).

## Options considered

The status of third-party packages below is what was known when this decision was made. It was not
re-checked against the package feeds in the pull request that introduced this file; the point of
the decision is the shape of the packages (browse only, advertise, native), which does not change
quickly. Re-check it before reopening the decision.

| Option | What it is | Assessment |
|--------|------------|------------|
| A. A managed mDNS library with advertising (for example `Makaretu.Dns.Multicast` and its forks) | A full DNS, multicast and service-discovery stack in managed code, MIT licensed. | It can advertise, but it was developed by one author and releases became rare; forks exist and none of them is clearly the maintained successor. It brings a large surface (a complete DNS stack, name-server, caching, IPv6, probing) of which the agent needs a tenth, and it would be the one dependency that nobody upstream looks after. A transitive dependency chain of its own also has to be pinned and reviewed (AGENTS.md, workflow step 9). |
| B. Browse-only libraries (`Zeroconf`, `Tmds.MDns`) | Clients that discover services. | They cannot announce a service, so they do not solve the problem. Useful only for a future "find other agents" feature (the hub, S20), which is a different decision. |
| C. The native Windows DNS-SD API (`DnsServiceRegister` in `dnsapi.dll`, Windows 10 1703 and later) through P/Invoke | The operating system's responder registers and announces the service. | No packet code, and it shares port 5353 politely. But it exists only on Windows, needs an unmanaged callback and `DNS_SERVICE_INSTANCE` marshalling that is easy to get wrong, registers per host rather than per interface, gives no control over when the announcement is sent (the resume announcement is the point of T216), and cannot be tested without a real Windows machine. It would be a second implementation next to Avahi on Linux. |
| D. Avahi on Linux (D-Bus `org.freedesktop.Avahi.Server`) | The daemon that most Linux distributions already run. | The right thing on Linux later (M8). It needs a D-Bus client dependency and a running `avahi-daemon`, so it is an addition for the Linux platform, not a replacement for a Windows solution. Kept as an option behind the same `IServiceAdvertiser` interface. |
| E. A small responder written for this project | RFC 6762 and 6763 for exactly one service: announce, answer queries for the service type, the instance and the host name, send a goodbye. About 1,500 lines in `Discovery/` including the service that decides when to announce, no package. | Chosen. |

## Decision

Implement the advertisement in the agent (option E), behind the existing platform interface
`IServiceAdvertiser`, with these parts:

* `DnsWire` encodes and decodes the few record types that are needed (PTR, SRV, TXT, A, and the
  questions for them), reading compressed names in queries and writing uncompressed names in responses. It has no dependency.
* `IMdnsTransport` and `IMdnsTransportFactory` are the **only** place that creates sockets
  (`UdpMdnsTransportFactory`: one UDP socket per usable IPv4 interface, port 5353 with address
  reuse so that Avahi or the Windows DNS client can share it, joined to 224.0.0.251). Tests replace the
  factory with a fake transport, so no test sends a packet.
* `MdnsServiceAdvertiser` implements `IServiceAdvertiser`. Starting an advertisement opens the
  sockets, sends an unsolicited announcement on every interface, repeats it after one second and
  then answers queries; starting it again replaces the advertisement and opens the sockets afresh,
  which is how a changed network is picked up; stopping sends the goodbye and closes the sockets.
  Nothing is opened before the first start.
* `ZeroconfService` decides **when**: as soon as the host reports that the listeners accept
  connections (so the HTTPS link listener is up), after a resume, after the network changed (debounced,
  because one change raises several events) and after a rename; it withdraws on shutdown, before the
  link clients are told `bye`. With the setting `zeroconf: false` the advertiser is never started, so no
  socket is opened and no network watch is registered.
* The announcement carries service type `_qoffeeautomat._tcp.local.`, the port that the HTTPS
  listener really got and the TXT records `id` (agent id), `name`, `v` (agent version) and `pv`
  (protocol range `min-max`, currently `1-1`).
* The simulated platform keeps a recording advertiser (`SimulatedServiceAdvertiser`); the test host
  uses it, and only the executable switches to the real responder (`UseMdnsAdvertiser` in `Program.cs`).

### Scope and known limits

* **IPv4 only.** Home Assistant resolves the agent through the A record and connects to the port of the
  SRV record; every network that has a Home Assistant host also has IPv4, and link-local IPv6 needs a
  scope id per interface that makes the announcement and the resolution harder for no gain. If a
  network without IPv4 ever matters, the transport grows an IPv6 group (ff02::fb) without changing the
  advertiser.
* **No probing and no conflict resolution.** Two agents with the same name on one network are not told
  apart. The instance name is the agent name, which the owner chooses, and the TXT record `id` is what the
  integration identifies an agent by.
* **No random delay before answers** (RFC 6762 section 6): the record set is unique to this host, and
  known-answer suppression is implemented.
* **Legacy unicast queries** (resolvers that are not mDNS responders) are answered directly with a short TTL.
* The service is announced per interface with that interface's own addresses.

## Consequences

* No new package, and no native code. The behaviour that matters for Home Assistant (announce at
  start and after a resume, withdraw at stop) is under our control and covered by unit tests with a
  fake transport and a fake network-change source.
* We own a small piece of protocol code. The risk is limited by the narrow scope above, by the tests
  that decode the bytes that would be sent (`MdnsServiceAdvertiserTests`), and by the fact that the
  `IServiceAdvertiser` seam allows a native implementation per platform.
* Interoperability with real browsers of the network cannot be proven in CI.

## Verification

Automated (`dotnet test tests/QoffeeAutomat.Agent.Tests --filter Category=Discovery`):

* The announcement contains the PTR, SRV, TXT and A records with the right names, the port of the
  HTTPS listener and the TXT keys `id`, `name`, `v`, `pv`; it is repeated once; queries for the service
  type, the service type enumeration, the instance and the host name are answered; names compare without
  regard to case; known answers are suppressed; legacy unicast resolvers get a direct answer.
* The advertisement starts when the host accepts connections, again after a resume (not after a sleep),
  once after a burst of network changes, and with the new name after a rename; stopping withdraws it with a
  goodbye.
* With the setting switched off, the factory is never asked to open a socket, no network watch is
  registered, and switching it on opens exactly one transport.

Manual (task has `human_verify: true`, to be done by the owner on a real network):

1. Start the agent on Windows. On another computer of the same network run `avahi-browse -rt _qoffeeautomat._tcp`
   (Linux) or `dns-sd -L "<agent name>" _qoffeeautomat._tcp local` (macOS or Windows with Bonjour).
   Expect the instance, the port of the HTTPS listener and the TXT records `id`, `name`, `v`, `pv`.
2. Stop the agent: the entry disappears at once (goodbye).
3. Put the PC to sleep and wake it: the entry is announced again within a few seconds.
4. Set `zeroconf` to false in the settings: the entry disappears, and the agent has no socket on UDP 5353
   (`netstat -ano -p udp` or `ss -lunp`).
5. Check that Windows' own name resolution (and, if installed, Bonjour) keeps working while the agent runs,
   because the port is shared.

## When to revisit

* A maintained managed library that advertises and has a small surface appears, or one of the existing ones
  becomes clearly maintained: replace `MdnsServiceAdvertiser` behind `IServiceAdvertiser`.
* Windows machines in the field turn out to be unreachable through the shared port (security software that
  blocks multicast sockets of services): add the native `DnsServiceRegister` implementation for Windows as
  another `IServiceAdvertiser`.
* The Linux platform (M8): use the Avahi implementation if `avahi-daemon` runs, otherwise this responder.
  (T802 chose the responder alone, see below.)

## Linux (T802)

The Linux agent advertises with the responder of this ADR (option E), not with Avahi. The reasons:

* Avahi's `org.freedesktop.Avahi.Server` is a D-Bus API, and the agent has no D-Bus client (D-048: the systemd integration is
  plain managed code, so the published files run on every distribution without native or extra packages). A back end that
  works only where `avahi-daemon` runs would need a second path anyway for the many servers and containers that do not run it.
* The responder shares UDP port 5353 politely. It sets `SO_REUSEADDR`, as `avahi-daemon` and `systemd-resolved` (with
  `MulticastDNS=yes`) do, so all of them receive the multicast packets, and each answers for its own records. Nothing has to
  be stopped or configured. The announcement is per interface, IPv4 only, with the limits listed above.
* It runs inside the service, so the announcement follows the same events as on Windows: the listener is up (`READY=1` is sent
  at the same time), a resume (the logind signal `PrepareForSleep(false)` reaches `ZeroconfService` through
  `IPowerEvents`), a network change and a rename; and the goodbye is sent when the agent stops, before `bye` closes the links.

The `IServiceAdvertiser` seam is unchanged, so an Avahi back end can be added for desktops where a second responder on port 5353
is unwanted. Manual check on Linux: `avahi-browse -rt _qoffeeautomat._tcp` from another computer while the service runs,
and `ss -lunp | grep 5353` shows the agent next to `avahi-daemon`.
