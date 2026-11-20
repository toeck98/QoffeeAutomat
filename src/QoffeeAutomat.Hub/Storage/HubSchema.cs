namespace QoffeeAutomat.Hub.Storage;

/// <summary>One step of the schema of <c>hub.db</c>.</summary>
/// <param name="Version">The schema version after the step; versions are consecutive and start at 1.</param>
/// <param name="Description">What the step adds.</param>
/// <param name="Sql">The statements; they run in one transaction together with the update of the version.</param>
public sealed record HubMigration(int Version, string Description, string Sql);

/// <summary>
/// The versioned schema of <c>hub.db</c> (PLAN.md S20, T902). The version is kept in <c>PRAGMA user_version</c>; a database
/// is migrated forward step by step on start, every step in a transaction. A step that has been released is never edited: a
/// change of the schema is a new step at the end of <see cref="Migrations"/>. A database with a higher version than
/// <see cref="LatestVersion"/> was written by a newer hub and is refused.
/// </summary>
public static class HubSchema
{
    /// <summary>The steps in order.</summary>
    public static IReadOnlyList<HubMigration> Migrations { get; } =
    [
        new HubMigration(
            1,
            "Hub identity and settings (T902).",
            """
            CREATE TABLE hub_meta (
                key   TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            ) WITHOUT ROWID;
            """),
        new HubMigration(
            2,
            "Users, API tokens and the audit log (T903).",
            """
            CREATE TABLE users (
                id                  TEXT    NOT NULL PRIMARY KEY,
                username            TEXT    NOT NULL UNIQUE COLLATE NOCASE,
                password_hash       TEXT    NOT NULL,
                role                TEXT    NOT NULL CHECK (role IN ('admin', 'operator', 'viewer')),
                disabled            INTEGER NOT NULL DEFAULT 0,
                created_at          INTEGER NOT NULL,
                updated_at          INTEGER NOT NULL,
                password_changed_at INTEGER NOT NULL,
                last_login_at       INTEGER
            );

            CREATE TABLE api_tokens (
                id           TEXT    NOT NULL PRIMARY KEY,
                name         TEXT    NOT NULL,
                role         TEXT    NOT NULL CHECK (role IN ('admin', 'operator', 'viewer')),
                token_hash   TEXT    NOT NULL UNIQUE,
                token_prefix TEXT    NOT NULL,
                created_by   TEXT    NOT NULL,
                created_at   INTEGER NOT NULL,
                expires_at   INTEGER,
                last_used_at INTEGER,
                revoked_at   INTEGER
            );

            CREATE TABLE audit_log (
                id             INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                at             INTEGER NOT NULL,
                user_name      TEXT    NOT NULL,
                role           TEXT,
                auth_method    TEXT    NOT NULL,
                action         TEXT    NOT NULL,
                method         TEXT    NOT NULL,
                target         TEXT,
                agents         TEXT    NOT NULL DEFAULT '[]',
                result         TEXT    NOT NULL,
                status         INTEGER NOT NULL,
                remote_address TEXT
            );
            CREATE INDEX ix_audit_log_at ON audit_log (at);
            CREATE INDEX ix_audit_log_user ON audit_log (user_name, at);
            CREATE INDEX ix_audit_log_action ON audit_log (action, at);
            """),
        new HubMigration(
            3,
            "Enrollment tokens, the agent registry and revoked agent credentials (T904).",
            """
            CREATE TABLE enrollment_tokens (
                id                TEXT    NOT NULL PRIMARY KEY,
                name              TEXT    NOT NULL,
                token_hash        TEXT    NOT NULL UNIQUE,
                token_prefix      TEXT    NOT NULL,
                tags              TEXT    NOT NULL DEFAULT '[]',
                requires_approval INTEGER NOT NULL DEFAULT 0,
                max_uses          INTEGER,
                uses              INTEGER NOT NULL DEFAULT 0,
                created_by        TEXT    NOT NULL,
                created_at        INTEGER NOT NULL,
                expires_at        INTEGER,
                last_used_at      INTEGER,
                revoked_at        INTEGER
            );

            CREATE TABLE agents (
                id                           TEXT    NOT NULL PRIMARY KEY,
                name                         TEXT    NOT NULL,
                hostname                     TEXT    NOT NULL,
                os                           TEXT    NOT NULL,
                os_version                   TEXT,
                arch                         TEXT    NOT NULL,
                version                      TEXT    NOT NULL,
                macs                         TEXT    NOT NULL DEFAULT '[]',
                ui_url                       TEXT,
                tags                         TEXT    NOT NULL DEFAULT '[]',
                status                       TEXT    NOT NULL CHECK (status IN ('pending', 'approved', 'enrolled', 'rejected')),
                enrolled_at                  INTEGER NOT NULL,
                approved_at                  INTEGER,
                last_seen_at                 INTEGER,
                token_id                     TEXT,
                credential_hash              TEXT    UNIQUE,
                credential_issued_at         INTEGER,
                pending_credential_hash      TEXT    UNIQUE,
                pending_credential_issued_at INTEGER
            );
            CREATE INDEX ix_agents_status ON agents (status);

            CREATE TABLE revoked_credentials (
                credential_hash TEXT    NOT NULL PRIMARY KEY,
                agent_id        TEXT    NOT NULL,
                revoked_at      INTEGER NOT NULL,
                reason          TEXT    NOT NULL
            ) WITHOUT ROWID;
            """),
        new HubMigration(
            4,
            "Events that agents report over the agent link (T906).",
            """
            CREATE TABLE agent_events (
                id          INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                agent_id    TEXT    NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
                at          INTEGER NOT NULL,
                received_at INTEGER NOT NULL,
                type        TEXT    NOT NULL,
                data        TEXT
            );
            CREATE INDEX ix_agent_events_agent ON agent_events (agent_id, id);
            """),
        new HubMigration(
            5,
            "Configuration library, profiles, assignments and the desired state of every agent (T907).",
            """
            CREATE TABLE library_documents (
                kind             TEXT    NOT NULL,
                id               TEXT    NOT NULL,
                current_revision INTEGER NOT NULL,
                created_at       INTEGER NOT NULL,
                deleted_at       INTEGER,
                PRIMARY KEY (kind, id)
            ) WITHOUT ROWID;

            CREATE TABLE library_revisions (
                kind         TEXT    NOT NULL,
                id           TEXT    NOT NULL,
                revision     INTEGER NOT NULL,
                body         TEXT    NOT NULL,
                hash         TEXT    NOT NULL,
                content_hash TEXT    NOT NULL,
                name         TEXT,
                created_at   INTEGER NOT NULL,
                created_by   TEXT    NOT NULL,
                PRIMARY KEY (kind, id, revision),
                FOREIGN KEY (kind, id) REFERENCES library_documents (kind, id)
            ) WITHOUT ROWID;

            CREATE TRIGGER library_revisions_immutable BEFORE UPDATE ON library_revisions
            BEGIN
                SELECT RAISE(ABORT, 'A library revision is immutable.');
            END;

            CREATE TABLE profiles (
                id              TEXT    NOT NULL PRIMARY KEY,
                draft           TEXT    NOT NULL,
                active_revision INTEGER,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE profile_revisions (
                profile_id   TEXT    NOT NULL REFERENCES profiles (id) ON DELETE CASCADE,
                revision     INTEGER NOT NULL,
                content      TEXT    NOT NULL,
                published_at INTEGER NOT NULL,
                published_by TEXT    NOT NULL,
                note         TEXT,
                PRIMARY KEY (profile_id, revision)
            ) WITHOUT ROWID;

            CREATE TRIGGER profile_revisions_immutable BEFORE UPDATE ON profile_revisions
            BEGIN
                SELECT RAISE(ABORT, 'A published profile revision is immutable.');
            END;

            CREATE TABLE assignments (
                id         TEXT    NOT NULL PRIMARY KEY,
                profile_id TEXT    NOT NULL REFERENCES profiles (id) ON DELETE CASCADE,
                target     TEXT    NOT NULL,
                created_at INTEGER NOT NULL,
                created_by TEXT    NOT NULL,
                UNIQUE (profile_id, target)
            ) WITHOUT ROWID;

            CREATE TABLE agent_desired (
                agent_id     TEXT    NOT NULL PRIMARY KEY,
                generation   INTEGER NOT NULL,
                content_hash TEXT    NOT NULL,
                policy       TEXT    NOT NULL,
                items        TEXT    NOT NULL,
                signature    TEXT    NOT NULL,
                updated_at   INTEGER NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE config_blobs (
                hash TEXT NOT NULL,
                kind TEXT NOT NULL,
                body TEXT NOT NULL,
                PRIMARY KEY (hash, kind)
            ) WITHOUT ROWID;
            """),
        new HubMigration(
            6,
            "Roll-out status of every agent: the generation the hub pushed, the generation the agent applied and the per-document results it reported (T909).",
            """
            CREATE TABLE agent_config_status (
                agent_id            TEXT    NOT NULL PRIMARY KEY,
                pushed_generation   INTEGER NOT NULL DEFAULT 0,
                pushed_at           INTEGER,
                delivery_error      TEXT,
                applied_generation  INTEGER NOT NULL DEFAULT 0,
                applied_at          INTEGER,
                applied_results     TEXT    NOT NULL DEFAULT '[]',
                reported_generation INTEGER NOT NULL DEFAULT 0,
                reported_state      TEXT,
                reported_at         INTEGER,
                reported_results    TEXT    NOT NULL DEFAULT '[]'
            ) WITHOUT ROWID;
            """),
        new HubMigration(
            7,
            "Metric series, raw samples, the change log of text metrics and the five minute and one hour roll-ups (T910).",
            """
            CREATE TABLE metric_series (
                id         INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                agent_id   TEXT    NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
                metric_id  TEXT    NOT NULL,
                value_type TEXT    NOT NULL CHECK (value_type IN ('number', 'bool', 'string', 'timestamp', 'enum')),
                unit       TEXT,
                UNIQUE (agent_id, metric_id)
            );

            CREATE TABLE metric_raw (
                series_id INTEGER NOT NULL REFERENCES metric_series (id) ON DELETE CASCADE,
                ts        INTEGER NOT NULL,
                value     REAL    NOT NULL,
                PRIMARY KEY (series_id, ts)
            ) WITHOUT ROWID;
            CREATE INDEX ix_metric_raw_ts ON metric_raw (ts);

            CREATE TABLE metric_states (
                series_id INTEGER NOT NULL REFERENCES metric_series (id) ON DELETE CASCADE,
                ts        INTEGER NOT NULL,
                value     TEXT    NOT NULL,
                PRIMARY KEY (series_id, ts)
            ) WITHOUT ROWID;
            CREATE INDEX ix_metric_states_ts ON metric_states (ts);

            CREATE TABLE metric_rollup_5m (
                series_id    INTEGER NOT NULL REFERENCES metric_series (id) ON DELETE CASCADE,
                bucket       INTEGER NOT NULL,
                min_value    REAL    NOT NULL,
                avg_value    REAL    NOT NULL,
                max_value    REAL    NOT NULL,
                sample_count INTEGER NOT NULL,
                PRIMARY KEY (series_id, bucket)
            ) WITHOUT ROWID;
            CREATE INDEX ix_metric_rollup_5m_bucket ON metric_rollup_5m (bucket);

            CREATE TABLE metric_rollup_1h (
                series_id    INTEGER NOT NULL REFERENCES metric_series (id) ON DELETE CASCADE,
                bucket       INTEGER NOT NULL,
                min_value    REAL    NOT NULL,
                avg_value    REAL    NOT NULL,
                max_value    REAL    NOT NULL,
                sample_count INTEGER NOT NULL,
                PRIMARY KEY (series_id, bucket)
            ) WITHOUT ROWID;
            CREATE INDEX ix_metric_rollup_1h_bucket ON metric_rollup_1h (bucket);
            """),
        new HubMigration(
            8,
            "Fleet tokens of Home Assistant: scope all or a set of tags, stored as a hash (T913).",
            """
            CREATE TABLE fleet_tokens (
                id           TEXT    NOT NULL PRIMARY KEY,
                name         TEXT    NOT NULL,
                token_hash   TEXT    NOT NULL UNIQUE,
                token_prefix TEXT    NOT NULL,
                scope        TEXT    NOT NULL CHECK (scope IN ('all', 'tags')),
                tags         TEXT    NOT NULL DEFAULT '[]',
                client_kind  TEXT    NOT NULL,
                client_name  TEXT    NOT NULL,
                created_by   TEXT    NOT NULL,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER,
                revoked_at   INTEGER
            );
            """),
    ];

    /// <summary>The version of the newest step.</summary>
    public static int LatestVersion => Migrations[^1].Version;
}
