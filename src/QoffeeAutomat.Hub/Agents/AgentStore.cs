using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Agents;

/// <summary>The values that enrolling an agent writes to its row.</summary>
/// <param name="Id">The agent id.</param>
/// <param name="Name">The reported name.</param>
/// <param name="Hostname">The reported host name.</param>
/// <param name="Os">The reported operating system family.</param>
/// <param name="OsVersion">The reported operating system version.</param>
/// <param name="Arch">The reported architecture.</param>
/// <param name="Version">The reported agent version.</param>
/// <param name="Macs">The reported MAC addresses.</param>
/// <param name="UiUrl">The reported address of the local UI.</param>
/// <param name="Tags">The tags after the enrollment.</param>
/// <param name="Status">The status after the enrollment.</param>
/// <param name="Now">The time of the enrollment.</param>
/// <param name="ApprovedAt">When the agent was approved, if it is.</param>
/// <param name="TokenId">The enrollment token.</param>
/// <param name="CredentialHash">The hash of the issued credential, or null when none is issued.</param>
internal sealed record AgentEnrollmentWrite(
    string Id,
    string Name,
    string Hostname,
    string Os,
    string? OsVersion,
    string Arch,
    string Version,
    IReadOnlyList<string> Macs,
    string? UiUrl,
    IReadOnlyList<string> Tags,
    string Status,
    DateTimeOffset Now,
    DateTimeOffset? ApprovedAt,
    string TokenId,
    string? CredentialHash);

/// <summary>
/// The statements on the tables <c>agents</c> and <c>revoked_credentials</c>. Every method works on a connection (and a transaction) that the caller
/// got from <see cref="HubDatabase"/>, so that a caller can combine several steps, for example the use of a token and the creation of an agent,
/// in one transaction. Values travel as parameters only.
/// </summary>
internal static class AgentStore
{
    private const string Columns =
        "id, name, hostname, os, os_version, arch, version, macs, ui_url, tags, status, enrolled_at, approved_at, last_seen_at, "
        + "token_id, credential_issued_at, pending_credential_hash IS NOT NULL";

    public static AgentRecord? Find(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM agents WHERE id = @id;", ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static List<AgentRecord> List(SqliteConnection connection, string? status)
    {
        var agents = new List<AgentRecord>();
        using SqliteCommand command = status is null
            ? HubSql.Command(connection, null, "SELECT " + Columns + " FROM agents ORDER BY enrolled_at DESC, id;")
            : HubSql.Command(connection, null, "SELECT " + Columns + " FROM agents WHERE status = @status ORDER BY enrolled_at DESC, id;", ("@status", status));
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            agents.Add(Read(reader));
        }

        return agents;
    }

    public static long Count(SqliteConnection connection, SqliteTransaction? transaction, string status)
    {
        object? count = HubSql.Scalar(connection, transaction, "SELECT COUNT(*) FROM agents WHERE status = @status;", ("@status", status));
        return count is long number ? number : 0;
    }

