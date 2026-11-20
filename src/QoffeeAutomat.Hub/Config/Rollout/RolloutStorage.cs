using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Config.Rollout;

/// <summary>System.Text.Json source-generation context for the per-document results that the hub stores about an agent. No reflection-based serialization is used (PLAN.md S14).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(ConfigItemResult[]))]
internal sealed partial class RolloutStorageJsonContext : JsonSerializerContext
{
}

/// <summary>The SQL of the table <c>agent_config_status</c> (migration 6). Statements are constants; values travel as parameters.</summary>
internal static class RolloutStorage
{
    private const string Columns =
        "agent_id, pushed_generation, pushed_at, delivery_error, applied_generation, applied_at, applied_results, reported_generation, reported_state, reported_at, reported_results";

    public static AgentConfigRow? Find(SqliteConnection connection, SqliteTransaction? transaction, string agentId)
    {
        using SqliteCommand command = HubSql.Command(
            connection,
            transaction,
            "SELECT " + Columns + " FROM agent_config_status WHERE agent_id = @id;",
            ("@id", agentId));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public static Dictionary<string, AgentConfigRow> List(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand command = HubSql.Command(connection, transaction, "SELECT " + Columns + " FROM agent_config_status;");
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<string, AgentConfigRow>(StringComparer.Ordinal);
        while (reader.Read())
        {
            AgentConfigRow row = Read(reader);
            rows[row.AgentId] = row;
        }

        return rows;
    }

    public static void Save(SqliteConnection connection, SqliteTransaction transaction, AgentConfigRow row)
    {
        HubSql.Execute(
            connection,
            transaction,
            "INSERT INTO agent_config_status (" + Columns + ") "
            + "VALUES (@id, @pushedGeneration, @pushedAt, @deliveryError, @appliedGeneration, @appliedAt, @appliedResults, @reportedGeneration, @reportedState, @reportedAt, @reportedResults) "
            + "ON CONFLICT(agent_id) DO UPDATE SET pushed_generation = excluded.pushed_generation, pushed_at = excluded.pushed_at, "
            + "delivery_error = excluded.delivery_error, applied_generation = excluded.applied_generation, applied_at = excluded.applied_at, "
            + "applied_results = excluded.applied_results, reported_generation = excluded.reported_generation, reported_state = excluded.reported_state, "
            + "reported_at = excluded.reported_at, reported_results = excluded.reported_results;",
            ("@id", row.AgentId),
            ("@pushedGeneration", row.PushedGeneration),
            ("@pushedAt", row.PushedAt is { } pushedAt ? (object)HubSql.ToStored(pushedAt) : null),
            ("@deliveryError", row.DeliveryError),
            ("@appliedGeneration", row.AppliedGeneration),
            ("@appliedAt", row.AppliedAt is { } appliedAt ? (object)HubSql.ToStored(appliedAt) : null),
            ("@appliedResults", WriteResults(row.AppliedResults)),
            ("@reportedGeneration", row.ReportedGeneration),
            ("@reportedState", row.ReportedState),
            ("@reportedAt", row.ReportedAt is { } reportedAt ? (object)HubSql.ToStored(reportedAt) : null),
            ("@reportedResults", WriteResults(row.ReportedResults)));
    }

    public static void Delete(SqliteConnection connection, SqliteTransaction? transaction, string agentId)
    {
        HubSql.Execute(connection, transaction, "DELETE FROM agent_config_status WHERE agent_id = @id;", ("@id", agentId));
    }

    private static AgentConfigRow Read(SqliteDataReader reader)
    {
        return new AgentConfigRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : HubSql.FromStored(reader.GetInt64(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : HubSql.FromStored(reader.GetInt64(5)),
            ReadResults(reader.GetString(6)),
            reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : HubSql.FromStored(reader.GetInt64(9)),
            ReadResults(reader.GetString(10)));
    }

    private static string WriteResults(IReadOnlyList<ConfigItemResult> results)
    {
        return JsonSerializer.Serialize(results.ToArray(), RolloutStorageJsonContext.Default.ConfigItemResultArray);
    }

    private static ConfigItemResult[] ReadResults(string text)
    {
        return JsonSerializer.Deserialize(text, RolloutStorageJsonContext.Default.ConfigItemResultArray) ?? [];
    }
}
