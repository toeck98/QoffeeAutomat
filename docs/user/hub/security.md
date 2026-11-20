# Security recommendations for the hub

The hub can run code on every PC that joined it, so it is the most sensitive part of QoffeeAutomat. The general model is in
[Security model and recommendations](../security.md); this page adds what is specific to the hub.

## What the hub does for you

* **Authentication is always required.** Local accounts with the password rules of the agent, and three roles: `admin`,
  `operator`, `viewer`. API tokens carry a role. Sign-ins and the enrollment endpoint are rate limited.
* **Everything that changes is audited**: library, profiles, assignments, tokens, users, every publish and every command run, with
  user, time and the affected PCs.
* **Secrets are stored hashed or protected.** Agent credentials, Home Assistant fleet tokens and API tokens are stored as hashes.
  The signing key and the TLS key are protected at rest in the data volume.
* **The PCs do not trust the hub blindly.** Every desired state is signed (ECDSA P-256) with a key the PC pinned when it joined,
  and the PC validates every document itself. A proxy that terminates TLS cannot change the configuration unnoticed.
* **Enrollment tokens expire**, can be limited in uses and can require approval.
* **System scripts need an administrator** to be published, after a preview of the affected PCs.
* **Roles limit what reaches a PC through the hub**: `viewer` can only read, `operator` can also run commands, `admin` can do
  everything. The PC narrows this further and refuses to change hub documents locally.

## What you should do

1. **Do not expose the hub to the internet.** Reach it from your LAN, through a VPN, or through a reverse proxy with its own
   authentication in front. PCs outside the LAN should use a VPN. If you must publish it, use your own certificate and a
   proxy that limits the paths to `/hub/v1/`, and keep the management interface on the LAN.
2. **Use a certificate you control for remote use.** The self-signed certificate is pinned in the enrollment strings, which is
   good inside a LAN. For a reverse proxy, terminate TLS there and set `QOFFEE_HUB_TRUSTED_PROXIES` to the proxy's address only.
3. **Protect the data volume and its backups** ([Back up and restore](backup.md)): they hold the signing key. Restrict access to
   the Docker host; whoever can read `/data` can impersonate the hub.
4. **Use named accounts and the least role.** Do not share the `admin` account; give people `operator` or `viewer`. Remove
   accounts you do not use.
5. **Keep enrollment tokens short-lived.** One use, an expiry of hours, **approval required** for anything that is not a PC you are
   standing next to. Revoke tokens you did not use.
6. **Use a read-only token for Prometheus** (the endpoint accepts nothing else) and one token per consumer.
7. **Keep the image updated.** Pin the version, read the release notes, back up, then update. Use the
   [GitHub attestation](../verify-download.md) of the image to check what you pull:
   `gh attestation verify oci://ghcr.io/toeck98/qoffeeautomat-hub:<version> --repo toeck98/QoffeeAutomat`.
8. **Review the audit log** after a change of staff or a suspicious event, and use `local_config: locked` for PCs that nobody
   should change by hand.
9. **Limit direct pairing** with the policy `direct_link: deny` for PCs that must only be reached through the hub.
10. **Do not put secrets into hub variables or scripts.** Variables are visible to administrators and reach every PC that the
    profile covers.

## In the Home Assistant app

* The Ingress port is reachable only by the Supervisor, and the Home Assistant user is trusted only there. Home Assistant
  administrators get the role of the option `ingress_role`; use `viewer` or `operator` if the people who open the panel should
  not be able to publish system scripts.
* The agent port 8490 is open on the host. Close it in the app's network settings if you only use the hub from the sidebar and
  no PC needs to join.

## If something is compromised

1. Stop the hub (or the app). PCs keep their last configuration and keep working; the hub can no longer change them.
2. Find the cause in the audit log. Change the passwords of the accounts and delete API tokens and fleet tokens.
3. Rotate the credentials of the PCs (**Rotate credential**), or let them leave and join again with new enrollment strings. If
   the signing key may have leaked, **create a new hub** (a new data directory) and enroll the PCs again, because the PCs trust
   the old key.

Report vulnerabilities as described in the [security policy](https://github.com/toeck98/QoffeeAutomat/security/policy).
