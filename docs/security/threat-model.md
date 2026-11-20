# QoffeeAutomat threat model and security review (1.0 and the central hub)

Task T606 (PLAN.md S09, S13, S19). This page maps **every item of the security model S09** to the code that implements it and the
tests that pin it, lists the result of the review for the 1.0 release, and keeps the register of findings. Sections 1 to 9 cover
the agent and the integration; **section 10 covers the central hub** (task T919, PLAN.md S20 "Hub security"), with its own review
result and findings register (H-nn).

* The specification is [`docs/plan/PLAN.md`](../plan/PLAN.md) S09. The user-facing summary is `docs/user/security.md`; the
  reporting channel is [`SECURITY.md`](../../SECURITY.md).
* Paths are relative to the repository root. Test names are the method names in the test project named in the row.
* This page is part of the repository and is **not** published on the documentation site (`exclude_docs` in `mkdocs.yml`).

## 1. Status of this review

| | |
|---|---|
| Review date | 2026-11-17 (branch `agent/m6-release-1-0`) |
| Method | Reading the code of every network boundary and of the places that start processes, write secrets or log. A sample, **not** a line-by-line audit of the roughly 38 000 lines of Agent, Core, SessionHost and Platform.Windows code. |
| Not done by the author of this page | Running the build, the tests, CodeQL, Scorecard, a dependency scan or anything on Windows. Those are listed as owner actions in [release-1.0-checklist.md](../maintainers/release-1.0-checklist.md). |
| Result | **No finding of severity high.** Two findings were fixed (F-01, F-02). Five low or informational findings stay open as issues or accepted risks (section 7). |

Severity scale: **high** (remote or cross-user compromise, secret disclosure, bypass of authentication), **medium** (weakens a
control, needs special conditions), **low** (hardening, availability, documentation gap), **info**.

"Reviewed" in the tables says how deeply the code was read for this review: **read** (the implementation was read), **tests**
(only the tests and their names were checked), **n/r** (not read, owner or a second reviewer should do it).

## 2. Assets, actors, boundaries

| Asset | Where | Protected by |
|-------|-------|--------------|
| Code execution as LocalSystem / the user | commands, scripts, `runAs` | authentication (S09.1), validation (S09.4), ACLs (S09.5) |
| Management password hash, link token hashes, TLS key, other secrets | `%ProgramData%\QoffeeAutomat\secrets` | DPAPI machine scope, directory ACL (S09.5, S09.6) |
| Variable values of runs (may be secret) | memory, run history, parameter file of a run | redaction (S09.8), private per-run directory (F-02) |
| Command, sensor and script definitions | `config`, `scripts` | ACLs, session-protected API |

| Actor | Can reach | Must not be able to |
|-------|-----------|---------------------|
| Standard user on the same PC | loopback port 8480, `/local/v1/emit`, the session pipe, world-readable files | get code run as SYSTEM, read secrets, change scripts or config |
| Device on the LAN | port 8481 (HTTPS): `/healthz`, `/link`, `/link/v1/pair`; the UI and API only while remote UI is on | run anything, pair without the one-time code, read the UI |
| Home Assistant (malicious values) | the authenticated QoffeeLink connection: run an enabled command with declared variables | create or change commands, inject code through a value |
| Holder of a stolen link token | QoffeeLink as that one client | anything beyond that client's rights; revocable |

