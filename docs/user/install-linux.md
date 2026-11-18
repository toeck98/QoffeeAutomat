# Install on Linux

## Requirements

* A 64-bit Linux with systemd: Intel/AMD (`x64`, Debian package architecture `amd64`) or ARM64 (`arm64`). The packages are
  tested on the current Ubuntu LTS and Debian stable.
* Root rights (`sudo`) for the installation.
* Nothing else for the tarball: the agent is self-contained and brings its own .NET runtime. The `.deb` pulls in the usual
  system libraries (C/C++ runtime, OpenSSL, zlib, ICU) through `apt`.
* For commands that run as your desktop user, a systemd user session (every current desktop has one). See
  [session helper](#session-helper-user-service).

## Download and verify

Download the package for your computer from the [latest release](https://github.com/toeck98/QoffeeAutomat/releases/latest):

| File | For |
|------|-----|
| `qoffeeautomat_<version>_amd64.deb`, `qoffeeautomat_<version>_arm64.deb` | Debian, Ubuntu and derivatives (recommended there) |
| `QoffeeAutomat-<version>-linux-x64.tar.gz`, `...-linux-arm64.tar.gz` | Any other distribution with systemd |

The agent runs as root, so check what you downloaded: [Verify your download](verify-download.md) explains checksums
(`SHA256SUMS.txt`) and build attestations. The Linux packages are not signed separately.

## Install the .deb package

```bash
sudo apt install ./qoffeeautomat_<version>_amd64.deb
```

`apt` installs the dependencies. The package

* installs the program to `/usr/lib/qoffeeautomat` (owned by root, writable by nobody else) and a link `/usr/bin/qoffeeautomat`,
* creates `/etc/qoffeeautomat`, `/var/lib/qoffeeautomat` and `/var/log/qoffeeautomat` (see [Permissions](#permissions)),
* installs the system service `qoffeeautomat` and the user unit `qoffeeautomat-session`, then enables and starts the service.

Upgrade with `sudo apt install ./qoffeeautomat_<newer-version>_amd64.deb`: the service is stopped, the files are replaced and
the service starts again. Configuration, secrets, history and logs are kept.

## Install the tarball

```bash
tar -xzf QoffeeAutomat-<version>-linux-x64.tar.gz
cd QoffeeAutomat-<version>-linux-x64
sudo ./install.sh
```

`install.sh` copies the program to `/usr/local/lib/qoffeeautomat`, links `/usr/local/bin/qoffeeautomat`, registers, enables and
starts the system service (`qoffeeautomat service install`) and installs the user unit of the session helper into
`/etc/systemd/user`. Options (`./install.sh --help`):

| Option | Meaning |
|--------|---------|
| `--prefix DIR` | Install below `DIR` instead of `/usr/local`. |
| `--ui-port PORT`, `--link-port PORT` | Ports of the loopback web interface (default 8480) and of QoffeeLink (default 8481). |
| `--no-start` | Enable the service but do not start it. |
| `--no-service` | Copy the files only; do not touch systemd (for example in a container). |
| `--no-session-service` | Do not install the user unit of the session helper. |

Run `install.sh` over an installed version to upgrade it. `install.sh` refuses to run when the `.deb` is installed.

## First start

The web interface listens on `http://127.0.0.1:8480/` and is reachable from this computer only. Read the setup token and
continue with the [first-run setup](first-run.md):

```bash
sudo cat /var/lib/qoffeeautomat/secrets/setup-token.txt
```

Check the service:

```bash
systemctl status qoffeeautomat
journalctl -u qoffeeautomat
curl http://127.0.0.1:8480/healthz
```

Then [pair the agent with Home Assistant](pairing.md). Open port 8481 (QoffeeLink) in your firewall for the Home Assistant host
only, for example `sudo ufw allow from <home-assistant-ip> to any port 8481 proto tcp`.

## The system service

`qoffeeautomat.service` runs `qoffeeautomat run` as root with `Type=notify`, a watchdog and restart on failure. Do not edit the
unit file: package upgrades replace it. Use a drop-in instead:

```bash
sudo systemctl edit qoffeeautomat
```

The agent also manages itself: `sudo qoffeeautomat service install|uninstall|start|stop|status` (see
`qoffeeautomat service --help` for `--ui-port` and `--link-port`).

## Session helper (user service)

Commands with `runAs: user`, the volume, notifications, the media player and the idle time need a process inside your desktop
session. That is the systemd **user** service `qoffeeautomat-session`. It talks to the agent over a Unix socket in
`/run/user/<uid>/qoffeeautomat/session.sock` (mode `0600`: only you and root can open it).

Both packages enable it for every user (`systemctl --global enable`), so it starts with your next desktop login. Start it now,
or enable it only for yourself:

```bash
systemctl --user start qoffeeautomat-session
systemctl --user enable --now qoffeeautomat-session      # only for your user
sudo systemctl --global disable qoffeeautomat-session    # for nobody
systemctl --user status qoffeeautomat-session
```

Without a desktop session the helper does not run, and commands that need it report that no session is available; everything
else works.

## Permissions

| Path | Mode | Content |
|------|------|---------|
| `/usr/lib/qoffeeautomat` (tarball: `<prefix>/lib/qoffeeautomat`) | `0755`, root | Program files. The service starts them as root, so nobody else may write here. |
| `/etc/qoffeeautomat` | `0700`, root | Settings, commands, sensors. |
| `/var/lib/qoffeeautomat` | `0700`, root | History, certificates and `secrets/` (tokens, password hash). |
| `/var/log/qoffeeautomat` | `0700`, root | Log files. |
| Files in the three data directories | `0600`, root | |

Every program and script in the data directories runs with the rights you give it in the command, so keep the directories
private. Change nothing about these modes: the agent refuses to use secrets that other users could read.

## Uninstall

**.deb:**

```bash
sudo apt remove qoffeeautomat       # stops and disables the service; configuration and data stay
sudo apt purge qoffeeautomat        # also deletes /etc, /var/lib and /var/log of qoffeeautomat
```

**Tarball:**

```bash
sudo /usr/local/lib/qoffeeautomat/uninstall.sh               # keeps configuration and data
sudo /usr/local/lib/qoffeeautomat/uninstall.sh --purge-data  # deletes them too
```

Both stop and disable the service, remove the units and the program. Afterwards remove the integration in Home Assistant: see
[Install the integration](install-integration.md).

## Troubleshooting

| Symptom | What to check |
|---------|---------------|
| The service does not start | `journalctl -u qoffeeautomat -n 100`. A port in use is the usual cause: `sudo qoffeeautomat service install --ui-port 9480`. |
| Startup fails with a globalization error | Install the ICU library: `sudo apt install libicu-dev` (the `.deb` depends on it already). |
| `curl /healthz` fails right after the installation | The first start takes a few seconds; wait for `systemctl is-active qoffeeautomat`. |
| The session helper is not running | `systemctl --user status qoffeeautomat-session`; it needs a graphical session (`graphical-session.target`). |
| No systemd (some containers, WSL without systemd) | `install.sh --no-service`, then start the agent with `qoffeeautomat run`. |

More in [Troubleshooting](troubleshooting.md).
