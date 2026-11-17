# QoffeeLink v1

QoffeeLink is the protocol between a QoffeeAutomat agent and its clients (the Home
Assistant integration, later the hub). This folder is the **single source of truth** for
it: the JSON Schemas in `schema/` are normative, the examples in `fixtures/` are tested
by the .NET suite (`tests/QoffeeAutomat.Contracts.Tests`), by the Python suite
(`tests_integration/test_protocol.py`) and by `scripts/dev/validate_protocol.py`.
`docs/plan/PLAN.md` S07 is the prose specification; if the two ever disagree, fix the
disagreement in the same pull request (AGENTS.md, workflow step 8).

## Layout

| Path | Content |
|------|---------|
| `schema/*.schema.json` | One JSON Schema (draft 2020-12) per message type, the manifest and the pairing request and response. `message.schema.json` is the top-level schema that dispatches on `t`; `defs.schema.json` holds shared definitions. |
| `schema/config/` | Schemas of the configuration documents (PLAN.md S06). They are not part of the wire protocol; see the section at the end. |
| `fixtures/` | Valid examples, named `<type>[.<variant>].json`. Round trips through the C# and Python models are lossless. |
| `fixtures/forward_compat/` | Valid frames that contain things a v1 receiver does not know (extra fields, unknown message type, unknown error code). They must validate and parse, but a round trip drops the unknown parts. |
| `fixtures/invalid/` | Frames that must be rejected, named `<type>.<reason>.json`. |
| `fixtures/config/` | The same layout for the configuration documents. |

The prefix of a fixture name selects the schema: a message type (`hello`, `state`, ...) is
checked against its own schema and against `message.schema.json`; `message` only against
`message.schema.json`; `manifest`, `pair_request` and `pair_response` against the schema
of the same name.

## Transport

* Endpoint `wss://<host>:8481/link/v1`, WebSocket subprotocol `qoffeelink.v1`.
* `Authorization: Bearer <link token>` on the upgrade request. An invalid token gets HTTP
  401 and no upgrade. Credentials never travel inside frames.
* One JSON object per text frame, UTF-8, `permessage-deflate`, at most 1 MiB. Field names
  are snake_case.
* WebSocket ping every 30 s from both sides; a connection without traffic for 75 s is
  dropped.
* Requests carry an integer `id` chosen by the sender. The peer answers with a `result`
  frame carrying the same `id`.

## Handshake

```
client                                   agent
  |  ---- HTTPS upgrade + Bearer ------->  |  401 if the token is unknown
  |  <-------------- hello --------------  |  protocol {min,max}, agent, manifest_rev, boot_id, features
  |  ------------ client_hello ---------> |  protocol (highest common), client {kind,name,version}, features
  |  ---- get_manifest (id 1) ----------> |  only if manifest_rev differs from the cached revision
  |  <---- result (id 1, data=manifest) -  |
  |  ---- subscribe (id 2) -------------> |  metrics: ids or "*", optional intervals
  |  <---- result (id 2) ----------------  |
  |  <---- state (full snapshot) --------  |  then deltas
```

1. The agent sends `hello` first, no matter who dialled.
2. The client picks the highest protocol version in `hello.protocol.min..max` that it
   supports and states it in `client_hello.protocol`. If there is no overlap the agent
   replies with the error `unsupported_version` and closes with code 4406.
3. The client compares `hello.manifest_rev` with its cached revision and calls
   `get_manifest` only when they differ. The same rule applies to `manifest_changed.rev`.
4. `boot_id` changes with every operating-system start and stays the same across agent
   restarts. A client that sees a new value knows the PC was restarted.
5. Features: both sides list optional features in `features`. A side only uses a feature
   when the peer announced it. Version 1 without features is the baseline.

## Message types

| `t` | Direction | Request | Schema |
|-----|-----------|---------|--------|
| `hello` | agent to client | | `hello.schema.json` |
| `client_hello` | client to agent | | `client_hello.schema.json` |
| `get_manifest` | client to agent | yes | `get_manifest.schema.json` |
| `manifest_changed` | agent to client | | `manifest_changed.schema.json` |
| `subscribe` | client to agent | yes | `subscribe.schema.json` |
| `state` | agent to client | | `state.schema.json` |
| `run` | client to agent | yes | `run.schema.json` |
| `event` | agent to client | | `event.schema.json` |
| `bye` | agent to client | | `bye.schema.json` |
| `result` | either | answer | `result.schema.json` |

Optional fields and their defaults:

