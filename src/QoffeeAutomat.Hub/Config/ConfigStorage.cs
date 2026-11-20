using System.Text.Json;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Config;

/// <summary>A revision of a library document with the data of its document.</summary>
internal sealed record LibraryRow(
    string Kind,
    string Id,
    int Revision,
    string Hash,
    string ContentHash,
    string? Name,
    JsonElement Body,
    DateTimeOffset DocumentCreatedAt,
    DateTimeOffset RevisionCreatedAt,
    string CreatedBy,
    DateTimeOffset? DeletedAt);

/// <summary>A profile with its draft.</summary>
internal sealed record ProfileRow(
    string Id,
    ProfileContent Draft,
    string DraftText,
    int? ActiveRevision,
    int LatestRevision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A published revision of a profile.</summary>
internal sealed record ProfileRevisionRow(
    string ProfileId,
    int Revision,
    ProfileContent Content,
    string ContentText,
    DateTimeOffset PublishedAt,
    string PublishedBy,
    string? Note);

/// <summary>The desired state the hub last computed for an agent and told it about (T907). T909 sends it.</summary>
/// <param name="AgentId">The agent.</param>
/// <param name="Generation">The generation, increasing per agent; the first one is 1.</param>
/// <param name="ContentHash">The hash of policy and items, see <see cref="DesiredStateHash"/>.</param>
/// <param name="Policy">The policy.</param>
/// <param name="Items">The items, sorted as signed.</param>
/// <param name="Signature">The signature over <c>{agent_id, generation, policy, items}</c>; kept so that it is identical every time it is sent.</param>
/// <param name="UpdatedAt">When the generation was created.</param>
public sealed record AgentGeneration(
    string AgentId,
    long Generation,
    string ContentHash,
    PolicyInfo Policy,
    IReadOnlyList<DesiredItem> Items,
    string Signature,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Creates the <c>config_desired</c> frame for this generation.</summary>
    /// <param name="requestId">The request id.</param>
    /// <returns>The frame with the stored signature.</returns>
    public ConfigDesiredMessage ToMessage(long requestId) => new(requestId, Generation, Policy, Items, Signature);
}

/// <summary>The SQL of the configuration tables (migration 5). Statements are constants; values travel as parameters.</summary>
internal static class ConfigStorage
{
    private const string LibraryColumns =
        "d.kind, d.id, r.revision, r.hash, r.content_hash, r.name, r.body, d.created_at, r.created_at, r.created_by, d.deleted_at";

    private const string LibraryJoin =
        "FROM library_documents d JOIN library_revisions r ON r.kind = d.kind AND r.id = d.id AND r.revision = COALESCE(@revision, d.current_revision)";

    private const string ProfileColumns =
        "id, draft, active_revision, (SELECT COALESCE(MAX(revision), 0) FROM profile_revisions WHERE profile_id = profiles.id), created_at, updated_at";

    // Library -----------------------------------------------------------------------------------------------------------------------

    public static LibraryRow? FindLibrary(SqliteConnection connection, SqliteTransaction? transaction, string kind, string id, int? revision = null)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT " + LibraryColumns + " " + LibraryJoin + " WHERE d.kind = @kind AND d.id = @id;",
            ("@revision", revision),
            ("@kind", kind),
            ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadLibrary(reader) : null;
    }

    public static List<LibraryRow> ListLibrary(SqliteConnection connection, SqliteTransaction? transaction, string? kind)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT " + LibraryColumns + " " + LibraryJoin + " WHERE d.deleted_at IS NULL AND (@kind IS NULL OR d.kind = @kind) ORDER BY d.kind, d.id;",
            ("@revision", null),
            ("@kind", kind));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<LibraryRow>();
        while (reader.Read())
        {
            rows.Add(ReadLibrary(reader));
        }

        return rows;
    }

    public static List<LibraryRevisionSummary> ListLibraryRevisions(SqliteConnection connection, SqliteTransaction? transaction, string kind, string id)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT revision, hash, created_at, created_by FROM library_revisions WHERE kind = @kind AND id = @id ORDER BY revision DESC;",
            ("@kind", kind),
            ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        var revisions = new List<LibraryRevisionSummary>();
        while (reader.Read())
        {
            revisions.Add(new LibraryRevisionSummary(
                reader.GetInt32(0),
                reader.GetString(1),
                HubSql.FromStored(reader.GetInt64(2)),
                reader.GetString(3)));
        }

        return revisions;
    }

    public static void SaveLibraryRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string id,
        int revision,
        JsonElement body,
        string hash,
        string contentHash,
        string? name,
        string user,
        DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO library_documents (kind, id, current_revision, created_at, deleted_at) VALUES (@kind, @id, @revision, @now, NULL) "
            + "ON CONFLICT(kind, id) DO UPDATE SET current_revision = excluded.current_revision, deleted_at = NULL;",
            ("@kind", kind),
            ("@id", id),
            ("@revision", revision),
            ("@now", HubSql.ToStored(now)));
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO library_revisions (kind, id, revision, body, hash, content_hash, name, created_at, created_by) "
            + "VALUES (@kind, @id, @revision, @body, @hash, @contentHash, @name, @now, @user);",
            ("@kind", kind),
            ("@id", id),
            ("@revision", revision),
            ("@body", body.GetRawText()),
            ("@hash", hash),
            ("@contentHash", contentHash),
            ("@name", name),
            ("@now", HubSql.ToStored(now)),
            ("@user", user));
    }

    public static void MarkLibraryDeleted(SqliteConnection connection, SqliteTransaction transaction, string kind, string id, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE library_documents SET deleted_at = @now WHERE kind = @kind AND id = @id;",
            ("@now", HubSql.ToStored(now)),
            ("@kind", kind),
            ("@id", id));
    }

    // Profiles ----------------------------------------------------------------------------------------------------------------------

    public static ProfileRow? FindProfile(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT " + ProfileColumns + " FROM profiles WHERE id = @id;",
            ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadProfile(reader) : null;
    }

    public static List<ProfileRow> ListProfiles(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + ProfileColumns + " FROM profiles ORDER BY id;");
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<ProfileRow>();
        while (reader.Read())
        {
            rows.Add(ReadProfile(reader));
        }

        return rows;
    }

    public static void InsertProfile(SqliteConnection connection, SqliteTransaction transaction, string id, string draftText, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO profiles (id, draft, active_revision, created_at, updated_at) VALUES (@id, @draft, NULL, @now, @now);",
            ("@id", id),
            ("@draft", draftText),
            ("@now", HubSql.ToStored(now)));
    }

    public static void UpdateDraft(SqliteConnection connection, SqliteTransaction transaction, string id, string draftText, DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE profiles SET draft = @draft, updated_at = @now WHERE id = @id;",
            ("@draft", draftText),
            ("@now", HubSql.ToStored(now)),
            ("@id", id));
    }

    public static void SetActiveRevision(SqliteConnection connection, SqliteTransaction transaction, string id, int revision)
    {
        HubSql.Execute(
            connection,
            transaction,
            "UPDATE profiles SET active_revision = @revision WHERE id = @id;",
            ("@revision", revision),
            ("@id", id));
    }

    public static void DeleteProfile(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        // The revisions and the assignments of the profile go with it (ON DELETE CASCADE).
        HubSql.Execute(connection, transaction, "DELETE FROM profiles WHERE id = @id;", ("@id", id));
    }

    public static ProfileRevisionRow? FindProfileRevision(SqliteConnection connection, SqliteTransaction? transaction, string profileId, int revision)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT profile_id, revision, content, published_at, published_by, note FROM profile_revisions WHERE profile_id = @id AND revision = @revision;",
            ("@id", profileId),
            ("@revision", revision));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadProfileRevision(reader) : null;
    }

    public static List<ProfileRevisionRow> ListProfileRevisions(SqliteConnection connection, SqliteTransaction? transaction, string profileId)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT profile_id, revision, content, published_at, published_by, note FROM profile_revisions WHERE profile_id = @id ORDER BY revision DESC;",
            ("@id", profileId));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<ProfileRevisionRow>();
        while (reader.Read())
        {
            rows.Add(ReadProfileRevision(reader));
        }

        return rows;
    }

    public static void InsertProfileRevision(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string profileId,
        int revision,
        string contentText,
        string user,
        string? note,
        DateTimeOffset now)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO profile_revisions (profile_id, revision, content, published_at, published_by, note) VALUES (@id, @revision, @content, @now, @user, @note);",
            ("@id", profileId),
            ("@revision", revision),
            ("@content", contentText),
            ("@now", HubSql.ToStored(now)),
            ("@user", user),
            ("@note", note));
    }

    // Assignments -------------------------------------------------------------------------------------------------------------------

    public static List<AssignmentInfo> ListAssignments(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT id, profile_id, target, created_at, created_by FROM assignments ORDER BY profile_id, target;");
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<AssignmentInfo>();
        while (reader.Read())
        {
            rows.Add(ReadAssignment(reader));
        }

        return rows;
    }

    public static AssignmentInfo? FindAssignment(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT id, profile_id, target, created_at, created_by FROM assignments WHERE id = @id;",
            ("@id", id));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadAssignment(reader) : null;
    }

    public static bool AssignmentExists(SqliteConnection connection, SqliteTransaction? transaction, string profileId, string target)
    {
        object? found = HubSql.Scalar(
            connection,
            transaction,
            "SELECT 1 FROM assignments WHERE profile_id = @profile AND target = @target;",
            ("@profile", profileId),
            ("@target", target));
        return found is not null;
    }

    public static void InsertAssignment(SqliteConnection connection, SqliteTransaction transaction, AssignmentInfo assignment)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO assignments (id, profile_id, target, created_at, created_by) VALUES (@id, @profile, @target, @now, @user);",
            ("@id", assignment.Id),
            ("@profile", assignment.ProfileId),
            ("@target", assignment.Target),
            ("@now", HubSql.ToStored(assignment.CreatedAt)),
            ("@user", assignment.CreatedBy));
    }

    public static void DeleteAssignment(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        HubSql.Execute(connection, transaction, "DELETE FROM assignments WHERE id = @id;", ("@id", id));
    }

    // Generations -------------------------------------------------------------------------------------------------------------------

    public static AgentGeneration? FindGeneration(SqliteConnection connection, SqliteTransaction? transaction, string agentId)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT agent_id, generation, content_hash, policy, items, signature, updated_at FROM agent_desired WHERE agent_id = @id;",
            ("@id", agentId));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? ReadGeneration(reader) : null;
    }

    public static Dictionary<string, AgentGeneration> ListGenerations(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT agent_id, generation, content_hash, policy, items, signature, updated_at FROM agent_desired;");
        using SqliteDataReader reader = command.ExecuteReader();
        var generations = new Dictionary<string, AgentGeneration>(StringComparer.Ordinal);
        while (reader.Read())
        {
            AgentGeneration generation = ReadGeneration(reader);
            generations[generation.AgentId] = generation;
        }

        return generations;
    }

    public static void SaveGeneration(SqliteConnection connection, SqliteTransaction transaction, AgentGeneration generation)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO agent_desired (agent_id, generation, content_hash, policy, items, signature, updated_at) "
            + "VALUES (@id, @generation, @hash, @policy, @items, @signature, @now) "
            + "ON CONFLICT(agent_id) DO UPDATE SET generation = excluded.generation, content_hash = excluded.content_hash, policy = excluded.policy, "
            + "items = excluded.items, signature = excluded.signature, updated_at = excluded.updated_at;",
            ("@id", generation.AgentId),
            ("@generation", generation.Generation),
            ("@hash", generation.ContentHash),
            ("@policy", JsonSerializer.Serialize(generation.Policy, ConfigStorageJsonContext.Default.PolicyInfo)),
            ("@items", JsonSerializer.Serialize(generation.Items.ToArray(), ConfigStorageJsonContext.Default.DesiredItemArray)),
            ("@signature", generation.Signature),
            ("@now", HubSql.ToStored(generation.UpdatedAt)));
    }

    /// <summary>Forgets the generations of agents that are no longer in the registry.</summary>
    public static void PruneGenerations(SqliteConnection connection, SqliteTransaction transaction, HashSet<string> knownAgents)
    {
        var stale = new List<string>();
        using (SqliteCommand command = HubSql.Command(connection, transaction, "SELECT agent_id FROM agent_desired;"))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string agentId = reader.GetString(0);
                if (!knownAgents.Contains(agentId))
                {
                    stale.Add(agentId);
                }
            }
        }

        foreach (string agentId in stale)
        {
            HubSql.Execute(connection, transaction, "DELETE FROM agent_desired WHERE agent_id = @id;", ("@id", agentId));
        }
    }

    // Blobs -------------------------------------------------------------------------------------------------------------------------

    public static void SaveBlob(SqliteConnection connection, SqliteTransaction transaction, string kind, string hash, JsonElement body)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO config_blobs (hash, kind, body) VALUES (@hash, @kind, @body);",
            ("@hash", hash),
            ("@kind", kind),
            ("@body", body.GetRawText()));
    }

    public static HubDocument? FindBlob(SqliteConnection connection, SqliteTransaction? transaction, string kind, string id, string hash)
    {
        object? body = HubSql.Scalar(
            connection,
            transaction,
            "SELECT body FROM config_blobs WHERE hash = @hash AND kind = @kind;",
            ("@hash", hash),
            ("@kind", kind));
        return body is string text ? new HubDocument(kind, id, hash, ConfigJson.Parse(text)) : null;
    }

    // Reading -----------------------------------------------------------------------------------------------------------------------

    private static LibraryRow ReadLibrary(SqliteDataReader reader)
    {
        return new LibraryRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            ConfigJson.Parse(reader.GetString(6)),
            HubSql.FromStored(reader.GetInt64(7)),
            HubSql.FromStored(reader.GetInt64(8)),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : HubSql.FromStored(reader.GetInt64(10)));
    }

    private static ProfileRow ReadProfile(SqliteDataReader reader)
    {
        string draftText = reader.GetString(1);
        return new ProfileRow(
            reader.GetString(0),
            ReadContent(draftText),
            draftText,
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.GetInt32(3),
            HubSql.FromStored(reader.GetInt64(4)),
            HubSql.FromStored(reader.GetInt64(5)));
    }

    private static ProfileRevisionRow ReadProfileRevision(SqliteDataReader reader)
    {
        string contentText = reader.GetString(2);
        return new ProfileRevisionRow(
            reader.GetString(0),
            reader.GetInt32(1),
            ReadContent(contentText),
            contentText,
            HubSql.FromStored(reader.GetInt64(3)),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static ProfileContent ReadContent(string text)
    {
        return JsonSerializer.Deserialize(text, ConfigStorageJsonContext.Default.ProfileContent)
            ?? throw new InvalidOperationException("The stored profile content is empty.");
    }

    private static AssignmentInfo ReadAssignment(SqliteDataReader reader)
    {
        return new AssignmentInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            HubSql.FromStored(reader.GetInt64(3)),
            reader.GetString(4));
    }

    private static AgentGeneration ReadGeneration(SqliteDataReader reader)
    {
        return new AgentGeneration(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetString(2),
            JsonSerializer.Deserialize(reader.GetString(3), ConfigStorageJsonContext.Default.PolicyInfo)
                ?? throw new InvalidOperationException("The stored policy is empty."),
            JsonSerializer.Deserialize(reader.GetString(4), ConfigStorageJsonContext.Default.DesiredItemArray) ?? [],
            reader.GetString(5),
            HubSql.FromStored(reader.GetInt64(6)));
    }

    /// <summary>Serializes the content of a profile the way it is stored and compared.</summary>
    public static string WriteContent(ProfileContent content)
    {
        return JsonSerializer.Serialize(content, ConfigStorageJsonContext.Default.ProfileContent);
    }
}
