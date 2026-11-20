# Install the hub with Docker

The hub image is `ghcr.io/toeck98/qoffeeautomat-hub` for `linux/amd64` and `linux/arm64`. It runs as a non-root user, keeps
all its state in the volume `/data`, has a health check (`GET /healthz`) and contains no secret: keys, TLS certificate and
the first-run token are created in `/data` on the first start.

## Docker Compose (recommended)

The repository contains a ready-made setup in
[`packaging/hub/`](https://github.com/toeck98/QoffeeAutomat/tree/main/packaging/hub):

```bash
mkdir qoffeeautomat-hub && cd qoffeeautomat-hub
# Download docker-compose.yml and .env.example from packaging/hub/ of the repository, then:
cp .env.example .env
docker compose up -d
docker compose logs hub
```

`docker-compose.yml` maps port 8490 and mounts the named volume `hub-data` at `/data`. Settings are in `.env`; pin the release
there (`QOFFEE_HUB_VERSION=0.2.0`) so that an update is a decision and not a surprise.

## Plain `docker run`

```bash
docker run -d --name qoffeeautomat-hub --restart unless-stopped \
  -p 8490:8490 -v qoffeeautomat-hub-data:/data \
  --cap-drop ALL --security-opt no-new-privileges:true \
  ghcr.io/toeck98/qoffeeautomat-hub:latest
```

If you mount a host folder instead of a named volume, it must be owned by the user ID **1654** (the user `app` of the image):
`sudo chown 1654:1654 /srv/qoffeeautomat-hub`. The hub restricts `/data` to its owner (mode 0700).

## First start: create the administrator

1. Open `https://<host>:8490`. The browser warns about the **self-signed certificate**; that is expected (see
   [TLS](#tls) below).
2. The hub has no account yet. It prints a **one-time setup token** to its log and keeps it in `/data/setup-token.txt`:
   `docker compose logs hub`.
3. Enter the token in the hub and create the first administrator. The token file is deleted afterwards. The password rules are
   those of the agent: a long password, not a common one.

If you lose the token before you used it, delete `setup-token.txt` and restart the hub; it creates a new one.

## Settings

Every setting is an environment variable; the hub's command-line options of the same meaning (`qoffeeautomat-hub --help`) win over
them.

| Variable | Default | Meaning |
|----------|---------|---------|
| `QOFFEE_HUB_DATA_DIR` | `/data` | Data directory. |
| `QOFFEE_HUB_PORT` | `8490` | Listener port in the container. Change the published port instead. |
| `QOFFEE_HUB_BIND` | `0.0.0.0` | Listener address. |
| `QOFFEE_HUB_NAME` | | Name of a **new** hub. |
| `QOFFEE_HUB_TLS` | `selfsigned` | `selfsigned`, `certificate` or `none`. |
| `QOFFEE_HUB_TLS_CERT`, `QOFFEE_HUB_TLS_KEY` | | PEM certificate and key (`certificate`). |
| `QOFFEE_HUB_TLS_PASSWORD_FILE` | | File with the password of a PKCS#12 certificate. |
| `QOFFEE_HUB_TRUSTED_PROXIES` | | Reverse proxies (addresses or CIDR ranges, comma separated) whose `X-Forwarded-*` headers are trusted. |
| `QOFFEE_HUB_LOG_FORMAT` | `json` in a container | `json` or `text`. |
| `QOFFEE_HUB_METRICS_COLLECT` | `*` | Which metrics are collected (ids or prefixes, comma separated). |
| `QOFFEE_HUB_METRICS_INTERVAL` | `60` | Collection interval in seconds. |
| `QOFFEE_HUB_RETENTION_RAW_HOURS` | `48` | Retention of raw samples. |
| `QOFFEE_HUB_RETENTION_5M_DAYS`, `QOFFEE_HUB_RETENTION_1H_DAYS` | `30`, `730` | Retention of the roll-ups. |
| `QOFFEE_HUB_RETENTION_STATES_DAYS` | `90` | Retention of the change log of text metrics. |

## TLS

* **Self-signed (default).** The hub creates a certificate on the first start and keeps it in `/data/secrets`. The enrollment
  string contains the **fingerprint** of that certificate, so agents verify the hub without a certificate authority, and Home
  Assistant shows the fingerprint for you to compare when you pair.
* **Your own certificate.** Mount the files read-only (`-v ./certs:/certs:ro`) and set `QOFFEE_HUB_TLS=certificate`,
  `QOFFEE_HUB_TLS_CERT=/certs/fullchain.pem` and `QOFFEE_HUB_TLS_KEY=/certs/privkey.pem`. The files must be readable by user
  1654.
* **Behind a reverse proxy.** `QOFFEE_HUB_TLS=none` serves plain HTTP and **requires** `QOFFEE_HUB_TRUSTED_PROXIES`. The proxy
  must terminate TLS, forward WebSockets (the agent link and the fleet link are WebSockets), and set `X-Forwarded-Proto`,
  `X-Forwarded-Host` and `X-Forwarded-For`. Do not publish the plain port to anything but the proxy.

## Update, health and logs

```bash
docker compose pull && docker compose up -d     # update to the version in .env
docker compose ps                               # "healthy" once /healthz answers
docker compose logs -f hub
```

The database is migrated when the new version starts. Make a [backup](backup.md) before every update; going back to an older
version after a migration is not supported.
