# Profiles, assignments and policies

The hub distributes configuration as a **desired state**: for each PC it computes the complete set of documents that applies to
it, numbers it (a *generation*), signs it, and the agent pulls what it does not have yet. The agent verifies the signature and
**validates every document itself**; the hub is never trusted to have validated.

## The building blocks

| Object | What it is |
|--------|------------|
| **Library document** | A command, sensor, script, mode group or metric-override document in the same format as on a single PC. The library keeps a history of revisions. |
| **Profile** | A named set of library documents (pinned at a revision), **variables** and a **policy**. It has a priority and an optional OS filter. A profile has a draft and immutable published revisions. |
| **Assignment** | A profile applied to a target: `all`, `tag:<tag>` or `agent:<id>`. |

The editors for commands and sensors in the library are the same as in the interface of a single PC.

## From draft to PC

1. Write documents in **Library**.
2. In **Profiles** create a profile, pin the documents, add variables and a policy, and save the draft.
3. **Preview** shows the PCs that are affected and what changes for each of them. **Publish** freezes the draft as the next
   revision.
4. In **Assignments** apply the profile to `all`, to a tag or to one PC. Assignments take effect at once.
5. **Roll-out** shows for every PC the generation the hub wants and the generation the PC has applied, with the state of every
   document. A PC that is offline applies it when it connects.

A **roll-back** publishes an earlier revision again. Publishing without a change in content creates no new generation.

!!! warning "System scripts need an administrator"
    A profile that contains a command with `runAs: system` and a PowerShell, bash or process action (or a script such a command
    runs) can only be published by an `admin`. The preview names the affected PCs first.

## Which profile wins

The more specific target wins: `all` is overridden by `tag:<tag>`, which is overridden by `agent:<id>`. Within the same level a higher
**priority** number wins; if that is equal too, the profile id decides, so the result is always the same. On equal kind and
id of a document the later one replaces the earlier one. Variables and policy merge the same way. The preview shows
**warnings** where a profile overrides another one, and when a command runs a script that no profile of the PC provides.

## Variables

The variables of a profile are key and value pairs that every run of a command on the PC sees as environment variables
`QA_HUBVAR_<NAME>`. They are **never pasted into script text** or command lines (the same rule as for command variables on a PC).
Do not put secrets into variables: they are stored in the hub database and are visible to every administrator.

## Policies

A profile can set a policy for the PCs it reaches (each value can also be `inherit`):

| Policy | Values | Effect |
|--------|--------|--------|
| `local_config` | `allow` (default), `locked` | **locked**: the local commands and sensors of the PC are switched off, and its interface and API refuse changes to commands, sensors, scripts and metric overrides. Viewing, changing the password and leaving the hub remain possible. |
| `direct_link` | `allow` (default), `deny` | **deny**: the PC stops advertising itself, refuses pairing with Home Assistant and closes direct connections. Home Assistant must use the [hub route](../hub-route.md). |

## What the PC shows

The documents from the hub live in a separate layer on the PC, shown as **from hub** and read-only locally. The effective
configuration is the hub layer over the local layer; if a hub document and a local one have the same kind and id, the hub one
wins and the local one is kept but shadowed. When the PC leaves the hub, the local layer is what remains.

A document that the PC cannot run (for example a Windows-only action on Linux) is **skipped and reported** in the roll-out
status; it does not fail the generation. A document that is **invalid** makes the generation fail: the PC keeps the previous
generation.

## The two Home Assistant routes

Profiles work the same for both ways Home Assistant can reach a PC. The one thing that depends on the route is the policy
`direct_link`: with `deny` the PC can only be reached through the hub. See [Connect Home Assistant to a hub](../hub-route.md).
