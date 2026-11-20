# End-to-end test

A real Home Assistant container with `custom_components/qoffeeautomat` mounted, and the Linux
agent with the simulated platform (PLAN.md S14, task T508).

```bash
pytest tests/e2e --ignore=tests/e2e/hub                  # builds and starts the stack, runs the tests, stops it
HA_VERSION=beta pytest tests/e2e --ignore=tests/e2e/hub  # another Home Assistant image tag (default: stable)
docker compose -f tests/e2e/docker-compose.yml config   # validate the compose file only
```

`E2E_AGENT_PLATFORM=linux pytest tests/e2e` runs the same test with the real Linux platform instead of
the simulated one (the agent reads `/proc` and `/sys` inside its container). The workflow runs both variants.

Environment variables: `HA_VERSION`, `E2E_AGENT_PLATFORM` (`simulated` or `linux`), `E2E_HA_PORT` (18123), `E2E_AGENT_PORT` (18481),
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

## Hub scenario (`hub/`, task T919)

`tests/e2e/hub/` has its own compose stack (project `qoffeeautomat-e2e-hub`): the hub image built from
`packaging/hub/Dockerfile`, two agents (`E2E-PC-1`, `E2E-PC-2`, simulated platform, the image of
`agent.Dockerfile`) and Home Assistant. The tests tell one story in file order: set up the hub, enroll both
agents with one token, roll a profile (a command and a sensor) out to a tag, read stored metrics through the
query API and the Prometheus export, run the command through the management tunnel and as a bulk run, pair
Home Assistant with the hub, check the devices and entities of both PCs and `run_command` through the fleet link,
lock the local configuration and check that a local write is refused, and remove one agent.

```bash
pytest tests/e2e/hub                  # builds and starts the hub stack, runs the scenario, stops it
docker compose -f tests/e2e/hub/docker-compose.yml config   # validate the compose file only
pytest tests/e2e --ignore=tests/e2e/hub                     # the agent scenario above, without the hub
```

Environment variables: `HA_VERSION`, `E2E_HUB_PORT` (18490), `E2E_HUB_AGENT1_PORT` (18491), `E2E_HUB_AGENT2_PORT`
(18492), `E2E_HUB_HA_PORT` (18223), `E2E_AGENT_PASSWORD`, `E2E_SKIP_COMPOSE=1`, `E2E_KEEP_STACK=1`. The job
`e2e-hub` of the workflow runs it for both image tags and uploads the logs of all four containers on failure.

| Path | Content |
|------|---------|
| `hub/docker-compose.yml` | Hub, two agents, Home Assistant. Ports are published on loopback only; the hub runs with the hardening of `packaging/hub/docker-compose.yml`. |
| `hub/library/` | The hub library documents that the scenario publishes: the command `hub_echo` (uses the hub variable `GREETING`) and the sensor `hub_marker`. |
| `hub/hub_driver.py`, `hub/conftest.py`, `hub/test_hub_e2e.py` | The pytest driver (standard library only; HTTP client and polling helper from `e2e_driver.py`). |