    /// <summary>Inserts an agent or replaces what an enrollment decides about an existing one. The time of the first enrollment and the last-seen time stay.</summary>
    public static void WriteEnrollment(SqliteConnection connection, SqliteTransaction transaction, AgentEnrollmentWrite write)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO agents (id, name, hostname, os, os_version, arch, version, macs, ui_url, tags, status, enrolled_at, approved_at, token_id, credential_hash, credential_issued_at) "
            + "VALUES (@id, @name, @hostname, @os, @osVersion, @arch, @version, @macs, @uiUrl, @tags, @status, @now, @approvedAt, @tokenId, @hash, @issuedAt) "
            + "ON CONFLICT(id) DO UPDATE SET name = excluded.name, hostname = excluded.hostname, os = excluded.os, os_version = excluded.os_version, "
            + "arch = excluded.arch, version = excluded.version, macs = excluded.macs, ui_url = excluded.ui_url, tags = excluded.tags, "
            + "status = excluded.status, approved_at = excluded.approved_at, token_id = excluded.token_id, credential_hash = excluded.credential_hash, "
            + "credential_issued_at = excluded.credential_issued_at, pending_credential_hash = NULL, pending_credential_issued_at = NULL;",
            ("@id", write.Id),
            ("@name", write.Name),
            ("@hostname", write.Hostname),
            ("@os", write.Os),
            ("@osVersion", write.OsVersion),
            ("@arch", write.Arch),
            ("@version", write.Version),
            ("@macs", JsonSerializer.Serialize(write.Macs.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@uiUrl", write.UiUrl),
            ("@tags", JsonSerializer.Serialize(write.Tags.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@status", write.Status),
            ("@now", HubSql.ToStored(write.Now)),
            ("@approvedAt", write.ApprovedAt is { } approved ? HubSql.ToStored(approved) : (object?)null),
            ("@tokenId", write.TokenId),
            ("@hash", write.CredentialHash),
            ("@issuedAt", write.CredentialHash is null ? (object?)null : HubSql.ToStored(write.Now)));
    }

    /// <summary>Gives an approved agent its credential.</summary>
    public static void IssueCredential(SqliteConnection connection, SqliteTransaction transaction, string id, string hash, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET status = @status, credential_hash = @hash, credential_issued_at = @now WHERE id = @id;",
            ("@status", AgentStatuses.Enrolled),
            ("@hash", hash),
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    /// <summary>Refreshes what the agent reported about itself while it waits for approval.</summary>
    public static void UpdateReportedInfo(SqliteConnection connection, SqliteTransaction transaction, AgentEnrollmentWrite write)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET name = @name, hostname = @hostname, os = @os, os_version = @osVersion, arch = @arch, version = @version, "
            + "macs = @macs, ui_url = @uiUrl WHERE id = @id;",
            ("@id", write.Id),
            ("@name", write.Name),
            ("@hostname", write.Hostname),
            ("@os", write.Os),
            ("@osVersion", write.OsVersion),
            ("@arch", write.Arch),
            ("@version", write.Version),
            ("@macs", JsonSerializer.Serialize(write.Macs.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@uiUrl", write.UiUrl));
    }

    public static int SetTags(SqliteConnection connection, SqliteTransaction transaction, string id, IReadOnlyList<string> tags)
    {
        return HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET tags = @tags WHERE id = @id;",
            ("@tags", JsonSerializer.Serialize(tags.ToArray(), AgentStorageJsonContext.Default.StringArray)),
            ("@id", id));
    }

    public static int SetStatus(SqliteConnection connection, SqliteTransaction transaction, string id, string from, string to, DateTimeOffset? approvedAt)
    {
        return HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET status = @to, approved_at = COALESCE(@approvedAt, approved_at) WHERE id = @id AND status = @from;",
            ("@to", to),
            ("@approvedAt", approvedAt is { } at ? HubSql.ToStored(at) : (object?)null),
            ("@id", id),
            ("@from", from));
    }

    public static void Delete(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        HubSql.Execute(connection, transaction, "DELETE FROM agents WHERE id = @id;", ("@id", id));
    }

    /// <summary>The hashes of the current and of the not yet confirmed credential of an agent.</summary>
    public static (string? Current, string? Pending) CredentialHashes(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT credential_hash, pending_credential_hash FROM agents WHERE id = @id;",
            ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }

        return (reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>Remembers that a credential is revoked, so that a later connection with it gets the answer <c>revoked</c> and not <c>unauthorized</c>.</summary>
    public static void Revoke(SqliteConnection connection, SqliteTransaction transaction, string? hash, string agentId, DateTimeOffset now, string reason)
    {
        if (hash is null)
        {
            return;
        }

        HubSql.Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO revoked_credentials (credential_hash, agent_id, revoked_at, reason) VALUES (@hash, @agentId, @now, @reason);",
            ("@hash", hash),
            ("@agentId", agentId),
            ("@now", HubSql.ToStored(now)),
            ("@reason", reason));
    }

    /// <summary>Revokes the current and the pending credential of an agent.</summary>
    public static void RevokeAll(SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset now, string reason)
    {
        (string? current, string? pending) = CredentialHashes(connection, transaction, id);
        Revoke(connection, transaction, current, id, now, reason);
        Revoke(connection, transaction, pending, id, now, reason);
    }

    public static bool IsRevoked(SqliteConnection connection, SqliteTransaction? transaction, string hash)
    {
        return HubSql.Scalar(connection, transaction, "SELECT 1 FROM revoked_credentials WHERE credential_hash = @hash;", ("@hash", hash)) is not null;
    }

    public static void SetPendingCredential(SqliteConnection connection, SqliteTransaction transaction, string id, string hash, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET pending_credential_hash = @hash, pending_credential_issued_at = @now WHERE id = @id;",
            ("@hash", hash),
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    public static void ClearPendingCredential(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET pending_credential_hash = NULL, pending_credential_issued_at = NULL WHERE id = @id;",
            ("@id", id));
    }

    /// <summary>Makes the pending credential the current one; the old current credential becomes revoked.</summary>
    public static void PromotePendingCredential(SqliteConnection connection, SqliteTransaction transaction, string id, DateTimeOffset now)
    {
        (string? current, string? pending) = CredentialHashes(connection, transaction, id);
        if (pending is null)
        {
            return;
        }

        Revoke(connection, transaction, current, id, now, "rotated");

        HubSql.Execute(
            connection,
            transaction,
            "UPDATE agents SET credential_hash = pending_credential_hash, credential_issued_at = pending_credential_issued_at, "
            + "pending_credential_hash = NULL, pending_credential_issued_at = NULL WHERE id = @id;",
            ("@id", id));
    }

    /// <summary>Finds the agent that a credential belongs to, as the current or as the pending one.</summary>
    public static (AgentRecord Agent, bool IsPendingCredential)? FindByCredential(SqliteConnection connection, SqliteTransaction? transaction, string hash)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT " + Columns + ", COALESCE(pending_credential_hash = @hash, 0) FROM agents WHERE credential_hash = @hash OR pending_credential_hash = @hash;",
            ("@hash", hash));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (Read(reader), reader.GetInt64(17) != 0);
    }

    public static void TouchSeen(SqliteConnection connection, SqliteTransaction? transaction, string id, DateTimeOffset now)
    {
        HubSql.Execute(connection, transaction, "UPDATE agents SET last_seen_at = @now WHERE id = @id;", ("@now", HubSql.ToStored(now)), ("@id", id));
    }

    private static AgentRecord Read(SqliteDataReader reader)
    {
        return new AgentRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            ReadList(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            ReadList(reader.GetString(9)),
            reader.GetString(10),
            HubSql.FromStored(reader.GetInt64(11)),
            reader.IsDBNull(12) ? null : HubSql.FromStored(reader.GetInt64(12)),
            reader.IsDBNull(13) ? null : HubSql.FromStored(reader.GetInt64(13)),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : HubSql.FromStored(reader.GetInt64(15)),
            reader.GetInt64(16) != 0);
    }

    private static string[] ReadList(string json)
    {
        return JsonSerializer.Deserialize(json, AgentStorageJsonContext.Default.StringArray) ?? [];
    }
}
