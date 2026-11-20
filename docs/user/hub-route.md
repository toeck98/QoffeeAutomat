# Connect Home Assistant to a hub

The default is to pair Home Assistant with every PC directly ([Pair with Home Assistant](pairing.md)). If you run a QoffeeAutomat
hub, there is a second way: Home Assistant connects **once to the hub** and gets all PCs of the hub, each as its own device. The
PCs need no inbound port and are not paired one by one. Both ways can be used in the same Home Assistant, but **every PC uses
exactly one of them**.

| | Direct link (default) | Hub route |
|---|---|---|
| Pairing | once per PC, with a code from the agent | once per hub, with a code from the hub |
| Works while the hub is down | yes | no, the PCs show as unavailable |
| Needs an open port on the PC | yes (or the [reverse link](reverse-link.md)) | no, the PCs dial the hub |
| Entities, actions, events | the same | the same |

## Add the hub

1. In the hub, create a pairing code for Home Assistant (hub settings, Home Assistant pairing). Choose the scope: all PCs, or the
   PCs that carry certain tags. The code is valid for five minutes and works once.
2. In Home Assistant open **Settings, Devices & services, Add integration, QoffeeAutomat** and choose **Home Assistant connects to a
   QoffeeAutomat hub**. Enter the host and the port of the hub (default 8490).
3. Compare the certificate fingerprint that Home Assistant shows with the one the hub shows, then enter the pairing code.

Home Assistant creates **one config entry per hub**. It shows the hub as a device and every PC of the hub as a device that is
linked to it (`via_device`). PCs that join the hub later appear by themselves, and a PC that is removed from the hub (or leaves
the scope of the token) disappears with its entities. Reauthentication (the token was revoked in the hub) and reconfiguration
(another address for the same hub) work as for a PC.

The entity ids are the same on both routes (`<agent id>_<metric or command id>` is the unique id), so automations, dashboards and
the history do not care which route a PC uses.

## One route per PC

A PC that already has its own entry (direct link) is **skipped** by the hub entry, so no entity exists twice. Home Assistant
creates a repair issue *"… is also available through the hub …"*. You have two choices:

* **Ignore the issue** to stay on the direct link. This is the default, and it is the right choice if the PC should keep working
  while the hub is down.
* **Fix the issue** to move the PC to the hub route. The direct entry is removed, the PC appears under the hub entry, and the
  **entity ids, your renamed entities, areas, disabled entities and the history are kept**.

The way back works too: add the PC directly (**Home Assistant connects to the PC**, enter its pairing code). Because the hub serves
the PC, Home Assistant asks you to confirm the move. Afterwards the PC belongs to the new entry, again with the same ids and
customisations, and the hub entry skips it.

If the hub's policy for a PC is `direct_link: deny`, the PC refuses pairing and direct connections. A direct entry that exists
already then shows the repair issue *"The hub does not allow a direct link to …"*, which offers to move the PC to the hub route
(this needs the hub entry first). Trying to pair such a PC directly shows an error that points to the hub.

## What works the same, and what does not

* **`qoffeeautomat.run_command`**, the command entities (buttons, switches, numbers, …) and **notifications** run through the hub.
  The hub forwards the call with the Home Assistant user, so it appears in the audit log of the hub and of the agent. With the PC
  off, the call fails with the message that the agent is not connected.
* **Events** (`qoffeeautomat_event`, the event entity) arrive as on a direct link.
* **Wake-on-LAN**: the MAC addresses come from the hub, and the button sends the magic packets from Home Assistant (the broadcast
  address is the option of the hub entry). There is no fast retry: the PC dials the hub by itself as soon as it is up.
* **`booted`**: the hub does not know the boot id of a PC. On the hub route Home Assistant fires `booted` when a PC comes online
  after it said `shutdown` or `restart`. A boot that follows a power loss or a sleep is not reported as a start.
* **Metrics** are subscribed per PC, and only the metrics of enabled entities (or watched by other hub clients) are sent.
* The hub and the PC both have to be reachable for a PC to be available. When the connection to the hub is lost, all PCs of the hub
  are unavailable until it is back.

## Security

The fleet token that pairing creates can run commands on every PC in its scope. Treat it like a password: the hub shows it once,
Home Assistant stores it in the config entry, and diagnostics never contain it. Use the scope to limit it to the PCs that Home
Assistant needs, and revoke it in the hub if Home Assistant is retired. The certificate of the hub is pinned like the one of an
agent. If the hub gets a new certificate, Home Assistant refuses the connection and the entry waits ("retrying"). Today the only
way to accept the new certificate is to remove the hub entry and add the hub again, which also removes the devices and
customisations of the hub's PCs. Give the hub a certificate that stays the same for a long time. See
[Security model and recommendations](security.md).
