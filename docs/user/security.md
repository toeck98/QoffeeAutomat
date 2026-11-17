# Security model and recommendations

QoffeeAutomat runs a service with the **highest rights of the system** (LocalSystem) that can execute scripts on request. That
is what makes it useful and what makes it worth protecting. This page says what is protected against whom, how, and what you
should do.

## What is considered

| Threat | Example |
|--------|---------|
| Other users on the same PC | A standard user tries to make the service run code with system rights. |
| Other devices on the network | A guest on your Wi-Fi scans for open ports and tries to run commands. |
| Malicious values from Home Assistant | A template, a voice command or a notification sends a crafted value to a command. |
| A stolen link token | A backup of Home Assistant leaks. |

What is **not** protected against: a person with administrator rights on the PC (they own it anyway), and someone with the
password of the web interface (they can author any command). The PC's own security (updates, antivirus, disk encryption) stays
your job.

## What the agent does

### Authentication everywhere

* The web interface and the management API **always** require a password, also on `127.0.0.1`. Without it, any program of any
  user on the PC could use the API to get system rights.
* First run: the agent writes a random **setup token** to `secrets\setup-token.txt`, readable by administrators only. You prove
  administrator access by entering it, then choose a password. The file is deleted.
* Passwords are stored as salted PBKDF2-SHA256 hashes. Sessions use an `HttpOnly`, `SameSite=Strict` cookie (`Secure` on HTTPS)
  and end after eight idle hours or seven days.
* Requests that change something need the header `X-QoffeeAutomat-Csrf: 1` and, if the browser sends an `Origin`, the agent's own
  origin. A page on another site cannot make your browser change commands.
* Five failed sign-ins from one address within 15 minutes lock that address out for 15 minutes; 30 failures from all
  addresses lock everybody out for 15 minutes.