Not in scope (stated in `docs/user/security.md`): a local administrator, and anybody who has the management password (they author
commands, which is the product's purpose).

## 3. Coverage of S09

| S09 | Control | Implementation | Tests | Reviewed | Result |
|-----|---------|----------------|-------|----------|--------|
| 1 | Authentication everywhere (D-017) | `src/QoffeeAutomat.Agent/Security/Auth/ManagementApi.cs` (`CreateGroup`: `RequireAuthorization` + `CsrfHeaderFilter` on the whole `/api/v1` group), `SessionAuthentication.cs` | `RouteProtectionTests`: `Every_api_route_except_the_documented_exceptions_returns_401_without_a_session`, `Only_the_documented_routes_allow_anonymous_access`, `Every_api_route_requires_the_management_policy_unless_it_is_documented_as_open` | read | ok, see section 4 |
| 1 | Setup token, owner-only file | `SetupTokenService.cs` (24 random bytes, `ownerOnly: true`, constant-time compare over hashes, deleted after setup) | `SetupTokenTests`, `AuthEndpointTests`: `Setup_needs_the_right_token_and_an_acceptable_password`, `Setup_signs_in_and_ends_earlier_sessions_and_the_token_is_gone` | read | ok |
| 1 | PBKDF2-SHA256, at least 600 000 iterations | `src/QoffeeAutomat.Core/Security/PasswordHasher.cs` (moved from Agent to Core by T901, shared with the hub; `Iterations = 600_000`, weaker hashes never accepted, upper bound against forged hashes, `FixedTimeEquals`); length policy in `PasswordService.cs` and `AuthOptions.cs` (10 to 256 characters) | `PasswordHasherTests`, `AuthEndpointTests`: `The_password_is_stored_as_a_hash_in_the_secret_store_and_never_in_plain_text` | read | ok |
| 1 | Session cookie `HttpOnly`, `SameSite=Strict`, `Secure` on HTTPS | `SessionAuthentication.cs` (`SessionCookie.Options`); server-side `SessionStore.cs` (256-bit id, only the SHA-256 is kept, idle 8 h, lifetime 7 d, at most 32 sessions) | `AuthEndpointTests`: `The_session_cookie_is_http_only_same_site_strict_and_secure_on_https`, `Logout_ends_the_session_and_clears_the_cookie`; `SessionStoreTests` | read | ok |
| 1 | CSRF header and `Origin` check | `CsrfHeaderFilter.cs` (all methods except GET, HEAD, OPTIONS, TRACE; `Origin`, when present, must equal the request origin) | `AuthEndpointTests`: `State_changing_requests_without_the_anti_forgery_header_are_rejected`, `Sign_in_and_setup_need_the_anti_forgery_header_too`, `A_request_from_another_origin_is_rejected_even_with_the_header` | read | ok |
| 1 | Rate limit and lockout (5 per address / 30 overall in 15 min, lockout 15 min) | `AttemptLimiter.cs`, `AccountService.cs`, `AuthOptions.cs`; keyed by the remote address, forwarding headers are not trusted (`AuthEndpoints.Caller`) | `AttemptLimiterTests`, `AuthEndpointTests`: `Five_wrong_passwords_lock_sign_in_out`, `A_lockout_of_one_address_does_not_lock_out_another`, **new** `A_burst_of_parallel_wrong_passwords_cannot_exceed_the_failure_limit` | read | **F-01 fixed**; F-05 accepted |
| 1 | `reset-password` | `Security/Auth/ResetPasswordCommand.cs` (needs administrator or root, writes a new setup token, old password works until the new one is set) | `SetupTokenTests`: `Reset_password_without_administrator_rights_is_refused_and_writes_nothing`, `Reset_password_as_administrator_recreates_the_setup_token`; `AuthEndpointTests`: `Reset_password_makes_setup_available_again_and_a_new_password_replaces_the_old_one` | read | ok |
| 2 | Link tokens: 32 random bytes, shown once, stored as SHA-256, individually revocable, several clients | `Link/Pairing/LinkTokenStore.cs` (`IssueAsync`, `ValidateAsync` compares against all records with `FixedTimeEquals`, `RevokeAsync`), `PairingService.cs` (revocation closes open connections with 4401, re-check after registering) | `PairEndpointTests`: `A_pairing_started_in_the_ui_pairs_a_client_and_the_token_authenticates`, `The_client_list_shows_the_client_and_revoking_it_rejects_its_token_and_closes_its_connection`; `PairingServiceTests` | read | ok |
| 2 | One-time pairing code | `PairingCodeManager.cs` (6 digits, 5 minutes, one use, invalidated after 5 wrong attempts, constant-time compare); `PairingEndpoints.cs` (HTTPS only, 4 KiB body, `Cache-Control: no-store`) | `PairingCodeTests`, `PairEndpointTests`: `A_wrong_code_is_unauthorized_and_five_wrong_attempts_invalidate_the_code`, `A_code_pairs_only_once_over_http`, `Pairing_is_refused_over_plain_http`, `An_oversized_pairing_request_is_rejected` | read | ok; F-05 accepted |
| 3 | TLS: self-signed ECDSA P-256, SANs, fingerprint pinning | `src/QoffeeAutomat.Core/Security/SelfSignedCertificateFactory.cs` and `CertificateFingerprint.cs` (moved from Agent to Core by T901, shared with the hub), `src/QoffeeAutomat.Agent/Security/Tls/TlsCertificateProvider.cs` (stored in the secret store, replaced when expired or unreadable); fingerprint shown by `GET /api/v1/link/pairing` and the HA config flow | `TlsCertificateTests` (all), `PairEndpointTests` | read | ok; **F-04**: "a user-supplied certificate can replace it" has no supported path yet |
| 3 | QoffeeLink only over HTTPS/WSS, bearer checked before the upgrade | `Link/LinkEndpoint.cs` (`HandleAsync`: HTTPS, bearer, then sub-protocol, then accept); frame size and send queue bounded in `LinkOptions.cs` | `LinkHandshakeTests`, `LinkRequestTests` | read | ok |
| 4 | No injection (D-012), values validated against the definition | section 5 | section 5 | read | ok; F-06 accepted |
| 5 | ACLs on config, scripts, secrets; `runAs: system` refused for writable scripts | `src/QoffeeAutomat.Platform.Windows/Security/DataDirectories.cs` (`DataDirectoryProvisioner`: protected DACL, inheritance off, owner Administrators, drive roots refused, start-up `EnsureSecure`), `SystemScriptAclGuard.cs`, `AclEvaluator.cs`; `Core/Execution/PowerShell/PowerShellRunner.cs` (`ScriptGuard` check) | `DataDirectoryProvisionerTests` (all), `SystemScriptAclGuardTests` (all), `AclEvaluatorTests`, `RealAclTests` (real file system, Windows CI) | read | ok; section 6 for a fresh install |
| 6 | Secrets at rest: DPAPI machine scope, key file 0600 | `Platform.Windows/Security/DpapiSecretProtector.cs` (`DataProtectionScope.LocalMachine`, context as entropy), `Core/Secrets/FileKeyProtector.cs`, `SecretStore.cs` | `DpapiSecretProtectorTests`: `A_blob_cannot_be_used_for_another_name`, `A_damaged_blob_is_rejected`; `FileKeyProtectorTests`, `SecretStoreTests` | read | ok |
| 7 | SessionHost pipe: ACL, client image check, server check | `Platform.Windows/Sessions/SessionPipeServer.cs` (`SessionPipeSecurity`: SYSTEM full, INTERACTIVE read/write, NETWORK denied; first instance flag; `CheckPeer`: not session 0 and image equals the installed SessionHost; `CheckHello`: role, protocol, claimed session equals real session), `PipePeer.cs` (`GetNamedPipeClientProcessId`, `QueryFullProcessImageNameW`); client side `src/QoffeeAutomat.SessionHost/Ipc/PipeServerVerifier.cs` (server in session 0, image equals the agent, token user SYSTEM where readable) | `SessionIpcTests` (Windows CI): `A_client_whose_image_is_not_the_installed_session_host_is_rejected`, `A_client_in_the_services_session_is_rejected`, `A_client_that_claims_another_session_than_the_one_it_runs_in_is_rejected`, `A_client_that_claims_to_be_an_agent_is_rejected`, `A_session_host_that_does_not_trust_the_server_never_says_hello` | read | ok, see 6.3 |
| 8 | Audit log of every run with redaction | `Core/History/RunRecord.cs` (`RunRecord`: source, client id, user id, variables; `RunVariables`: secret and undeclared variables become `***`, secret values are also removed from output), `SqliteRunHistory.cs`, `CommandEngine.cs` (redaction before storing and before output is returned) | `CommandAdmissionTests`, `VariableBinderTests`; run API tests `RunApiTests` | read | ok for runs; **F-03**: `retention.auditLogDays` is not used, management actions are not in the run history |
| 13 | Update check and self-update (T704, D-046): the only request to the internet, off-able; checksum and Authenticode check before the installer starts | `Agent/Updates/UpdateChecker.cs` (no call at all while `updates.check` is false or the platform cannot update), `GitHubReleaseClient.cs` (HTTPS GitHub hosts only, also after redirects; size and time limits; no cookie, no identifier), `AgentUpdater.cs` (checksum from `SHA256SUMS.txt`, refusal deletes the installer, signature required when the running build is signed, `bye` reason `update`, fixed argv, work directory under the data directory), `Platform.Windows/Updates/WinTrustAuthenticodeVerifier.cs` | `Updates` category in `QoffeeAutomat.Agent.Tests`: `UpdaterTests`, `UpdateCheckerTests`, `ChecksumAndVersionTests` | written, not yet reviewed | The update action has no parameters; Home Assistant cannot influence what runs. Residual risk U-1 below. |
| 9 | Remote UI off by default; firewall only private and domain | `Agent/Security/Auth/RemoteAccessGate.cs` (non-loopback callers get 403 for everything except `/healthz`, `/link`, `/local`), `Hosting/Listeners.cs` (loopback HTTP listener, HTTPS listener only with a certificate); `packaging/windows/QoffeeAutomat.iss` (`AddFirewallRule`: `profile=private,domain`, program-bound) | `RouteProtectionTests`: `The_management_api_is_only_served_to_this_pc_while_remote_ui_is_off`; `UiHostingTests`; `test-installer.ps1` (installer job of `ci-dotnet.yml`) | read | ok |
| 10 | Supply chain | `.github/workflows/`: actions pinned by SHA (AGENTS.md), `dependabot.yml` (nuget, pip, github-actions, devcontainers), `codeql.yml` (weekly + PR), `dependency-review.yml` (fails on moderate, denies GPL/AGPL/SSPL), `scorecard.yml` (weekly), `ci-meta.yml` (zizmor, actionlint), `release.yml` (SBOM, SHA256SUMS, build provenance attestations, verification before publish) | the workflows themselves; `scripts/plan/validate.py` | read (workflows) | ok; alerts must be read on GitHub (checklist) |

## 4. Authentication and input of every endpoint

Every route of the agent. "Session" means the policy `QoffeeAutomatManagement` (session cookie); "gate" means `RemoteAccessGate`
(loopback only unless remote UI is on). All routes under `/api/v1` also carry the CSRF filter for state-changing methods.

| Route | Methods | Auth | Body or input limit | Notes |
|-------|---------|------|--------------------|-------|
| `/healthz` | GET | none (by design) | none | Returns status and version only. Not gated, so a LAN device learns the version. Accepted. |
| `/api/v1/auth/setup` | GET, POST | anonymous | 8 KiB (`AuthEndpoints.MaxBodyBytes`) | Only while the setup token file exists; needs the token; attempt-limited; answers 404 once a password exists. |
| `/api/v1/auth/login` | POST | anonymous | 8 KiB | Attempt-limited, password length bounded. |
| `/api/v1/auth/{logout,me,password}` | POST, GET, POST | session | 8 KiB | `password` needs the current password and uses the same limiter and gate (F-01). |
| `/api/v1/commands`, `/commands/{id}`, `/commands/validate` | GET, POST, PUT, PATCH, DELETE | session | 256 KiB (`CommandEndpoints.MaxBodyBytes`), read with `ApiResults.ReadBodyAsync`; whole document validated by `DocumentValidator` | Ids match `^[a-z][a-z0-9_]{0,62}$` (`ScriptNames.IsValidDocumentId`). |
| `/api/v1/commands/{id}/run`, `/commands/test` | POST | session | 256 KiB (`RunEndpoints.MaxBodyBytes`); variables validated by `VariableBinder` (section 5) | `test` runs an unsaved command document: needs the session by design (equivalent to authoring). |
| `/api/v1/runs`, `/runs/{id}`, `/runs/{id}/stream` | GET | session | read only | Output of secret variables is redacted. |
| `/api/v1/scripts`, `/scripts/{name}` | GET, PUT, DELETE | session | 3 MiB body (`ScriptEndpoints.MaxBodyBytes`); names by `ScriptNames` (no separators, no `..`, no `:`, no reserved device names, 128 characters) | A path is never built from an unvalidated name; `PowerShellRunner.ValidateSource` re-checks the name and that the full path stays below the scripts directory. |
| `/api/v1/sensors`, `/sensors/{id}`, `/sensors/test` | GET, POST, PUT, DELETE | session | 256 KiB (`SensorEndpoints.MaxBodyBytes`); documents validated by `DocumentValidator.ValidateSensor` | Sensors take no variables from callers; `/sensors/test` runs an unsaved sensor document and needs the session like authoring does. |
| `/api/v1/metrics`, `/metrics/{id}`, `/metrics/stream`, `/metrics/history` | GET, PUT, DELETE | session | 16 KiB for overrides | |
| `/api/v1/settings`, `/status`, `/agent/restart` | GET, PUT, POST | session | 16 KiB; validated by `DocumentValidator.ValidateSettings` | Restart needs the session and the CSRF header. |
| `/api/v1/config/export`, `/config/import`, `/config/import/preview`, `/config/revisions/...` | GET, POST | session | import: 32 MiB request, at most 2000 entries, 2 MiB per entry (counted while reading, not only from the header), 64 MiB declared total | Entry names are only classified, never used as a path (`ConfigArchive.Classify`); everything is validated before the first write; settings are skipped unless asked for. An archive is **code** (commands, scripts): import only archives you trust. |
| `/api/v1/diagnostics` | GET | session | none | Settings, versions and the last 5000 log lines pass through `SecretScrubber` (PEM blocks, password hashes, bearer tokens, `password=`/`token=` members, long token-like strings). |
| `/api/v1/events`, `/logs` | GET | session | `limit` 1 to 500 / 1 to 1000 | `/logs` does not apply the scrubber, see F-07. |
| `/api/v1/link/pairing`, `/link/clients`, `/link/clients/{id}` | GET, POST, DELETE | session | none | Returns the pairing code and the fingerprint. |
| `/openapi/v1.json` | GET | session | none | Mapped outside the group but with `RequireAuthorization` (`OpenApiEndpoint.cs`). |
| `/link/v1/pair` | POST | one-time code | 4 KiB; client kind from a fixed list, name 1 to 100 characters without control characters | HTTPS only; not gated. |
| `/link` | GET (WebSocket) | bearer token, checked before the upgrade | frames bounded by `MaxFrameBytes`, send queue bounded | HTTPS only; sub-protocol required. |
| `/local/v1/emit` | POST | per-run bearer secret, loopback callers only | 32 KiB | Only publishes a custom event; token file in the secrets directory is deleted at start and stop (`LocalChannelEndpoints.cs`). Tests: `LocalChannelTests`. |
| UI static files, client-route fallback | GET | none, but gated | n/a | Served only to loopback callers unless remote UI is on (`UiHosting.cs`). Contains no secret. No CDN, no external host. |

`RouteProtectionTests` enumerates the endpoints of the host and fails when a route below `/api/v1` is reachable without a session
other than setup and login, which keeps this table honest when routes are added.

Other boundaries:

* **QoffeeLink frames** are parsed with the generated serializers of `QoffeeAutomat.Contracts` and the schemas in `protocol/`;
  `command.run` variables go through the same `VariableBinder` as the REST API (`CommandEngine`).
* **Host header**: the agent does not validate it (DNS rebinding). A rebound page has no session cookie for the real origin, the
  API answers 401, and `/local/v1/emit` needs a secret that only started processes know, so this is hardening, not a hole (F-09).
* **Kestrel** defaults apply for header sizes and timeouts; the body limits above are set per endpoint.

## 5. Variables in every action type (D-012)

| Place | What happens to a value | Code | Tests |
|-------|-------------------------|------|-------|
| Entry (REST, QoffeeLink, UI, sequence step) | Unknown names rejected; type, range, step, `maxLength`, `pattern` (100 ms timeout), `options` checked; numeric strings and `"true"`/`"false"` only coerced where documented; not-finite numbers rejected | `Core/Validation/VariableBinder.cs`, `ParameterValues.cs` | `VariableBinderTests`, `ParameterDefinitionTests` |
| Sequence | Each step is admitted as its own run with its own `vars`; the variables of the parent are not passed on implicitly, so a value cannot reach a command that did not declare it | `CommandEngine.RunSequenceAsync` | `CommandAdmissionTests` |
| PowerShell, inline and file | Values are written to a JSON parameter file (strings as Base64 of UTF-8, numbers and booleans in canonical form, names must match `^[A-Za-z][A-Za-z0-9_]{0,31}$`), a fixed wrapper reads them and splats them into the target script (`& $target @arguments`); the wrapper contains no data and never evaluates text as code; the engine is started with `-File wrapper.ps1` | `Core/Execution/PowerShell/PowerShellWrapper.cs`, `PowerShellRunner.cs` | `PowerShellWrapperTests`: `The_wrapper_cannot_run_text_as_code_and_holds_no_data`, `The_parameter_file_never_contains_a_value_as_readable_text_and_is_ascii`, `An_invalid_parameter_name_is_rejected_before_it_can_reach_a_splat`; `PowerShellExecutionTests`: `A_hostile_string_reaches_a_script_file_unchanged_and_is_not_executed_in_pwsh` (and `_in_windows_powershell`, and `A_hostile_string_reaches_inline_code_unchanged_in_pwsh`) |
| Process action | `args` are separate argv entries. `{{name}}` is expanded **inside one entry** in a single pass (the result is never expanded again) and never changes the number of arguments. `file` and `workingDirectory` are not expanded. Arguments go to `ProcessStartInfo.ArgumentList`; on the paths that build a Windows command line themselves (session and elevated launch) each argument is quoted as the C runtime parses it | `Core/Commands/ActionHandlers.cs` (`PlaceholderExpander`, `ProcessActionHandler`), `Core/Execution/ProcessRun.cs`, `Platform.Windows/Sessions/WtsSessionNativeApi.cs` (`BuildCommandLine`) | `ProcessRunnerTests`, `SessionIpcTests` (`The_session_host_command_line_is_built_with_quoting_that_the_c_runtime_reads_back`), `CommandTemplateTests` |
| Built-in action | The built-in's own parameter definitions are bound again with `VariableBinder`; the provider receives typed values | `ActionHandlers.cs` (`BuiltinActionHandler.BindInputs`), `Platform.Windows/Actions/` | `BuiltinActionValidationTests`, `ActionCatalogTests` |
| Environment | `QA_VAR_<NAME>` and `QA_*` context variables are set as environment entries of the child, not through a shell | `Core/Execution/ProcessRun.cs` | `PowerShellExecutionTests` (`The_run_context_is_visible_to_the_script_in_pwsh`) |
| `runAs: user`, `user_elevated` | The request travels to the SessionHost (or the elevated launcher) as structured data and is started there with the same argv rules; PowerShell for the user is wrapped by the SessionHost the same way | `SessionHost/Execution/`, `Platform.Windows/Sessions/ElevatedLauncher.cs` | `UserContextExecutionTests`, `ElevatedLauncherTests`, `ElevatedEnvironmentTests` | 
| History, logs, output | Secret and undeclared variables are `***`; secret values of at least 4 characters are removed from output; commands with a secret parameter do not stream live output | `Core/History/RunRecord.cs`, `CommandEngine.cs` | `VariableBinderTests`, `RunApiTests` |

The rows for `runAs: user` and `user_elevated` (SessionHost, `ElevatedLauncher`, `WindowsElevatedNativeApi`) were checked through
their tests and the shared argv-quoting code only; their implementation is **n/r** and is part of the owner's Windows check
(`human_verify`, T311) and of a second reviewer's reading.

What D-012 does **not** cover, and the product says so (`docs/user/security.md`): a script that executes its own parameters
(`Invoke-Expression`), and a **program that interprets its arguments** (for example a process action with `cmd.exe /c {{x}}` or
`powershell.exe -Command {{x}}`). Constrain such values with `pattern` or `options`. Finding F-06 proposes a warning in the editor.

## 6. File permissions after a fresh install

### 6.1 Expected state

`qoffeeautomat service install` (run by the installer, `packaging/windows/QoffeeAutomat.iss`) calls `DataDirectoryProvisioner.Provision`.
The agent verifies it again on every start (`EnsureSecure`) and refuses to start when a directory is too open.

| Directory below `%ProgramData%\QoffeeAutomat` | Inheritance | SYSTEM | Administrators | Users | Owner |
|-----------------------------------------------|-------------|--------|----------------|-------|-------|
| (root), `config`, `secrets`, `data`, `logs` | off | full | full | none | Administrators |
| `scripts` | off | full | full | read and execute | Administrators |
| `data\tmp` (new, F-02), created by the agent | inherited from `data` | full | full | none | Administrators |

Program Files: the installer refuses an installation directory outside Program Files (the service starts every file in its
directory).

### 6.2 Checks the owner runs on a real machine (human_verify)

These are in the checklist; they cannot be verified from Linux:

```powershell
icacls "$env:ProgramData\QoffeeAutomat"          # and config, scripts, secrets, data, logs
icacls "$env:ProgramData\QoffeeAutomat\secrets\setup-token.txt"
(Get-Acl "$env:ProgramData\QoffeeAutomat\secrets").Owner
```

Expected: no entry for `BUILTIN\Users`, `Everyone` or `Authenticated Users` except `scripts` (read and execute); the owner is
`BUILTIN\Administrators`; the setup token file is readable by SYSTEM and Administrators only. Then, as a standard user, `type`
the setup token (must be denied), write to `scripts` (must be denied), and sign in to the UI with the token as an administrator.

### 6.3 Residual notes

* The `scripts` directory is readable by standard users on purpose (scripts of `runAs: user` run as the user). **Do not put
  secrets into scripts**; use parameters marked `secret`.
* A standard user can start another `QoffeeAutomat.SessionHost.exe` in their own session. The pipe check accepts the installed
  image, and the claimed session must be the real one, so that user can only receive requests meant for their own session,
  which run as themselves anyway.
* The pipe is created with `FirstPipeInstance` and a DACL that denies NETWORK; the client process id comes from the kernel
  (`GetNamedPipeClientProcessId`) and cannot be claimed in the hello message.

## 7. Findings register

| Id | Severity | Finding | Status |
|----|----------|---------|--------|
| F-01 | medium | **Lockout bypass by parallel requests.** `AccountService.LoginAsync` and `ChangePasswordAsync` read the lockout, then spent time in the PBKDF2 check, then counted the failure. A burst of parallel requests all passed the check before the first failure was counted, so an attacker could try far more than 5 passwords per window. | **Fixed**: one password check at a time (`_passwordGate` in `src/QoffeeAutomat.Agent/Security/Auth/AccountService.cs`). New test `A_burst_of_parallel_wrong_passwords_cannot_exceed_the_failure_limit` in `tests/QoffeeAutomat.Agent.Tests/Auth/AuthEndpointTests.cs`. Not built or run by the author of the fix. |
| F-02 | low | **Per-run files in the temp directory of LocalSystem.** The wrapper, inline code and the parameter file (Base64 of the variable values, possibly secret) of a `runAs: system` PowerShell run were created below `C:\Windows\Temp`, which standard users can write to, instead of an administrator-only directory. A random per-run directory name and the default ACL made exploitation unlikely, but secret values should not rely on that. | **Fixed**: `PowerShellOptions.TempRoot` is now `<data directory>\tmp` (`src/QoffeeAutomat.Agent/Hosting/AgentHost.cs`), which inherits the protected ACL of `data`. Old directories are removed by the existing sweep (`PowerShellRunner.SweepStale`). Verify on Windows with a run and `icacls`. |
| F-03 | low | `retention.auditLogDays` is validated and shown in the settings page but nothing reads it: the audit trail required by S09.8 is the run history (`runs.db`, `retention.runHistoryDays`), and management actions (command edits, pairing, sign-in) are only in the agent log. | Open. Proposed issue: remove the setting from the UI and the schema in 1.x, or add a real audit table; until then document that the run history is the audit log. |
| F-04 | low | S09.3 says a user-supplied certificate can replace the self-signed one; there is no setting, command or UI for it. | Open. Proposed issue: implement for 1.x, or correct the sentence in S09.3 in the same pull request. Not a vulnerability. |
| F-05 | low | Lockout key is the remote address. All local users share `127.0.0.1`, so one local user can lock sign-in out for 15 minutes; a LAN device can burn the active pairing code with five wrong attempts. Availability only, no disclosure. | Accepted (it is what S09.1 specifies). |
| F-06 | low | A process or PowerShell action may hand a value to a program that interprets it (`cmd /c`, `-Command`). The agent cannot know. | Accepted as documented. Proposed issue: editor warning when `file` is `cmd.exe`, `powershell.exe`, `pwsh.exe`, `wscript.exe`, `cscript.exe` or `mshta.exe` and an argument holds a placeholder without `pattern` or `options`. |
| F-07 | info | `GET /api/v1/logs` returns log lines unscrubbed (the diagnostics bundle scrubs). A search of all `LoggerMessage` definitions and log calls found no message that takes a token, password, cookie or variable value, so nothing sensitive is logged today. | Open, defence in depth. Proposed issue: pass `LogTail` entries through `SecretScrubber`. |
| F-08 | info | Import reads every entry through a counting reader, but 2000 entries of 2 MiB can still cost a few seconds of CPU. Requires a session. | Accepted. |
| F-09 | info | The `Host` header is not validated (DNS rebinding); no impact because of the cookie and token requirements above. | Accepted. Proposed hardening: reject unknown hosts on the loopback listener. |
| F-10 | info | `/healthz` is unauthenticated on the LAN listener and reveals the version. | Accepted. |
| U-1 | low | **Self-update trusts the GitHub release.** The checksum comes from the same release as the installer, so it detects a damaged or swapped download but not a manipulated release; for that the Authenticode signature is needed, and it is checked only while the running build is signed (an unsigned build cannot tell a good signature from none). An attacker who can publish a release of the project can therefore run code as SYSTEM on every PC that has the check on. Releases are immutable and built by a public workflow with provenance (S12, S13, D-020). The check can be switched off in the settings; `agent.update` runs only when an authorized client asks for it. | Accepted, documented in `docs/user/updates.md`. Re-check when signing is in place: then an unsigned installer must be refused even by an unsigned build. |
| U-2 | info | The update check is the only outbound request of the agent. It tells GitHub the address of the PC and the agent version (`User-Agent: QoffeeAutomat/<version>`); no other identifier is sent. | Accepted, documented in `docs/user/updates.md` and `docs/user/security.md`; off-able. |

No open finding has severity high.

## 8. Dependencies

Runtime dependencies are few: the ASP.NET Core and .NET libraries, `Microsoft.Data.Sqlite`, `System.Management`,
`System.Security.Cryptography.ProtectedData` and friends (`Directory.Packages.props`); the UI adds MudBlazor and BlazorMonaco
(Monaco editor, bundled locally). Test, docs and tooling packages are not shipped. The Home Assistant integration has no
runtime dependency outside Home Assistant.

* **Licences.** The packages above are published under MIT or Apache-2.0 to the best of the author's knowledge; this was
  **not** verified with a tool for this page. `dependency-review.yml` denies GPL, AGPL and SSPL for every pull request; the
  SBOM of a release (`sbom.spdx.json`) lists the licences GitHub resolved. The owner checks the SBOM of the release candidate
  (checklist).
* **Known vulnerabilities.** Not scanned for this page. Commands for the release candidate:

  ```bash
  dotnet list package --vulnerable --include-transitive
  python -m pip install pip-audit && pip-audit -r requirements-dev.txt -r requirements-docs.txt
  ```

  plus the open alerts of Dependabot, CodeQL and Scorecard in the GitHub Security tab. Any alert of severity high or critical
  that affects a shipped component blocks the release.

## 9. Re-running this review

Update this page in the pull request that changes any row of section 3: a new route (section 4), a new action type or a new
way a variable reaches a process (section 5), a new file the service writes (section 6), a new secret or log message.
Check that `RouteProtectionTests` still enumerates the new route.

## 10. The central hub (T919, PLAN.md S20)

The hub (`src/QoffeeAutomat.Hub`, D-024) is the most sensitive component of the project: a hub administrator can have code run as
SYSTEM on every enrolled PC, and the hub holds the credentials, the signing key and the metric history of the fleet. This section
maps **every item of "Hub security" in S20** to the code that implements it and the tests that pin it, then lists the threats of
each trust boundary with their mitigations and the findings of the review. Paths are relative to the repository root; test names
are method names in the test project named in the row. The end-to-end scenario `tests/e2e/hub/` (job `e2e-hub` of `e2e.yml`)
exercises the same controls against the real container image.

### 10.1 Status of this review

| | |
|---|---|
| Review date | 2026-11-20 (branch `agent/m9-central-hub`) |
| Method | Reading the code of every network boundary of the hub (listeners, ingress gate, authentication handler, enrollment, agent link, fleet link, management tunnel, Prometheus export, secret storage) and the agent side of the hub link (pinning, signature check, layer, local guard), then comparing it with the tests and with S20. A sample, **not** a line-by-line audit of the hub code. |
| Not done by the author of this page | Running the build, the tests, `tests/e2e/hub`, CodeQL, a dependency or image scan, or a check on a real Home Assistant Supervisor. These are owner actions (section 10.7). |
| Result | **No finding of severity high.** Two findings of severity medium (H-01 test gap, H-02 lockout race) and five of severity low or info stay open (section 10.8). Neither medium finding is a remote bypass of authentication: H-02 raises the number of password guesses per time, which the cost of PBKDF2 (600 000 iterations) still bounds. |

Severity scale and the meaning of "read", "tests" and "n/r" are those of section 1.

### 10.2 Assets, actors, trust boundaries

| Asset | Where | Protected by |
|-------|-------|--------------|
| Code execution on every enrolled PC | published commands and scripts, bulk runs, the management tunnel | roles (S20.1), signature of the desired state (S20.4), the agent's own validation, the system-script rule (S20.7), audit (S20.2) |
| Signing key of the hub (ECDSA P-256) | secret store in `/data/secrets` | file modes, secret store (S20.3); a leak means "create a new hub" (`docs/user/hub/security.md`) |
| TLS private key of the hub | secret store, or the operator's certificate file | same; agents and Home Assistant pin the fingerprint |
| Agent credentials, fleet tokens, API tokens, enrollment tokens, user passwords | `hub.db` | stored as SHA-256 hashes (tokens) or PBKDF2-SHA256 (passwords), never in plain text (S20.3) |
| Metric history, inventory, configuration library, audit log | `hub.db` (SQLite) | database file mode 0600 in a 0700 directory; role checks on every read |
| Fleet link of Home Assistant | `/hub/v1/fleet` | fleet token with a scope (all agents or tags) |

| Actor | Can reach | Must not be able to |
|-------|-----------|---------------------|
| Anybody on the network of the hub | port 8490 (HTTPS): `/healthz`, the sign-in and setup routes, the enroll route, the agent link and pairing routes (each with its own secret) | read or change anything without a credential; guess a token or password cheaply; learn more than the version |
| Holder of an enrollment token | `POST /hub/v1/enroll` until the token is used up, expired or revoked | enroll more agents than allowed; skip the approval; read configuration |
| A PC with an agent credential | the agent link of its own id | act as another agent; keep working after removal; change what other agents get |
| Holder of a fleet token (Home Assistant) | the fleet link within the scope of the token | reach an agent outside the scope; use the management API of the hub |
| Hub user with the role viewer, operator or admin | the hub API and the tunnel as the role allows (section 10.4, RBAC) | exceed the role; publish system scripts to the fleet as an operator; use a token for a password change |
| Home Assistant Supervisor (app mode) | the ingress listener, with the Home Assistant user in `X-Remote-User-*` | be impersonated by another container |
| Other process or container on the host or on the hassio network | whatever is published or on the bridge | reach the ingress listener as the Supervisor; read `/data` |
| Network attacker between hub and PC, or hub and Home Assistant | the traffic | read or change it undetected (pinned certificate), replay an old desired state |
| Supplier of an image or a dependency | the build | put code into the release unnoticed |

Trust boundaries, from outside to inside:

```text
 browser / API client --HTTPS 8490--> [ hub listener ] -- session or API token -- roles --> hub API
 Home Assistant --HTTPS 8490, pairing code / fleet token--> [ fleet link ] -- scope --> agents
 PC (agent) --WSS 8490, enrollment token, then credential--> [ agent link ] <-- signed desired state, tunnel requests
 Supervisor --HTTP 8099 from 172.30.32.2 only--> [ ingress listener ] -- X-Remote-User-* --> role of ingress_role
 hub --> SQLite hub.db + /data/secrets (0700 / 0600) --> signing key, TLS key, hashes
```

### 10.3 Coverage of S20 "Hub security"

| S20 | Control | Implementation | Tests | Reviewed | Result |
|-----|---------|----------------|-------|----------|--------|
| 1 | Authentication mandatory on every route except setup, sign-in, `/healthz` and the routes with their own secret | `src/QoffeeAutomat.Hub/Security/HubAuthentication.cs` (one handler: ingress headers on the ingress listener, then `Authorization: Bearer`, then the session cookie), `SecurityServices.cs`, `Api/HubApi.cs` (`/api/v1` group with a policy per operation), `Metrics/Prometheus/PrometheusEndpoint.cs` | `Hub.Tests` `RouteProtectionTests`: `The_anonymous_routes_are_exactly_the_documented_setup_and_login_routes`, `Every_other_operation_names_an_authorization_policy`, `Every_other_operation_answers_401_without_credentials`, `A_made_up_session_or_a_made_up_token_is_no_credential`, `The_openapi_document_and_the_hub_identity_need_authentication_but_healthz_does_not`; e2e `test_hub_api_needs_a_session_and_the_anti_forgery_header` | read | ok |
| 1 | Local accounts, password rules of S09 (10 to 256 characters, PBKDF2-SHA256, 600 000 iterations) | `Security/UserService.cs`, `UserStore.cs`, `HubAuthOptions.cs`; hash in `src/QoffeeAutomat.Core/Security/PasswordHasher.cs` (shared with the agent) | `PasswordHasherTests` (Agent.Tests, shared class); the rules of the hub itself have no dedicated test | read | ok; see **H-01** |
| 1 | Setup token, owner-only file, constant-time compare, deleted after setup | `Security/SetupTokenService.cs` (hash both sides, `FixedTimeEquals`), `SetupService.cs`, `Hosting/HubPaths.cs` (`setup-token.txt`) | e2e `Hub.set_up` in `hub_driver.py` (the whole scenario depends on it); no unit test of the hub service | read | ok; see **H-01** |
| 1 | Session cookie `HttpOnly`, `SameSite=Strict`, `Secure` over HTTPS; server-side sessions (SHA-256 of the id, idle 8 h, lifetime 7 d, at most 256) | `Security/HubAuthentication.cs` (`HubSessionCookie`), `HubSessionStore.cs` | none dedicated | read | ok; see **H-01** |
| 1 | CSRF: header `X-QoffeeAutomat-Csrf` on every state change by session or ingress, `Origin` must equal the request origin; API tokens are exempt | `Security/CsrfHeaderFilter.cs` | `RoleMatrixTests.API_tokens_of_every_role_follow_the_same_matrix_without_the_anti_forgery_header` (token side); e2e `test_hub_api_needs_a_session_and_the_anti_forgery_header` (session side, 403 `csrf_header_missing`) | read | ok; see **H-01** |
| 1 | Roles admin, operator, viewer; API tokens carry a role and an optional expiry | `Security/HubRoles.cs`, `ApiTokenService.cs`, `Api/Users/TokenEndpoints.cs`, `UserEndpoints.cs` | `RoleMatrixTests`: `The_matrix_has_a_row_for_every_protected_operation_and_the_policies_agree_with_it`, `Every_role_may_call_exactly_the_operations_of_the_matrix`, `API_tokens_of_every_role_follow_the_same_matrix_without_the_anti_forgery_header`, `An_API_token_cannot_change_a_password_not_even_that_of_an_administrator`, `Each_policy_lets_in_its_role_and_every_more_powerful_one_and_nobody_else`, `Roles_are_ranked_and_only_the_three_roles_are_valid` | read | ok |
| 1 | Ingress: the Supervisor's user is trusted only on the ingress listener, only from the Supervisor address, and mapped to `ingress_role` | `Hosting/IngressGateMiddleware.cs` (runs before the forwarded-header rewrite), `Hosting/HubSettings.cs` (`QOFFEE_HUB_INGRESS_*`, default `172.30.32.2`, role default `admin`), `HubListenerSetup.cs`, `Security/HubAuthentication.cs` (`ingress` branch, names prefixed `ha:`); `packaging/hub/ha-app/rootfs/run.sh`, `config.yaml` (`panel_admin: true`) | `IngressTests`: `Gate_middleware_rejects_requests_from_non_supervisor_addresses`, `Ingress_listener_authenticates_supervisor_and_rejects_other_addresses`, `Settings_resolve_ingress_environment_variables`, `Settings_reject_invalid_ingress_role_or_port_collision`; `TrustedProxyTests` (all) | read | ok; see **H-05**, **H-06** |
| 2 | Audit log of every change, publish and command run, with user, time and affected agents | `Security/AuditMiddleware.cs` (every non-GET request below `/api/v1`, denied ones too), `AuditLog.cs` (200 000 entries or 365 days), `Fleet/Actions/FleetActionService.cs` (`bulkRunFinished`), `Tunnel/TunnelEndpoints.cs` (target `agent:ID:tunnel:METHOD path`) | `EnrollmentTests`: `Enrollment_events_are_audited_and_the_audit_log_holds_no_secret`, `Changes_to_agents_and_tokens_are_audited_with_the_affected_agent`; `ConfigApiTests.Every_change_is_audited_with_the_profile_or_document_and_the_agents_that_got_a_new_generation`; `FleetRunTests.A_run_is_written_to_the_audit_log_with_the_user_the_agent_and_the_command`; `FleetActionTests.A_bulk_run_is_recorded_in_the_audit_log_when_it_starts_and_when_it_ends`; `FleetScopeTests.A_denied_run_is_audited_as_denied_and_nothing_reaches_the_agent`; `TunnelProxyTests.Calls_that_change_state_are_audited_with_the_user_the_agent_and_the_path`; e2e `test_audit_log_records_publish_and_fleet_actions` | read | ok; sign-ins and user changes are audited but not pinned by a test (**H-01**) |
| 3 | Agent credentials, fleet tokens, API tokens and enrollment tokens stored as hashes | `Agents/AgentSecrets.cs` (32 random bytes, SHA-256), `Enrollment/EnrollmentTokenService.cs`, `Security/ApiTokenService.cs`, `Fleet/FleetPairingService.cs` (`FixedTimeEquals` on the code), `Fleet/FleetTokenService.cs` | `EnrollmentTests.Enrolling_issues_a_credential_and_the_hub_keeps_only_its_hash`; `FleetPairingTests`: `The_hub_stores_only_the_hash_of_a_fleet_token`, `Fleet_tokens_are_listed_with_their_scope_and_use_and_never_with_their_secret`; `PrometheusTests.The_export_contains_no_secret` | read | ok |
| 3 | Signing key and TLS key protected at rest in the data volume | `Security/HubSigningKeyStore.cs`, `HubCertificateProvider.cs`, `src/QoffeeAutomat.Core/Secrets/SecretStore.cs` and `FileKeyProtector.cs` (key file mode 0600), `Hosting/HubDataDirectory.cs` (directories 0700, secret files 0600; a mode that is too open is tightened at start) | `HubStartupTests`: `The_secrets_are_not_stored_as_plain_text`, `Directories_get_mode_0700_and_secret_files_mode_0600_on_linux`, `A_damaged_signing_key_stops_the_start_and_is_not_replaced`; `HubStorageTests.The_database_file_is_only_accessible_by_its_owner`; `Core.Tests` `SecretStoreTests`, `FileKeyProtectorTests` | read | ok; the key file lies in the same volume (**H-07**) |
| 4 | Agents verify the signature of every desired state and validate every document themselves | `src/QoffeeAutomat.Contracts/Hub/DesiredStateSigner.cs` (signs and verifies), `src/QoffeeAutomat.Agent/Hub/ConfigSync/ConfigSyncService.cs` (key pinned at enrollment, see `HubEnrollment.cs`), `src/QoffeeAutomat.Core/Config/HubLayer/HubConfigLayer.cs`, `HubDocumentReader.cs` | `Agent.Tests` `ConfigSyncProtocolTests`: `A_desired_state_with_a_bad_signature_changes_nothing_and_reports_failed`, `A_desired_state_that_was_signed_for_another_agent_or_changed_after_signing_is_refused`, `A_document_whose_hash_is_not_the_signed_hash_changes_nothing_and_reports_failed`, `A_document_that_was_not_asked_for_fails_the_generation`, `One_invalid_document_fails_the_generation_and_the_valid_ones_are_not_applied`, `A_generation_that_is_not_newer_than_the_applied_one_is_ignored_and_needs_no_documents`; `HubLayerTests`: `A_bad_document_fails_the_generation_and_the_previous_generation_stays_active`, `A_generation_that_is_not_newer_than_the_applied_one_is_refused` | read | ok |
| 5 | Enrollment tokens expire, are limited in uses and can require approval | `Enrollment/EnrollmentTokenService.cs`, `EnrollmentService.cs`, `EnrollEndpoint.cs`, `Agents/AgentRegistry.cs` | `EnrollmentTests`: `A_token_cannot_be_used_after_it_has_expired`, `A_token_cannot_be_used_beyond_its_maximum_number_of_uses`, `A_token_that_is_unknown_or_revoked_enrolls_nobody`, `An_agent_of_an_approval_token_cannot_open_the_agent_link_before_it_is_approved`, `A_pending_agent_that_asks_again_does_not_use_up_another_use_of_the_token`, `A_rejected_agent_gets_no_credential_and_cannot_be_approved_afterwards`; e2e `test_both_agents_are_enrolled_and_connected` (a token with two uses is used up by two agents) | read | ok |
| 6 | Enrollment endpoint and logins are rate limited | `Security/LoginRateLimiter.cs` (5 failures per address and 30 overall in 15 minutes, lockout 15 minutes, one scope each for sign-in, setup, password change and pairing), `Enrollment/EnrollEndpoint.cs`, `Fleet/PairEndpoint.cs`, `Api/Users/AuthEndpoints.cs` | `EnrollmentTests.Failed_enroll_requests_are_rate_limited_per_address`; the wrong-code tests of `FleetPairingTests`; none for sign-in | read | enroll and pairing ok; sign-in has no test (**H-01**) and a race (**H-02**) |
| 7 | Publishing a profile with `runAs: system` scripts needs `admin` and shows the affected agents first | `Config/ConfigEndpoints.cs` (`publishProfile` and `previewProfile` with `requiresAdmin`, `systemScripts`, `agents`), `Config/SystemScripts.cs`, `ConfigService.cs` | `Hub.Tests` `Config`: `A_profile_with_system_scripts_can_only_be_published_by_an_administrator_and_the_preview_says_so`, `A_profile_with_a_command_that_runs_code_as_system_can_only_be_published_by_an_administrator`, `Only_commands_that_run_code_as_system_make_a_profile_need_the_administrator`, `A_script_that_a_system_command_of_another_profile_runs_cannot_be_replaced_without_the_administrator`, `Reading_is_open_to_every_role_and_changing_the_library_drafts_and_assignments_needs_an_administrator` | read | ok |

### 10.4 Threats and mitigations per boundary

| Boundary | Threat | Mitigation | Evidence | Residual risk |
|----------|--------|------------|----------|---------------|
| **Enrollment tokens** | A leaked or guessed token enrolls a rogue PC that then receives the configuration and variables of the fleet | 32 random bytes; only the hash is stored; expiry, a use limit, optional approval, revocation; an agent that needs approval is `pending` and cannot open the link until approved; failed enrollments are rate limited per address; the tags come from the token, so a rogue PC gets only the profiles of those tags; a token that is used up stops working | `EnrollmentTests` (rows of 10.3); e2e `test_both_agents_are_enrolled_and_connected` | A token with many uses and no approval is as good as its secrecy. Documented: one use, a short expiry, approval for PCs you do not stand next to (`docs/user/hub/security.md`). Hub variables and documents reach an approved PC, so they must hold no secrets. |
| **Enrollment tokens** | Enrolling an existing agent id again to take over its identity | A valid token is needed; the old credential ends; with an approval token the agent waits again | `EnrollmentTests`: `Enrolling_the_same_agent_id_again_replaces_its_credential_only_with_a_valid_token`, `Enrolling_an_enrolled_agent_with_an_approval_token_makes_it_wait_again_and_ends_its_old_credential` | none |
| **Agent credentials** | A stolen credential lets an attacker pose as one PC | 32 random bytes, only the hash on the hub, the credential on the PC in its secret store (DPAPI or key file); revocation by removing the agent (401 `revoked`, the agent deletes its credential and its hub layer); rotation keeps two valid credentials until the new one is used | `EnrollmentTests.Removing_an_agent_revokes_its_credential_and_later_connections_are_answered_with_401_revoked`; `RotationTests` (all six); e2e `test_removed_agent_loses_its_hub_layer` | The attacker sees what the hub sends to that PC (configuration, variables) until the credential is rotated or the agent removed. Accepted. |
| **Fleet tokens** | A stolen token of Home Assistant reads the state of the fleet or runs commands | The token carries a scope (all agents or tags); a request outside the scope answers as forbidden and is audited; state and events outside the scope are never sent; a token is revocable and listed with its last use and open connections; pairing uses a six-digit code that lives five minutes, is accepted exactly once, is compared in constant time and is rate limited | `FleetScopeTests` (all), `FleetPairingTests`; `test_pairing_with_a_wrong_code_is_refused` and `test_the_client_stops_retrying_when_the_token_is_revoked` in `tests_integration/test_fleet.py` | A token of scope `all` can run every command that a PC exposes to Home Assistant. That is the purpose of the fleet link; use tags to narrow it. |
| **TLS pinning** | A man in the middle between PC and hub, or between Home Assistant and hub, changes the configuration or steals a credential | The enrollment string carries the SHA-256 fingerprint of the hub certificate and the agent pins it for enrollment and for the link; Home Assistant pins it for pairing and the fleet link (the config flow shows it for comparison); TLS 1.2 and 1.3 only; the signing key is pinned as well, so even a TLS-terminating proxy cannot alter a desired state | `Agent.Tests` `HubLinkTests`: `A_hub_certificate_that_does_not_match_the_pinned_fingerprint_is_refused_at_enrollment`, `A_hub_certificate_that_does_not_match_the_pinned_fingerprint_is_refused_on_the_link`, `A_hub_without_a_pinned_fingerprint_is_checked_by_the_system_and_a_self_signed_certificate_fails`; `HubStartupTests`: `A_restarted_hub_still_answers_to_a_client_that_pinned_the_old_fingerprint`, `A_client_that_pins_another_certificate_is_refused`, `A_certificate_of_the_operator_is_used_instead_of_a_self_signed_one`; `test_pairing_is_only_sent_to_the_pinned_certificate`; e2e `test_hub_is_set_up_with_a_self_signed_certificate` and the fingerprint comparison in the `hub_entry` fixture | Behind a proxy (`--tls none`) the certificate is validated by the system, not pinned; the hub refuses to start in that mode without `--trusted-proxy` (`HubSettings`). The first contact of a new PC trusts the fingerprint in the string (trust on first use through a channel the administrator controls). A replaced hub certificate invalidates every enrollment string. |
| **Ingress listener and Supervisor peer restriction** | Another container or a LAN device sends `X-Remote-User-Name` to get the role of a Home Assistant user | The headers are read only on the ingress port; there the peer must equal the configured Supervisor address (default `172.30.32.2`), checked in `IngressGateMiddleware` before any forwarded header can rewrite the address, and again in the authentication handler; a wrong peer gets 403 `ingress_forbidden`; the main port ignores the headers; Home Assistant shows the panel to administrators only (`panel_admin`) and the role is the option `ingress_role` | `IngressTests` (all four), `TrustedProxyTests.A_request_from_any_other_address_cannot_forge_them` | The listener binds all addresses of the container, so the peer address is the only gate. Another app with `NET_RAW` on the hassio bridge could spoof the Supervisor address; that is the trust model of every ingress app (**H-06**). The role is the same for every Home Assistant administrator and defaults to `admin` (**H-05**). The path cannot be exercised without a real Supervisor (section 10.7). |
| **Management tunnel isolation** | A hub user, or someone who takes over the hub, uses the tunnel to run or change things on a PC beyond their role | The hub maps its role one to one to the tunnel role and applies the rules, and the agent applies them again and has the last word; paths must stay below `/api/v1`; the tunnelled request runs through the agent's own pipeline (middleware, authorization, endpoint filters), so no rule of its API is bypassed, and the identity comes from in-process state that no client can send; the account, the password and the hub link of the agent are out of reach for every role; the local switch of the agent turns the feature off; bodies, concurrency and streams are bounded; every call is audited on both sides | `TunnelAccessTests` (all nine); `TunnelProxyTests` (for example `A_viewer_cannot_write_and_nothing_reaches_the_agent`, `A_path_that_tries_to_leave_api_v1_is_a_bad_request`, `The_account_of_the_agent_is_refused_on_the_hub_even_for_an_administrator`); `TunnelTests` (`The_agent_keeps_its_account_and_its_hub_link_to_itself_whatever_the_hub_asks`, `Paths_outside_api_v1_are_rejected_by_the_agent`); `HubLinkTests.The_management_tunnel_is_announced_only_while_the_local_switch_allows_it`; e2e `test_command_runs_through_the_management_tunnel` (run source `hub`, variables validated by the agent, a viewer token refused) | An administrator of the hub can change the commands of an agent through the tunnel unless `local_config` is `locked` or the local switch is off. That is the purpose of the feature. |
| **RBAC and CSRF** | A viewer or operator escalates; a web page of another origin makes a signed-in browser change state | One role matrix, enforced by named policies and pinned by a test that compares the routes with the matrix; operators may publish and run but need `admin` for system scripts; an API token cannot change a password; state changes by session or ingress need the anti-forgery header and a same-origin `Origin`; session cookies are `SameSite=Strict`; tokens are exempt because a browser never attaches them | `RoleMatrixTests` (all six), `RouteProtectionTests`; e2e `test_hub_api_needs_a_session_and_the_anti_forgery_header` and `test_prometheus_export_needs_a_read_only_token` (a session and an operator token are refused at `/metrics`) | The `Origin` comparison uses the host that the request carries; behind ingress this depends on the proxy of the Supervisor and has to be checked on a real Supervisor (section 10.7). |
| **SQLite and secrets storage** | Another local user or container reads `hub.db` or the secrets; a damaged or newer database corrupts the hub; SQL injection | `/data` is a volume owned by the application user (mode 0700) with files 0600, and the hub tightens a mode that is too open at start; the database migrates forward in numbered steps, each in a transaction, and a database of a newer hub is refused and left alone; the SQL of the services uses named parameters (`@hash` and so on), never a value spliced into the text; secrets are in the secret store, not in the database; a damaged signing key stops the start and is never replaced silently | `HubStorageTests` (including `A_failing_step_is_rolled_back_completely`, `A_database_of_a_newer_hub_is_refused_and_left_alone`, `The_database_file_is_only_accessible_by_its_owner`); `HubStartupTests` (as in 10.3) | A backup of `/data` holds every secret of the hub including the key file; protect it like the volume (`docs/user/hub/backup.md`). On Windows the hub relies on the ACL of the parent directory. |
| **Supply chain and container hardening** | A manipulated base image, dependency, action or release | The image runs as a non-root user (`USER $APP_UID`) with `/data` as an owned volume; the compose example drops all capabilities and sets `no-new-privileges`; TLS is the default; the Home Assistant app drops root with `setpriv` before it starts the hub and maps only `ssl`; the release builds the images per architecture on native runners, smoke-tests them (`packaging/hub/test-image.sh`: not root, volume owner, health) and attests build provenance, and the workflow verifies the attestation before it publishes; all actions are pinned by SHA (AGENTS.md); the product makes no request to an external host and the UI has local assets only; dependency review, CodeQL, Scorecard and Dependabot run for the repository | `.github/workflows/release.yml` (jobs `hub-images` and `hub-manifests`), `ci-hub-image.yml`, `packaging/hub/Dockerfile`, `docker-compose.yml`, `ha-app/config.yaml`, `ha-app/rootfs/run.sh` | Base images are referenced by tag, not by digest, and Dependabot does not watch the Dockerfiles (**H-04**). The root file system is writable (**H-03**). The app image starts as root for the `chown` of `/data` and then drops privileges. |

### 10.5 Authentication and input of the hub's routes

| Route group | Auth | Input and limits | Notes |
|-------------|------|------------------|-------|
| `/healthz` | none (by design) | none | Returns status and version only. Accepted, as F-10 of the agent. |
| `/api/v1/auth/setup`, `/auth/login` | anonymous | JSON body with a size limit (`AuthEndpoints.MaxBodyBytes`) | Setup exists only while no account exists and needs the token; both are attempt limited and need the CSRF header. |
| `/api/v1/auth/logout`, `/auth/me`, `/auth/password` | session | bodies limited by `HubApiResults.ReadBodyAsync` | The current password is needed to change one, and the attempts are limited. |
| `/api/v1/users`, `/tokens`, `/audit` | admin | same | Passwords are validated by `UserService`; a token secret is returned once, with `Cache-Control: no-store`, and never listed. |
| `/api/v1/enrollment-tokens`, `/agents` (changes), `/fleet-pairings`, `/fleet-tokens` | admin (changes), every role (reads of agents) | tags 1 to 32 characters of a-z, 0-9, `.`, `_`, `-`, at most 16 | Secrets are returned once and never listed. |
| `/api/v1/library`, `/profiles`, `/assignments`, `/rollouts`, `/agents/{id}/config-status` | admin (library, drafts, assignments), operator (publish, roll back, push), every role (reads) | documents validated by the hub and again by every agent; ids and kinds by pattern | System scripts raise the publish to admin (S20.7). |
| `/api/v1/agents/{id}/agent-api/**` (tunnel) | viewer (GET), operator (other methods), then the agent decides | path below `/api/v1`, body up to 900 KiB, bounded streams | Section 10.4. |
| `/api/v1/fleet-actions/**`, `/api/v1/agents/{id}/run` | operator (bulk run, run, cancel), admin (update orchestration) | at most 200 agents, 4 actions at once, one update | Variables are bound by the agent (D-012), never spliced. |
| `/api/v1/metrics/query`, `/agents/{id}/metrics`, `/inventory` | every role | at most 50 agents per query, at most 5000 points per series | Read only. |
| `/metrics` (Prometheus) | API token with the role viewer only; no session, no other role | none | The output holds no secret (`PrometheusTests.The_export_contains_no_secret`). |
| `/hub/v1/enroll`, `/hub/v1/agent`, `/hub/v1/pair`, `/hub/v1/fleet` | enrollment token, agent credential, pairing code, fleet token | rate limited per address (enroll, pair); frames bounded; a frame that is not JSON closes the link with 4400 (`FleetLinkTests`) | Not under the session policy by design; `HubHost` maps them separately. |

### 10.6 What the end-to-end test pins

`tests/e2e/hub/test_hub_e2e.py` runs the real hub image, two agents and Home Assistant and checks, from outside, the controls
that the unit tests cover in process:

* sign-in is required (401 without a session, also for `/metrics`) and a state change without the anti-forgery header is
  refused with 403 `csrf_header_missing`;
* an enrollment token with two uses enrolls exactly two agents, gives both its tag, and is then inactive;
* the roll-out reaches both agents as `up_to_date`, the agent runs the hub layer, a viewer token cannot start a run through the
  tunnel (403), and an operator's run has the source `hub` while the agent validates the variables itself (422 for a bad value);
* the Prometheus export refuses a session and an operator token and serves a viewer token;
* Home Assistant pairs through the fingerprint of the hub and a pairing code, and the result is one fleet token of scope `all`;
* `local_config: locked` makes the agent refuse a local write with 403 `local_config_locked`;
* removing an agent deletes its hub layer, ends its link and its Home Assistant entities, and leaves the other agent alone;
* the audit log holds the publish, the start and the end of the bulk run, and the removal.

### 10.7 Owner actions

These cannot be done by the author of this page and belong to the release of the hub (`docs/maintainers/releasing.md`):

1. Run `pytest tests/e2e/hub` once locally and let the job `e2e-hub` pass in CI; read its logs for a line that names a secret.
2. Run `dotnet test` for `QoffeeAutomat.Hub.Tests`, `QoffeeAutomat.Agent.Tests` and `QoffeeAutomat.Core.Tests`.
3. Open the hub as a panel in a real Home Assistant (Supervisor): check that an administrator is signed in with `ingress_role`,
   that a change in the UI (publish, create a token) is accepted with the `Origin` that the proxy of the Supervisor passes on, and that
   a request with the header `X-Remote-User-Name` from another host or app to port 8099 answers 403 `ingress_forbidden`.
4. Scan the published image with a scanner of your choice and read the open alerts of Dependabot, CodeQL and Scorecard. A
   finding of severity high or critical in a shipped component blocks the release.
5. Verify the attestation of the image:
   `gh attestation verify oci://ghcr.io/<owner>/qoffeeautomat-hub:<version> --repo <owner>/QoffeeAutomat`.

### 10.8 Findings register of the hub

| Id | Severity | Finding | Status |
|----|----------|---------|--------|
| H-01 | medium | **Test gap for the sign-in controls of the hub.** The code of S20.1 and S20.6 is implemented and was read (`SetupService`, `SetupTokenService`, `LoginService`, `LoginRateLimiter`, `HubSessionStore`, `HubSessionCookie`, `CsrfHeaderFilter`, `UserService`, the audit of sign-ins and user changes), but `QoffeeAutomat.Hub.Tests` has no test that pins the setup token, the password rules, the lockout, the cookie flags, the CSRF and `Origin` rules for sessions, or the audit of user changes. The agent has such tests (`AuthEndpointTests`, `SetupTokenTests`), the routes are covered by `RouteProtectionTests` and `RoleMatrixTests`, and `tests/e2e/hub` pins the CSRF header and the 401. | Open. Proposed issue: port `AuthEndpointTests` and `SetupTokenTests` of the agent to the hub (`tests/QoffeeAutomat.Hub.Tests/Security/`) before the next minor version. It is a missing test, not a known vulnerability. |
| H-02 | medium | **Lockout race on sign-in and password change.** `LoginService.LoginAsync` and `AuthEndpoints.ChangeOwnPasswordAsync` read the lockout, then spend the time of the PBKDF2 check, then count the failure, so a burst of parallel requests passes the lockout check before the first failure is counted. This is finding F-01 of the agent, which was fixed there with one password check at a time (`_passwordGate` in `AccountService.cs`); the hub does not have the same fix. The rate of guesses stays bounded by the CPU cost of PBKDF2 (600 000 iterations), and a password of the length the rules ask for cannot be guessed at that rate, so the finding is medium and not high. | Open. Proposed fix: serialize the password check as in `AccountService`, or reserve an attempt before the check, with a test like `A_burst_of_parallel_wrong_passwords_cannot_exceed_the_failure_limit`. Not changed by this review; nothing was built or run. |
| H-03 | low | The compose example and the image keep a writable root file system; only `no-new-privileges` and `cap_drop: ALL` are set. | Open. Proposed issue: try `read_only: true` with a `tmpfs` for `/tmp` and document it if the hub runs. |
| H-04 | low | The Dockerfiles (`packaging/hub/Dockerfile`, `tests/e2e/agent.Dockerfile`) name the base images by tag (`mcr.microsoft.com/dotnet/...:10.0`) and `dependabot.yml` has no `docker` ecosystem, so a changed tag is neither pinned by digest nor proposed as an update. The release attests the build provenance of the result. | Open. Proposed issue: pin the digests and add the `docker` ecosystem for `/packaging/hub`, `/packaging/hub/ha-app` and `/tests/e2e`. |
| H-05 | low | In the Home Assistant app every Home Assistant administrator gets the same hub role, and the default is `admin`, which includes publishing system scripts to the fleet. | Accepted and documented in `docs/user/hub/security.md`: set `ingress_role` to `operator` or `viewer` where that is too much. |
| H-06 | low | The ingress listener serves plain HTTP on all addresses of the container, and the only gate is the source address of the connection. An app with `NET_RAW` on the same Docker bridge could forge the address of the Supervisor. | Accepted: it is the trust model of Home Assistant ingress for every app. The port is not published to the host. |
| H-07 | info | The key file that protects the secret store lies in the same volume as `secrets.json`. Anybody who can read `/data` or a backup of it can impersonate the hub. | Accepted, documented in `docs/user/hub/security.md` and `backup.md`. An external key store is a possible later change. |

No open finding has severity high. The release of the hub (the last acceptance item of T919) needs the owner actions of section
10.7. The two medium findings do not block it under the severity rule of section 1, but H-02 is cheap to fix and should be fixed
first.

### 10.9 Re-running the hub review

Update this section in the pull request that changes any row of section 10.3: a new route group (section 10.5), a new secret or
a new way one is stored, a new listener or trust rule (ingress, proxies), a new way for a request to reach an agent (tunnel, fleet
action), a new role rule, or a new image, base image or workflow step of the hub release. Check that the hub's
`RouteProtectionTests` and `RoleMatrixTests` still enumerate the new route, and extend `tests/e2e/hub` for anything that crosses a
container boundary.

