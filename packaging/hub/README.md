# Packaging of the QoffeeAutomat hub

The hub (`src/QoffeeAutomat.Hub`, PLAN.md S20) ships as one multi-arch container image and as a Home Assistant app that is
built on that image (D-024). User documentation: [`docs/user/hub/`](../../docs/user/hub/index.md).

| Path | Content |
|------|---------|
| `Dockerfile` | Production image `ghcr.io/toeck98/qoffeeautomat-hub` for `linux/amd64` and `linux/arm64`: multi-stage, non-root, `/data` volume, health check. Build context: the repository root. |
| `healthcheck.sh` | The `HEALTHCHECK` of the image (`GET /healthz` on the loopback address). |
| `docker-compose.yml`, `.env.example` | Compose example with a named volume. |
| `ha-app/` | The Home Assistant app `ghcr.io/toeck98/qoffeeautomat-hub-app`: `config.yaml` (the only file of that name in the repository, D-034), `Dockerfile` (a layer on the hub image), `rootfs/run.sh` (entry point), `translations/`, `DOCS.md`. |
| `../../repository.yaml` | Makes the repository an app repository. |

Local build and test of the container:

```bash
docker build -f packaging/hub/Dockerfile -t qoffeeautomat-hub .
docker compose -f packaging/hub/docker-compose.yml up
shellcheck packaging/hub/healthcheck.sh packaging/hub/ha-app/rootfs/run.sh
```

Release: the jobs `hub-image` and `hub-app-image` of `.github/workflows/release.yml` build and push both images with the
release version, `latest` (stable releases only) and a build provenance attestation. `release-please` keeps the `version` of
`ha-app/config.yaml` in step with the release.
