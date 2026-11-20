# Enroll PCs

A PC joins the hub with an **enrollment string**. The PC dials the hub (`wss://<hub>/hub/v1/agent`), so it needs **no inbound
port**, and it works across subnets and NAT. The string is created in the hub and is used once per PC (or several times, if you
allow it).

## Create an enrollment token

1. In the hub open **Enrollment** and create a token. You can set:
    * a **name**, so that you remember what it is for,
    * an **expiry**: a token that is used for one PC should expire within hours,
    * a **maximum number of uses**,
    * **tags** that every PC gets that joins with it (for example `office`, `gaming`),
    * **approval required**: the PC waits in the state *pending* until an administrator accepts it.
2. The hub shows the **enrollment string** (`qahub1.…`) **once**. It contains the hub address, the token, the fingerprint of
   the hub certificate and the public key with which the hub signs its configuration. Copy it now; the hub stores only a hash.
   Under the string the hub shows ready-made commands for the Windows installer and for the agent.

Treat the string like a password until it has expired or is used up: anyone who has it can enroll a PC. Revoke a token in the
list if it leaks.

## Join a PC

Joining gives the hub **full control** over the PC. The agent says so before it asks you to confirm.

| How | Steps |
|-----|-------|
| Agent interface | Open **Hub** in the agent's settings, paste the string, confirm. |
| Windows installer | `QoffeeAutomat-Setup.exe /VERYSILENT /HUBENROLL="qahub1.…"` (see [Install on Windows](../install-windows.md)). |
| Command line (Windows and Linux) | `qoffeeautomat hub join qahub1.…` as administrator or root. |

The agent shows the state of the hub link: `not_joined`, `pending`, `connected`, `disconnected` or `revoked`. `qoffeeautomat hub
status` prints it.

With **approval required**, open **Enrollment** in the hub, check the name and address of the waiting PC, and **Approve** or
**Reject**. A PC that was rejected cannot join with that token.

## After joining

* The PC appears in **Fleet** with the tags of the token. Change tags later in the PC's details; the configuration that applies
  to it is recomputed at once.
* The PC receives the profiles that are assigned to it ([Profiles, assignments and policies](profiles.md)).
* The agent keeps working with the last configuration while the hub is not reachable, and reconnects by itself (waits between 1
  second and 5 minutes).
* You can rotate the credential of a PC in the hub (**Rotate credential**). The old one stays valid until the PC has confirmed
  the new one.

## Leave and remove

* **On the PC:** in the agent settings, **Leave the hub**. You choose whether the hub's commands and sensors are **converted to
  local ones** or **removed**. Your own local commands are never touched and become active again.
* **In the hub:** delete the PC. If it is online it is told to leave; a PC that is offline learns it the next time it connects
  (the hub answers *revoked*). The hub layer of the PC and its stored metrics are removed.

## Tags

A tag has 1 to 32 characters from `a-z 0-9 . _ -` and starts with a letter or a digit. A PC or a token has at most 16 tags.
