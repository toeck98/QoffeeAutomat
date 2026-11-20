# QoffeeAutomat Hub

The hub is the central place of QoffeeAutomat: you configure commands and sensors once, publish them to groups of PCs, see
the metrics of all PCs, and let Home Assistant reach every PC through one connection.

## First start

1. Install and start the app. Open **QoffeeAutomat** in the sidebar. Home Assistant administrators are signed in by Ingress; they
   get the role set in the option *Role of Home Assistant users*.
2. Open the **Log** tab of the app. On the very first start the hub prints a one-time **setup token**. You need it only if you
   want to create a hub account for use outside Home Assistant (the direct address `https://<home-assistant-host>:8490`).
3. In the hub, create an **enrollment token** and join your PCs with it. The PCs connect to port **8490** of this host.

## Options

| Option | Meaning |
|--------|---------|
| `hub_name` | Name of the hub. Used when the hub is created. |
| `ingress_role` | Hub role of the Home Assistant administrators who use the sidebar panel: `admin` (default), `operator` or `viewer`. |
| `certfile`, `keyfile` | Names of a PEM certificate and its key in `/ssl` (the Home Assistant `ssl` folder) for port 8490. Leave both empty for the hub's own self-signed certificate. |

## Ports

* **8099** is the Ingress port. It is not published and answers only the Supervisor.
* **8490** (HTTPS) is the agent port: PCs, the Home Assistant integration and Prometheus connect here. Change the host port in
  the **Network** section of the app if 8490 is taken; PCs that join later need that port in their enrollment string.

## Backup

The data of the hub (database, keys, certificate) is in the `/data` folder of the app and is part of every Home Assistant
backup that contains the app. The app is stopped while the backup is made, so the database is consistent.

Full documentation: <https://toeck98.github.io/QoffeeAutomat/user/hub/>
