# The QoffeeAutomat hub

The hub is an **optional** central server for QoffeeAutomat. Every PC (agent) works without it. With a hub you can:

* **join many PCs** with one enrollment string, without opening a port on any PC (the PCs dial the hub),
* **write commands, sensors and scripts once** and publish them to all PCs, to groups of PCs (tags) or to single PCs,
* **decide per PC or group** whether local changes are allowed and whether the PC still accepts direct pairing,
* **keep a history of metrics** of all PCs and export the current values to Prometheus,
* **connect Home Assistant once** to the hub and get every PC as a device,
* open the **interface of any PC** (dashboard, commands, sensors, logs) through the hub.

The hub is made for up to 50 PCs with about 30 collected metrics each on small hardware such as the Home Assistant host. See
[Sizing the hub for metric history](../hub-sizing.md).

!!! warning "The hub controls your PCs"
    A PC that joins a hub gives the hub **full control** over it: the hub can publish commands that run with the rights of the
    agent, which is `SYSTEM` on Windows and `root` for system commands on Linux. Protect the hub like a password manager. Read
    [Security recommendations](security.md) before you expose it.

## Install the hub

| | As a container | As a Home Assistant app |
|---|---|---|
| For | Any Linux host with Docker, a NAS, a server | Home Assistant OS and Supervised |
| Image | `ghcr.io/toeck98/qoffeeautomat-hub` | `ghcr.io/toeck98/qoffeeautomat-hub-app` (built on the hub image) |
| Architectures | `amd64`, `arm64` | `amd64`, `aarch64` |
| Sign-in | Local hub accounts | Home Assistant sidebar panel (Ingress), local accounts for direct access |
| Data | the volume mounted at `/data` | the app's `/data` folder, part of the Home Assistant backup |
| Guide | [Install with Docker](docker.md) | [Install as a Home Assistant app](ha-app.md) |

Both ways run the same program. There is no Windows package of the hub.

## The way to a working fleet

1. [Install the hub](docker.md) and create the first administrator.
2. [Create an enrollment token and join the PCs](enrollment.md).
3. [Publish profiles](profiles.md): commands, sensors, variables and policies for the PCs.
4. [Connect Home Assistant](#home-assistant-and-the-hub) to the hub, or keep the direct links.
5. Look at [metrics and Prometheus](metrics.md), and set up [backups](backup.md).

## Home Assistant and the hub

Home Assistant can reach a PC in **two ways**, and both can be used in the same Home Assistant:

* **Direct link** (the default): Home Assistant pairs with each PC. It keeps working when the hub is down.
* **Hub route**: Home Assistant pairs **once with the hub** and gets all PCs, each as its own device. The PCs need no inbound port.

**Every PC uses exactly one of the two**, and the entity ids are the same on both, so you can move a PC from one route to the
other without losing history or customisations. [Connect Home Assistant to a hub](../hub-route.md) describes the steps. In the
hub, the page **Home Assistant** creates the pairing code, and you can limit the code to PCs with certain tags.

## What is where in the hub

| Page | Purpose |
|------|---------|
| Fleet | All PCs, their state, tags and roll-out status. Open a PC to use its interface. |
| Enrollment | Enrollment tokens and PCs waiting for approval. |
| Library, Profiles, Assignments, Roll-out | The configuration you publish ([Profiles, assignments and policies](profiles.md)). |
| Actions | Fleet actions: run a command or update the agents on many PCs at once. |
| Metrics | Charts and history of the stored metrics. |
| Home Assistant | Pairing codes and the fleet tokens of Home Assistant. |
| Users, API tokens | Accounts and tokens, each with a role (`admin`, `operator`, `viewer`). |
| Audit log | Every change, publish and command run, with user and time. |
| Hub settings | Name and options of the hub. |

## Roles

| Role | May |
|------|-----|
| `viewer` | Look at everything, read-only. |
| `operator` | Additionally run commands and publish profiles that contain no system scripts. |
| `admin` | Everything: users, tokens, enrollment, the library, assignments, and the publish of profiles with system scripts. |