* The password is reset with `QoffeeAutomat.Agent.exe reset-password` as a local administrator (see
  [First-run setup](first-run.md#forgotten-password)).
* **One-click login from the tray.** If you are a member of the local Administrators group, **Open UI** in the tray menu signs
  you in without typing the password: the agent hands the tray a login token that is valid for **30 seconds**, works **once**
  and only from **this PC** (a request from the network never works, and trying it uses the token up). Opening it starts a
  normal session. The agent decides who gets a token, not the tray program: it looks at the account that is logged on at the
  console, so a standard user, another user's background session and a tampered tray program never get one and see the normal
  login page. If User Account Control is switched off, Windows gives no sign of administrator rights, and you also see the login
  page.

### A web interface that stays on the PC

The management API and the web interface listen on the **loopback address only** (`127.0.0.1:8480`). The second listener, port
8481 on all interfaces, serves QoffeeLink and pairing. The web interface and the API only answer there if you switch on
**Remote UI** in the settings, which is **off by default**. Even then, every request needs the password.

### Encrypted and authenticated link

* The link (port 8481) is always HTTPS/WSS. The agent creates a **self-signed ECDSA P-256 certificate** on first start. Home
  Assistant pins its SHA-256 fingerprint at pairing time and later accepts only that certificate. You compare the fingerprint
  in the agent and in Home Assistant at pairing, which defeats a man in the middle on your network.
* Pairing needs a **one-time six-digit code** that lives five minutes, works once and is blocked after five wrong attempts.
* The **link token** is 32 random bytes, shown to Home Assistant once. The agent stores only its SHA-256 hash. Revoke a client
  on the page *Home Assistant* and its connections close at once.
* Credentials are only sent in the upgrade header and the pairing answer, never inside compressed frames.

### Variables are data, never code

This is the central protection against malicious values:

* A variable value is **never** pasted into script text or a command line. PowerShell receives the values as **bound, typed named
  parameters** through a generated wrapper that reads them from a parameter file; programs receive each value as **one separate
  argument**; the environment gets `QA_VAR_<NAME>`.
* The agent **validates every value** against the parameter definition (type, range, step, pattern, options, length), whatever
  the sender checked. Unknown variables are rejected.
* Home Assistant can only **run** commands that are enabled and exposed, with the variables they declare. It cannot create or
  change commands, scripts or sensors; that needs the password of the web interface.

So `"; Remove-Item C:\ -Recurse"` as a value of a text variable is just a string, even if it comes from a template, a voice
assistant or a notification action. What a command *does with* a string (your script may well pass it to `Invoke-Expression`!)
is still up to you: **do not write scripts that execute their parameters**.

### Secrets for scripts

Passwords and keys that a script needs are stored as **named secrets** (page *Secrets*), not in scripts or variables:

* The value is **write-only**. The UI and the API create, replace and delete a secret but never return the value.
* It is encrypted in the secrets file (Windows DPAPI, machine scope; a key file with mode 0600 on Linux) like the password hash
  and the link tokens.
* Only a command that **lists** the secret receives it, and only as the environment variable `QA_SECRET_<NAME>` of its own
  process: never as an argument, in the parameter file or in a script.
* It never appears in the run history, the logs, the audit log, the configuration export or the diagnostics bundle, and when a
  script prints it, the captured output is **masked** (`***`) before it is stored or shown. The export contains the *names* a
  command uses, never a value.

Anybody who can edit a command can list any secret in it and print it into the environment of a program that they choose, so a
secret is as safe as the password of the web interface that guards the editor.

### Files and processes

* The folders `config`, `scripts` and `secrets` in `%ProgramData%\QoffeeAutomat` can only be written by `SYSTEM` and Administrators.
  The agent refuses to start a *System* script whose file or folder can be changed by non-administrators and logs a security
  warning. The installer refuses an installation folder outside Program Files for the same reason.
* Secrets that the agent has to read back are protected with the Windows data protection API (machine scope); passwords and
  link tokens are stored only as hashes.
* The agent and the **session helper** talk over a named pipe. Only interactive users can connect to it; the agent accepts only
  the installed `QoffeeAutomat.SessionHost.exe` and the helper checks that the other end runs as `SYSTEM` from the agent's
  folder.
* The session helper runs with the **standard token** of the user. Elevated desktop commands (`user_elevated`) are carried out
  by the agent only when the logged-in user is an administrator; they never give a standard user more rights.

### Accountability

Every run is written to the **audit log** with source (`ha`, `ui`, `cli`, `sequence`, `test`), client, the Home Assistant user id
(when supplied), the variables (values of parameters marked *secret* are hidden), exit code and duration. See the page
*History*. The diagnostics bundle and the Home Assistant diagnostics redact tokens, fingerprints, addresses and names of hosts.

### Supply chain

Releases are built by a public workflow with pinned actions. Every asset has a **checksum**, an **SPDX software bill of
materials** and a **build provenance attestation**; the executables are signed when the signing service is configured. See
[Verify your download](verify-download.md). The product loads no assets from other hosts. The only request the agent makes to
the internet **on its own** is the daily [update check](updates.md): one HTTPS request to the GitHub releases page of the
project, without credentials or an identifier of the PC; switch it off in the settings and it makes none. The agent installs an
update only when an authorized client asks for it, and only after the checksum (and, for a signed build, the signature) of the
installer is verified. The one request it makes for someone else is the picture of a
[notification](notifications.md#the-picture): when Home Assistant sends an `image_url`, the agent fetches that address
(`http` or `https`, no credentials, no link-local address, at most three redirects, ten seconds, 1 MiB, only PNG, JPEG, GIF or
BMP). Nothing is fetched unless an authorized client asks for it.

## Recommendations

**Passwords and access**

1. Use a **long, unique password** for the web interface, kept in a password manager.
2. Leave **Remote UI off** unless you really need it. If you switch it on, do it only in a network you trust.
3. Treat your **Home Assistant account as part of the PC's security**: whoever can use an exposed command, for example through a
   dashboard, a shared voice assistant or a guest account with access to scripts, can run it. Give dashboards and voice assistants
   the buttons they need, not every command.
4. Pair only Home Assistant instances you control, and **revoke** clients you no longer use.

**Commands**

5. **Expose only what you use.** A command that is disabled or not shown in Home Assistant cannot be run from there. The default
   power and session commands start out disabled for this reason.
6. Use the **least powerful run context**: *User session* instead of *System* where the desktop is enough, and avoid
   *User session, elevated* unless a program really needs it.
7. **Declare strict parameters**: use `enum` options instead of free text, set `min`, `max` and `maxLength`, add a `pattern` to
   text, and mark passwords and tokens as `secret`.
8. **Review scripts** before you enable them, also templates you downloaded. Never pass a parameter to `Invoke-Expression`,
   `cmd /c` or a similar construct.
9. Set a sensible **timeout**, a **cooldown** for commands that should not be repeated at once, and `concurrency: single` where two
   runs at the same time would be wrong.
10. Keep dangerous commands (shut down, format, delete) **out of voice control** and behind a confirmation in Home Assistant.

**The PC**

11. Keep the PC and the agent **up to date**; watch the releases.
12. **Verify downloads** (checksum and attestation) before you install.
13. Think twice about **automatic sign-in** for [unattended starts](example-mode-start.md): it lets anybody who can reach the PC
    physically use your account. Use it for PCs in a room you control, with a locked screen after a short idle time if you want.
14. Do not share the machine's `%ProgramData%\QoffeeAutomat\secrets` folder or backups of the whole disk without protecting them.
    A [configuration export](backup-restore.md) contains **no** passwords, tokens or keys.

**Network**

15. The installer opens port 8481 for **private and domain** networks only. Keep your PC's network profile *Private* only on networks
    you trust, and do not forward port 8481 to the internet. Use a VPN to reach Home Assistant and the PC from outside.
16. Keep IoT and guest devices in a separate network segment from the PC and Home Assistant.

## For reviewers

The [threat model and security review](https://github.com/toeck98/QoffeeAutomat/blob/main/docs/security/threat-model.md) maps
every point of this page to the code that implements it and the tests that cover it, and lists the findings of the 1.0 review.

## Reporting a problem

Please do **not** open a public issue for a vulnerability. Use the private reporting channel described in the
[security policy](https://github.com/toeck98/QoffeeAutomat/security/policy).
