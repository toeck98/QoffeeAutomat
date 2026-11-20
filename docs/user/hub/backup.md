# Back up and restore the hub

Everything the hub knows is in **one folder**: the data directory (`/data` in the container and in the app).

| Path in `/data` | Content |
|-----------------|---------|
| `hub.db` (with `hub.db-wal` and `hub.db-shm` while the hub runs) | Database: users, enrollment, agents, library, profiles, assignments, audit log and the metric history. |
| `secrets/secrets.key`, `secrets/secrets.json` | The key and the protected secrets: the **signing key** of the hub and the TLS key. |
| `setup-token.txt` | Only before the first administrator exists. |

!!! danger "Keep the database and the secrets together, and keep them private"
    The backup contains the keys that sign the configuration of your PCs. Store it as carefully as a password manager, and
    never separate `hub.db` from `secrets/`: **a hub without its signing key is a new hub**, and every PC refuses its
    configuration and has to join again.

## Back up

**Home Assistant app:** make a normal Home Assistant backup that contains the app. The app is stopped for the backup, so the
database is consistent. The backup includes the data of the hub and nothing else of it needs saving.

**Container:** stop the hub for a short time and copy the volume. A copy of the files of a running SQLite database in
WAL mode can be inconsistent.

```bash
docker compose stop hub
docker run --rm -v qoffeeautomat-hub_hub-data:/data:ro -v "$PWD":/backup alpine \
  tar -czf /backup/qoffeeautomat-hub-$(date +%F).tar.gz -C /data .
docker compose start hub
```

(The volume name starts with the Compose project name, `docker volume ls` shows it.) The metric history is the largest part
of the database; if you only need the configuration, you can accept losing it, but the file is one unit.

## Restore

1. Stop the hub (or the app).
2. Put the files back into an **empty** data directory, owned by the user of the image (1654 in the container), mode 0700:

    ```bash
    docker compose down
    docker volume rm qoffeeautomat-hub_hub-data
    docker volume create qoffeeautomat-hub_hub-data
    docker run --rm -v qoffeeautomat-hub_hub-data:/data -v "$PWD":/backup alpine \
      sh -c 'tar -xzf /backup/qoffeeautomat-hub-2026-01-01.tar.gz -C /data && chown -R 1654:1654 /data && chmod 0700 /data'
    docker compose up -d
    ```

3. Use the **same version of the hub as the backup, or a newer one**: the hub migrates its database forward and cannot go back.
4. Check **Fleet**. The PCs reconnect by themselves, because their credentials and the hub's signing key are in the backup. A
   PC that joined after the backup is unknown to the restored hub: remove its hub link on the PC and enroll it again.

For a **Home Assistant app**, restore the backup in Home Assistant (**Settings, System, Backups**) and start the app.

## Move the hub to another host

Back up as above, restore on the new host, and give the new host the **same address** (DNS name) that is in the enrollment
strings, or the PCs cannot find it. The certificate fingerprint stays the same because the certificate is in the backup. If the
address has to change, create new enrollment strings and let the PCs join again (agent: **Leave the hub**, then join).

## What a backup does not contain

The agents' own configuration is on the PCs ([Backup and restore](../backup-restore.md)); the hub only holds what it published.