* `run.vars` defaults to `{}`, `run.wait` to `true`, `run.context.user_id` is absent.
* `event.data` may be absent.
* `hello.features`, `client_hello.features`, `subscribe.intervals`, the manifest members
  `events` and `features` and the descriptor members that are not required by the schemas
  are optional. Optional fields are omitted rather than sent as `null`; receivers treat
  `null` like absent. (The only `null` that carries meaning is a value in `state.v`.)
* `agent.macs` is required but may be empty; `agent.ui_url` and `agent.os_version` are
  optional.
* A `result` with `ok: true` carries `data` (an object, `{}` if there is nothing to
  report); with `ok: false` it carries `error {code, message}`.
* `metrics` in `subscribe` is an array of metric ids or the string `"*"`. A plain string
  other than `"*"` is invalid.

The `data` of a successful `run` result has the shape `run_result_data` in
`defs.schema.json`: `run_id`, and when `wait` was true `exit_code`, `stdout`, `stderr`,
`duration_ms`, `timed_out`.

## State delivery rules

Requirement R-06: efficient communication.

* Only metrics in the current subscription set are sampled and sent (D-007). `subscribe`
  replaces the whole set; its `result` is followed by a full `state` snapshot of the
  subscribed metrics.
* Values are compared after applying the metric's `precision` and `deadband`. A metric
  that did not change is not sent.
* Changes are coalesced and flushed at most once per second per connection. One `state`
  frame carries many metrics.
* `subscribe.intervals` (metric id to seconds) additionally limits the rate: the client
  receives at most one value per interval for these metrics.
* `ts` is the Unix time in milliseconds when the frame was built. A value of `null` means
  "unavailable".
* Values are JSON numbers, strings or booleans. Timestamps are ISO 8601 strings in UTC;
  enum metrics are strings.

## Manifest and `rev`

The manifest describes what the agent offers: metrics, commands (with variables and the
Home Assistant control) and events. It is delivered as the `data` of the result of
`get_manifest`.

`rev` is the first 12 hexadecimal characters (lower case) of the SHA-256 of the canonical
manifest JSON **without the `rev` member**. Canonical JSON (D-039):

* UTF-8, no insignificant whitespace;
* object members sorted by name, ordinal order of the UTF-16 code units; array order is
  kept;
