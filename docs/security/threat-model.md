# QoffeeAutomat threat model and security review (1.0)

Task T606 (PLAN.md S09, S13, S19). This page maps **every item of the security model S09** to the code that implements it and the
tests that pin it, lists the result of the review for the 1.0 release, and keeps the register of findings.

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
| 1 | PBKDF2-SHA256, at least 600 000 iterations | `PasswordHasher.cs` (`Iterations = 600_000`, weaker hashes never accepted, upper bound against forged hashes, `FixedTimeEquals`); length policy in `PasswordService.cs` and `AuthOptions.cs` (10 to 256 characters) | `PasswordHasherTests`, `AuthEndpointTests`: `The_password_is_stored_as_a_hash_in_the_secret_store_and_never_in_plain_text` | read | ok |
| 1 | Session cookie `HttpOnly`, `SameSite=Strict`, `Secure` on HTTPS | `SessionAuthentication.cs` (`SessionCookie.Options`); server-side `SessionStore.cs` (256-bit id, only the SHA-256 is kept, idle 8 h, lifetime 7 d, at most 32 sessions) | `AuthEndpointTests`: `The_session_cookie_is_http_only_same_site_strict_and_secure_on_https`, `Logout_ends_the_session_and_clears_the_cookie`; `SessionStoreTests` | read | ok |
| 1 | CSRF header and `Origin` check | `CsrfHeaderFilter.cs` (all methods except GET, HEAD, OPTIONS, TRACE; `Origin`, when present, must equal the request origin) | `AuthEndpointTests`: `State_changing_requests_without_the_anti_forgery_header_are_rejected`, `Sign_in_and_setup_need_the_anti_forgery_header_too`, `A_request_from_another_origin_is_rejected_even_with_the_header` | read | ok |
| 1 | Rate limit and lockout (5 per address / 30 overall in 15 min, lockout 15 min) | `AttemptLimiter.cs`, `AccountService.cs`, `AuthOptions.cs`; keyed by the remote address, forwarding headers are not trusted (`AuthEndpoints.Caller`) | `AttemptLimiterTests`, `AuthEndpointTests`: `Five_wrong_passwords_lock_sign_in_out`, `A_lockout_of_one_address_does_not_lock_out_another`, **new** `A_burst_of_parallel_wrong_passwords_cannot_exceed_the_failure_limit` | read | **F-01 fixed**; F-05 accepted |
| 1 | `reset-password` | `Security/Auth/ResetPasswordCommand.cs` (needs administrator or root, writes a new setup token, old password works until the new one is set) | `SetupTokenTests`: `Reset_password_without_administrator_rights_is_refused_and_writes_nothing`, `Reset_password_as_administrator_recreates_the_setup_token`; `AuthEndpointTests`: `Reset_password_makes_setup_available_again_and_a_new_password_replaces_the_old_one` | read | ok |
| 2 | Link tokens: 32 random bytes, shown once, stored as SHA-256, individually revocable, several clients | `Link/Pairing/LinkTokenStore.cs` (`IssueAsync`, `ValidateAsync` compares against all records with `FixedTimeEquals`, `RevokeAsync`), `PairingService.cs` (revocation closes open connections with 4401, re-check after registering) | `PairEndpointTests`: `A_pairing_started_in_the_ui_pairs_a_client_and_the_token_authenticates`, `The_client_list_shows_the_client_and_revoking_it_rejects_its_token_and_closes_its_connection`; `PairingServiceTests` | read | ok |
| 2 | One-time pairing code | `PairingCodeManager.cs` (6 digits, 5 minutes, one use, invalidated after 5 wrong attempts, constant-time compare); `PairingEndpoints.cs` (HTTPS only, 4 KiB body, `Cache-Control: no-store`) | `PairingCodeTests`, `PairEndpointTests`: `A_wrong_code_is_unauthorized_and_five_wrong_attempts_invalidate_the_code`, `A_code_pairs_only_once_over_http`, `Pairing_is_refused_over_plain_http`, `An_oversized_pairing_request_is_rejected` | read | ok; F-05 accepted |
| 3 | TLS: self-signed ECDSA P-256, SANs, fingerprint pinning | `Security/Tls/SelfSignedCertificateFactory.cs`, `TlsCertificateProvider.cs` (stored in the secret store, replaced when expired or unreadable), `CertificateFingerprint.cs`; fingerprint shown by `GET /api/v1/link/pairing` and the HA config flow | `TlsCertificateTests` (all), `PairEndpointTests` | read | ok; **F-04**: "a user-supplied certificate can replace it" has no supported path yet |
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
