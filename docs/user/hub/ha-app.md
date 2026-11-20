# Install the hub as a Home Assistant app

Home Assistant OS and Home Assistant Supervised can run the hub as an **app** (formerly called add-on). The app is the hub
container image with an entry point for the Supervisor, and it shows the hub in the Home Assistant sidebar.

## Add the repository and install

1. In Home Assistant open **Settings, Apps** (older versions: **Add-ons**), **App store**.
2. Open the menu (three dots), **Repositories**, and add `https://github.com/toeck98/QoffeeAutomat`.
3. Find **QoffeeAutomat Hub**, **Install**, then **Start**. Turn on **Start on boot** and **Show in sidebar**.
4. Open **QoffeeAutomat** in the sidebar.

!!! note "Home Assistant Container and Core"
    These installation types have no Supervisor and therefore no apps. Use [Docker](docker.md) instead.

## Sign-in through Home Assistant (Ingress)

The sidebar panel is shown by **Ingress**: Home Assistant forwards your browser session to the hub on an internal port (8099),
so you sign in only to Home Assistant.

* The panel is visible to Home Assistant **administrators** only.
* The hub accepts connections on the Ingress port **from the Supervisor only** (`172.30.32.2`); any other address is refused.
  The Home Assistant user headers are trusted on that port and nowhere else.
* Every administrator who opens the panel gets the hub role of the option **Role of Home Assistant users** (default `admin`). The
  audit log names the Home Assistant user.

Ingress is for the browser. PCs, Home Assistant's own integration and Prometheus use the **agent port 8490**.

## Options

| Option | Default | Meaning |
|--------|---------|---------|
| `hub_name` | `QoffeeAutomat Hub` | Name of the hub, used when the hub is created. |
| `ingress_role` | `admin` | Hub role for Home Assistant administrators in the sidebar panel: `admin`, `operator` or `viewer`. |
| `certfile`, `keyfile` | empty | File names (no path) of a PEM certificate and its key in the Home Assistant `ssl` folder for port 8490. Empty: the hub's self-signed certificate. Set both or neither. |

Restart the app after you change an option.

## The agent port 8490

The app publishes **8490/tcp** on the host so that PCs and Home Assistant can reach the hub (**Settings, Apps, QoffeeAutomat Hub,
Configuration, Network**). Use the address `https://<home-assistant-host>:8490` in the enrollment of the PCs. Do not forward that
port from the internet unless you have read [Security recommendations](security.md).

Without a certificate of your own the hub uses a self-signed certificate; the enrollment string pins its fingerprint.

## Data, backup and restore

The app keeps database, keys and certificate in its `/data` folder. It is included in every Home Assistant backup that
contains the app; the app is **stopped during the backup** so that the database is consistent. Restoring a backup restores the
hub with its enrollments, so PCs stay joined. See [Backup and restore](backup.md).

## Update

The app version is the hub version. When a new release is published, Home Assistant offers the update in the App store. Make a
backup first (Home Assistant offers one in the update dialog).

## Troubleshooting

* **The app does not start:** open its **Log** tab. A wrong `certfile` or `keyfile` is reported there.
* **The sidebar panel shows an error:** check that the app runs, and reload the page. The panel works only through Home
  Assistant; open `https://<host>:8490` for the direct address.
* **PCs cannot join:** the enrollment string must contain the address that the PCs can reach (`https://<host>:8490`). Create it
  again in **Enrollment** after you changed the port.