* members whose value is `null` are omitted;
* strings are escaped only where JSON requires it: `"` and `\`, the control characters
  as `\b \f \n \r \t`, all others below U+0020 as lower-case `\u00xx`; everything else,
  including non-ASCII characters and `/`, is written as is;
* numbers are written as plain integers or decimals without exponent, in the shortest
  form (`35.5`, never `35.50` or `3.55e1`); `validate_protocol.py` rejects other forms.

`fixtures/manifest.json` carries its correct `rev`. `python scripts/dev/validate_protocol.py`
recomputes it, and `python scripts/dev/validate_protocol.py --update-rev` rewrites it after
the manifest fixture was edited (and updates the `manifest_rev` of `hello*.json`).

## Error codes

Carried in `result.error.code`. The list is closed for v1 senders; receivers must tolerate
unknown codes and treat them like `internal`.

| Code | Meaning |
|------|---------|
| `unauthorized` | The link token is unknown or revoked. |
| `unsupported_version` | No common protocol version. |
| `bad_request` | The frame or its payload is malformed. |
| `unknown_command` | The command id does not exist. |
| `command_disabled` | The command exists but is disabled. |
| `invalid_vars` | A variable is missing, unknown or invalid. |
| `busy` | The command (or mode group) is already running and its concurrency forbids another run. |
| `cooldown` | The command is within its cooldown period. |
| `no_session` | The command needs a logged-in user and nobody is logged in. |
| `elevation_unavailable` | `runAs: user_elevated` but the user is not an administrator. |
| `cancelled` | The run was cancelled. |
| `timeout` | The run exceeded its timeout and was killed. |
| `paused` | The agent is in maintenance mode and does not run commands. |
| `condition_not_met` | The command's conditions (required session state, time window) are not met. Added after the first v1 release in a backward compatible way: receivers treat unknown codes as a generic failure. |
| `internal` | Unexpected error in the agent. |

## Close codes

| Code | Name | Used when |
|------|------|-----------|
| 1000 | normal | Orderly close by either side. |
| 1001 | going away | After `bye`. |
| 1009 | message too big | A frame exceeds 1 MiB. |
| 1011 | internal error | Unexpected failure in the agent. |
| 4400 | `bad_request` | Handshake violation (no valid `client_hello`, not JSON, not an object). |
| 4401 | `unauthorized` | The link token was revoked while the connection was open. |
| 4406 | `unsupported_version` | The client's version is outside the agent's range. |

The code `44xx` mirrors the HTTP status of the corresponding error. After a handshake error
the agent first sends a `result` with the error, then closes.

## Compatibility rules

1. **Unknown fields are ignored** by every receiver; schemas do not set
   `additionalProperties: false` on messages.
2. **Unknown message types are ignored**, never an error.
3. **Closed enums** (`bye.reason`, `client.kind`, metric `kind`, `value_type` and
   `context`, variable `type`, control `type`) are defined per protocol version. The v1
   schemas list the values of v1, so a v1 receiver may treat other values as malformed.
   A later version that adds values bumps `protocol.max` and uses them only when
   negotiated. **Open vocabularies** (error codes, feature names, event types) can grow
   at any time; receivers tolerate unknown entries.
4. **Adding** optional fields or optional features does not need a new protocol version.
   **Removing or renaming** fields, changing meaning or adding mandatory fields is a breaking
   change: bump the protocol version (`hello.protocol.max`) and the schema folder.
5. The fixtures are the contract: a change to a schema or a model comes with a fixture, and
   CI fails when the .NET or the Python side drifts.

## Pairing

`POST /link/v1/pair` over HTTPS on the link listener, no bearer token.

Request (`pair_request.schema.json`): `{"code":"123456","client":{"kind":"homeassistant","name":"Home"}}`.
Response (`pair_response.schema.json`): `{"token":"<base64url 32 bytes>","agent":{"id":"...","name":"..."}}`.

Codes have 6 digits, live 5 minutes, are single-use and are invalidated after 5 failed
attempts. The token is shown once; the agent stores only its SHA-256 hash. Failure responses
(wrong or expired code, too many attempts) use HTTP status codes and are specified with the
pairing implementation (task T211).

## Reverse link (the agent dials the client)

For roaming devices behind NAT the agent dials a WebSocket endpoint that the Home Assistant
integration exposes (ADR `docs/adr/0003-reverse-link.md`, decision D-043). Only the transport
differs; after the upgrade the connection is QoffeeLink v1 as described above, with the agent
sending `hello` first (S07: "no matter who dialled").

| | Inbound link | Reverse link |
|---|---|---|
| Who dials | the client | the agent |
| Endpoint | `wss://<agent>:8481/link/v1` | `wss://<home assistant>/api/qoffeeautomat/reverse/v1` |
| Credential | link token (hash kept by the agent) | reverse token `qalr_…` (hash kept by Home Assistant, token kept by the agent) |
| Unknown credential | HTTP 401, no upgrade | HTTP 401, no upgrade; the agent stops dialling until it is enrolled again |
| Revoked while connected | close code 4401 | close code 4401, same consequence |
| TLS | self-signed, pinned fingerprint of the agent | system trust store, or a pinned fingerprint of the certificate of Home Assistant or its proxy; `ws://` only for loopback hosts |
| Reconnect | the client, with backoff | the agent, 1 s to 5 min with jitter, at once after a resume or a network change |

The enrollment string that carries URL, token and optional fingerprint to the agent is
`qalr1.<base64url(JSON {"url","token","fp"})>`. `fixtures/reverse/enrollment.json` is the
decoded payload; `fixtures/reverse/handshake*.json` are transcripts of the handshake and of
its failures (frames refer to the fixtures of this folder by name). They are replayed by
`tests/QoffeeAutomat.Agent.Tests` (`Category=OutboundLink`) against the agent and by
`tests_integration/test_reverse_link.py` against the endpoint of Home Assistant. There is no
JSON Schema for them: they are not part of the frames of the protocol.

## Configuration documents

`schema/config/` publishes the schemas of the documents that the management API and the
hub exchange (PLAN.md S06): command (`command.schema.json`), custom sensor
(`sensor.schema.json`), mode group (`mode.schema.json`), metric overrides and agent
settings, with the common header `schemaVersion`, `revision`, `updatedAt`, `origin`. Their JSON uses
camelCase. The schemas describe the structure only; semantic rules (for example the
`haControl` requirements or cycle detection in sequences) are enforced by the
`DocumentValidator` in `QoffeeAutomat.Core`.

## Checking everything

```bash
python -m pip install -r requirements-dev.txt
python scripts/dev/validate_protocol.py          # schemas, fixtures, rev
dotnet test tests/QoffeeAutomat.Contracts.Tests  # C# round trips and schema validation
pytest tests_integration/test_protocol.py        # Python models
```
