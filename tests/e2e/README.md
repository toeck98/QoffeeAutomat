# End-to-end test

A real Home Assistant container with `custom_components/qoffeeautomat` mounted, and the Linux
agent with the simulated platform (PLAN.md S14, task T508).

```bash
pytest tests/e2e                      # builds and starts the stack, runs the tests, stops it
HA_VERSION=beta pytest tests/e2e      # another Home Assistant image tag (default: stable)
docker compose -f tests/e2e/docker-compose.yml config   # validate the compose file only
```

Environment variables: `HA_VERSION`, `E2E_HA_PORT` (18123), `E2E_AGENT_PORT` (18481),
`E2E_AGENT_PASSWORD`, `E2E_SKIP_COMPOSE=1` (use a running stack), `E2E_KEEP_STACK=1` (do not
stop the stack afterwards). The test is skipped when `docker` is not installed.

| Path | Content |
|------|---------|
| `docker-compose.yml` | The two services. Ports are published on loopback only. |
| `agent.Dockerfile` | Publishes the agent, installs `pwsh` from the PowerShell .NET tool. |
| `agent/entrypoint.sh` | Seeds the configuration on first start, sets the password, reports readiness. |
| `agent/seed/` | Settings (`remoteUi` on so the driver can reach the API), the command `e2e_echo` with variables, the button `e2e_ping`, the custom sensor `e2e_marker`. |
| `ha/configuration.yaml` | Home Assistant configuration (`default_config` gives the onboarding API). |
| `e2e_driver.py`, `conftest.py`, `test_e2e.py` | The pytest driver (standard library only). |

The workflow `.github/workflows/e2e.yml` runs nightly, on demand and on pull requests with the
label `release`, for the `stable` and `beta` image, and uploads the logs of both containers when
a run fails.
