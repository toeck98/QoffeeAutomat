# Metrics and Prometheus

The hub collects the metrics of the PCs that joined it, keeps their history in its own SQLite database and exports the current
values in the Prometheus text format. No external database is needed.

## What is stored

For every metric of every PC the hub keeps raw samples (48 hours), 5 minute roll-ups (30 days) and 1 hour roll-ups (2 years); text
metrics (strings, enums, timestamps) are stored when they change (90 days). The page **Metrics** shows the fleet, and the history
page of a PC shows one PC. The numbers and how to size the disk for your fleet are in
[Sizing the hub for metric history](../hub-sizing.md).

In the container you change what is collected and how long it is kept with environment variables (see
[Install with Docker](docker.md#settings)); the Home Assistant app uses the defaults:

| Variable | Default |
|----------|---------|
| `QOFFEE_HUB_METRICS_COLLECT` | `*` (every metric) |
| `QOFFEE_HUB_METRICS_INTERVAL` | `60` seconds |
| `QOFFEE_HUB_RETENTION_RAW_HOURS` | `48` |
| `QOFFEE_HUB_RETENTION_5M_DAYS` | `30` |
| `QOFFEE_HUB_RETENTION_1H_DAYS` | `730` |
| `QOFFEE_HUB_RETENTION_STATES_DAYS` | `90` |

A PC that leaves the hub or is removed from it takes its history with it.

## Prometheus

`GET /metrics` on the agent port (`https://<hub>:8490/metrics`) returns the current values of all PCs.

1. In the hub open **API tokens** and create a token with the role **`viewer`**. Copy it; it is shown once.
2. Add a scrape job:

```yaml
scrape_configs:
  - job_name: qoffeeautomat
    scheme: https
    metrics_path: /metrics
    authorization:
      type: Bearer
      credentials_file: /etc/prometheus/qoffeeautomat.token
    static_configs:
      - targets: ["hub.example.lan:8490"]
    tls_config:
      insecure_skip_verify: true   # self-signed certificate; better: ca_file with your own CA
```

The endpoint accepts **only a read-only token**. A session cookie is refused, and a token with a more powerful role gets `403`, so
that a token that can run commands is never kept in a scrape configuration. Use a separate token for each scraper so that you
can revoke them one by one.

With the self-signed certificate you can either skip verification (the connection stays encrypted, but the server is not
authenticated), or give the hub a certificate of your own ([TLS](docker.md#tls)) and verify it.

## Grafana and others

Grafana reads the Prometheus data. For other tools, the query API of the hub (`GET /api/v1/metrics/query`, documented in the
OpenAPI description of the hub) returns the stored history with a role `viewer` token.
