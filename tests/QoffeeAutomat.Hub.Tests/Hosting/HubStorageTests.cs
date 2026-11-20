using System.Globalization;
using Microsoft.Data.Sqlite;
using QoffeeAutomat.Hub.Hosting;
using QoffeeAutomat.Hub.Storage;

namespace QoffeeAutomat.Hub.Tests.Hosting;

/// <summary>The database hub.db: versioned schema migrations and the identity of the hub (T902).</summary>
[Trait("Category", "Hosting")]
public sealed class HubStorageTests
{
    /// <summary>Every schema version that a database can have before the latest one, including 0 for a new file.</summary>
    public static IEnumerable<object[]> EarlierVersions()
    {
        return Enumerable.Range(0, HubSchema.LatestVersion).Select(version => new object[] { version });
    }

    private static readonly string[] ExpectedTables = ["hub_meta", "users", "api_tokens", "audit_log"];

    private static SqliteConnection Open(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        object? found = HubSql.Scalar(connection, null, "SELECT name FROM sqlite_master WHERE type = 'table' AND name = @name;", ("@name", name));
        return found is not null;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        // The table name comes from the test's own constants.
        object? count = HubSql.Scalar(connection, null, "SELECT COUNT(*) FROM " + table + ";");
        return Convert.ToInt64(count, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void The_steps_of_the_schema_are_numbered_without_gaps_from_one()
    {
        int[] versions = [.. HubSchema.Migrations.Select(step => step.Version)];

        Assert.Equal(Enumerable.Range(1, versions.Length), versions);
        Assert.Equal(versions[^1], HubSchema.LatestVersion);
        Assert.True(HubSchema.LatestVersion >= 2, "T902 and T903 each add a step.");
    }

    [Theory]
    [MemberData(nameof(EarlierVersions))]
    public void A_database_migrates_forward_from_every_earlier_schema_version(int fromVersion)
    {
        using var directory = new TempDirectory();
        using SqliteConnection connection = Open(directory.Below("hub.db"));

        // A database as an older hub left it, with data in the tables that existed then.
        Assert.Equal(fromVersion, HubMigrator.Migrate(connection, fromVersion));
        if (fromVersion >= 1)
        {
            HubSql.Execute(connection, null, "INSERT INTO hub_meta (key, value) VALUES ('hub_id', 'older-hub');");
        }

        if (fromVersion >= 2)
        {
            HubSql.Execute(
                connection,
                null,
                "INSERT INTO users (id, username, password_hash, role, disabled, created_at, updated_at, password_changed_at) VALUES ('u1', 'old', 'x', 'admin', 0, 1, 1, 1);");
        }

        Assert.Equal(fromVersion, HubMigrator.ReadVersion(connection));

        int reached = HubMigrator.Migrate(connection);

        Assert.Equal(HubSchema.LatestVersion, reached);
        Assert.Equal(HubSchema.LatestVersion, HubMigrator.ReadVersion(connection));
        foreach (string table in ExpectedTables)
        {
            Assert.True(TableExists(connection, table), "The table " + table + " is missing after the migration from version " + fromVersion.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (fromVersion >= 1)
        {
            Assert.Equal("older-hub", HubSql.Scalar(connection, null, "SELECT value FROM hub_meta WHERE key = 'hub_id';"));
        }

        if (fromVersion >= 2)
        {
            Assert.Equal(1L, Count(connection, "users"));
        }
    }

    [Fact]
    public void Migrating_a_current_database_again_changes_nothing()
    {
        using var directory = new TempDirectory();
        using SqliteConnection connection = Open(directory.Below("hub.db"));
        HubMigrator.Migrate(connection);
        HubSql.Execute(connection, null, "INSERT INTO hub_meta (key, value) VALUES ('hub_id', 'abc');");

        Assert.Equal(HubSchema.LatestVersion, HubMigrator.Migrate(connection));
        Assert.Equal(1L, Count(connection, "hub_meta"));
    }

    [Fact]
    public void Opening_a_database_of_an_old_version_migrates_it()
    {
        using var directory = new TempDirectory();
        string path = directory.Below("hub.db");
        using (SqliteConnection connection = Open(path))
        {
            HubMigrator.Migrate(connection, 1);
        }

        using HubDatabase database = HubDatabase.Open(path);

        Assert.Equal(1, database.InitialSchemaVersion);
        Assert.Equal(HubSchema.LatestVersion, database.SchemaVersion);
    }

    [Fact]
    public void A_new_database_starts_at_version_zero_and_a_second_start_runs_no_migration()
    {
        using var directory = new TempDirectory();
        string path = directory.Below("hub.db");

        using (HubDatabase first = HubDatabase.Open(path))
        {
            Assert.Equal(0, first.InitialSchemaVersion);
            Assert.Equal(HubSchema.LatestVersion, first.SchemaVersion);
        }

        using HubDatabase second = HubDatabase.Open(path);
        Assert.Equal(HubSchema.LatestVersion, second.InitialSchemaVersion);
    }

    [Fact]
    public void A_database_of_a_newer_hub_is_refused_and_left_alone()
    {
        using var directory = new TempDirectory();
        string path = directory.Below("hub.db");
        int newer = HubSchema.LatestVersion + 1;
        using (SqliteConnection connection = Open(path))
        {
            HubMigrator.Migrate(connection);
            HubSql.Execute(connection, null, "PRAGMA user_version = " + newer.ToString(CultureInfo.InvariantCulture) + ";");
        }

        var exception = Assert.Throws<HubStartupException>(() => HubDatabase.Open(path));

        Assert.Contains("newer hub", exception.Message, StringComparison.Ordinal);
        using SqliteConnection check = Open(path);
        Assert.Equal(newer, HubMigrator.ReadVersion(check));
    }

    [Fact]
    public void A_failing_step_is_rolled_back_completely()
    {
        using var directory = new TempDirectory();
        using SqliteConnection connection = Open(directory.Below("hub.db"));
        HubMigrator.Migrate(connection, 1);

        // The step to version 2 creates users, api_tokens and audit_log; a table of that name in the way makes it fail half way.
        HubSql.Execute(connection, null, "CREATE TABLE audit_log (in_the_way INTEGER);");

        Assert.Throws<HubStartupException>(() => HubMigrator.Migrate(connection));

        Assert.Equal(1, HubMigrator.ReadVersion(connection));
        Assert.False(TableExists(connection, "users"));
        Assert.False(TableExists(connection, "api_tokens"));
    }

    [Fact]
    public void The_database_file_is_only_accessible_by_its_owner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        string path = directory.Below("hub.db");

        using (HubDatabase.Open(path))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void The_identity_is_created_once_and_never_changes()
    {
        using var directory = new TempDirectory();
        string path = directory.Below("hub.db");
        var time = new ManualTimeProvider();

        HubIdentity first;
        using (HubDatabase database = HubDatabase.Open(path))
        {
            first = HubIdentityStore.LoadOrCreate(database, "  Living room hub  ", time);
        }

        time.Advance(TimeSpan.FromDays(30));
        using HubDatabase again = HubDatabase.Open(path);
        HubIdentity second = HubIdentityStore.LoadOrCreate(again, "A different name", time);

        Assert.Matches("^[0-9a-f]{32}$", first.Id);
        Assert.Equal("Living room hub", first.Name);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData(null, "QoffeeAutomat Hub")]
    [InlineData("   ", "QoffeeAutomat Hub")]
    [InlineData("  Hub  ", "Hub")]
    public void A_name_is_trimmed_and_defaults_when_empty(string? given, string expected)
    {
        Assert.Equal(expected, HubIdentityStore.NormalizeName(given));
    }

    [Fact]
    public void A_name_is_limited_in_length()
    {
        string name = HubIdentityStore.NormalizeName(new string('x', 200));

        Assert.Equal(HubIdentityStore.MaxNameLength, name.Length);
    }
}
