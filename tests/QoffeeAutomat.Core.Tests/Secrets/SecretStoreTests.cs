using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Core.Tests.Secrets;

[Trait("Category", "Secrets")]
public sealed class SecretStoreTests
{
    private const string Secret = "S3cr3t-Value-that-must-not-appear-in-the-file";

    [Fact]
    public async Task A_string_secret_round_trips()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets", "secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets", "secrets.json"), protector);

        await store.SetStringAsync(SecretNames.PasswordHash, Secret, CancellationToken.None);

        Assert.Equal(Secret, await store.GetStringAsync(SecretNames.PasswordHash, CancellationToken.None));
        Assert.Null(await store.GetStringAsync(SecretNames.LinkTokenHashes, CancellationToken.None));
        Assert.Null(await store.GetBytesAsync(SecretNames.TlsCertificate, CancellationToken.None));
    }

    [Fact]
    public async Task Binary_secrets_round_trip_such_as_a_certificate_with_its_private_key()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets.json"), protector);
        byte[] pfx = new byte[2048];
        RandomNumberGenerator.Fill(pfx);

        await store.SetBytesAsync(SecretNames.TlsCertificate, pfx, CancellationToken.None);
        byte[]? read = await store.GetBytesAsync(SecretNames.TlsCertificate, CancellationToken.None);

        Assert.Equal(pfx, read);
    }

    [Fact]
    public async Task Secrets_are_not_readable_in_plain_text_in_the_file()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets", "secrets.json");
        using var protector = new FileKeyProtector(temp.Combine("secrets", "secrets.key"));
        using var store = new SecretStore(file, protector);
        byte[] binary = Encoding.UTF8.GetBytes(Secret);

        await store.SetStringAsync(SecretNames.PasswordHash, Secret, CancellationToken.None);
        await store.SetBytesAsync("script.token", binary, CancellationToken.None);

        byte[] raw = await File.ReadAllBytesAsync(file);
        string text = Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("S3cr3t", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(binary), text, StringComparison.Ordinal);
        Assert.False(raw.AsSpan().IndexOf(binary) >= 0);

        // The structure is plain JSON: names and the protector scheme are visible, values are not.
        using JsonDocument json = JsonDocument.Parse(raw);
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(FileKeyProtector.SchemeName, json.RootElement.GetProperty("protector").GetString());
        Assert.True(json.RootElement.GetProperty("secrets").TryGetProperty(SecretNames.PasswordHash, out _));
    }

    [Fact]
    public async Task Secrets_survive_a_restart_with_the_same_key()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets", "secrets.json");
        string key = temp.Combine("secrets", "secrets.key");
        using (var protector = new FileKeyProtector(key))
        using (var store = new SecretStore(file, protector))
        {
            await store.SetStringAsync(SecretNames.PasswordHash, Secret, CancellationToken.None);
            await store.SetStringAsync(SecretNames.LinkTokenHashes, "hashes", CancellationToken.None);
        }

        using var protector2 = new FileKeyProtector(key);
        using var store2 = new SecretStore(file, protector2);

        Assert.Equal(Secret, await store2.GetStringAsync(SecretNames.PasswordHash, CancellationToken.None));
        Assert.Equal("hashes", await store2.GetStringAsync(SecretNames.LinkTokenHashes, CancellationToken.None));
    }

    [Fact]
    public async Task Secrets_cannot_be_read_with_another_key()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        using (var protector = new FileKeyProtector(temp.Combine("one.key")))
        using (var store = new SecretStore(file, protector))
        {
            await store.SetStringAsync(SecretNames.PasswordHash, Secret, CancellationToken.None);
        }

        using var other = new FileKeyProtector(temp.Combine("two.key"));
        using var otherStore = new SecretStore(file, other);

        SecretStoreException exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => otherStore.GetStringAsync(SecretNames.PasswordHash, CancellationToken.None));
        Assert.Equal(SecretStoreFailure.DecryptionFailed, exception.Reason);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_moved_to_another_name_is_not_accepted()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using (var store = new SecretStore(file, protector))
        {
            await store.SetStringAsync("admin.token", "admin", CancellationToken.None);
            await store.SetStringAsync("guest.token", "guest", CancellationToken.None);
        }

        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();
        JsonObject secrets = root["secrets"]!.AsObject();
        string admin = secrets["admin.token"]!.GetValue<string>();
        string guest = secrets["guest.token"]!.GetValue<string>();
        secrets["admin.token"] = guest;
        secrets["guest.token"] = admin;
        await File.WriteAllTextAsync(file, root.ToJsonString());

        using var tampered = new SecretStore(file, protector);

        SecretStoreException exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => tampered.GetStringAsync("admin.token", CancellationToken.None));
        Assert.Equal(SecretStoreFailure.DecryptionFailed, exception.Reason);
    }

    [Fact]
    public async Task Names_can_be_listed_and_secrets_removed()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets.json"), protector);
        await store.SetStringAsync("script.b", "2", CancellationToken.None);
        await store.SetStringAsync("script.a", "1", CancellationToken.None);

        string[] expectedBoth = ["script.a", "script.b"];
        Assert.Equal(expectedBoth, await store.ListNamesAsync(CancellationToken.None));
        Assert.True(await store.RemoveAsync("script.a", CancellationToken.None));
        Assert.False(await store.RemoveAsync("script.a", CancellationToken.None));

        string[] expectedOne = ["script.b"];
        Assert.Equal(expectedOne, await store.ListNamesAsync(CancellationToken.None));
        Assert.Null(await store.GetStringAsync("script.a", CancellationToken.None));

        using var reopened = new SecretStore(temp.Combine("secrets.json"), protector);
        Assert.Equal(expectedOne, await reopened.ListNamesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Setting_a_secret_again_replaces_it()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets.json"), protector);

        await store.SetStringAsync("auth.password_hash", "old", CancellationToken.None);
        await store.SetStringAsync("auth.password_hash", "new", CancellationToken.None);

        Assert.Equal("new", await store.GetStringAsync("auth.password_hash", CancellationToken.None));
        Assert.Single(await store.ListNamesAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("1starts_with_digit")]
    public async Task Invalid_secret_names_are_rejected(string name)
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets.json"), protector);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SetStringAsync(name, "x", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetStringAsync(name, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RemoveAsync(name, CancellationToken.None));
        Assert.False(SecretNames.IsValid(name));
        Assert.False(File.Exists(temp.Combine("secrets.json")));
    }

    [Fact]
    public void The_well_known_names_are_valid()
    {
        Assert.True(SecretNames.IsValid(SecretNames.PasswordHash));
        Assert.True(SecretNames.IsValid(SecretNames.LinkTokenHashes));
        Assert.True(SecretNames.IsValid(SecretNames.TlsCertificate));
        Assert.True(SecretNames.IsValid(SecretNames.ScriptPrefix + "api_key"));
    }

    [Fact]
    public async Task A_file_that_cannot_be_read_is_never_overwritten()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        const string damaged = "{ this was a secrets file";
        await File.WriteAllTextAsync(file, damaged);
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(file, protector);

        SecretStoreException read = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.GetStringAsync("auth.password_hash", CancellationToken.None));
        SecretStoreException write = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.SetStringAsync("auth.password_hash", "new", CancellationToken.None));

        Assert.Equal(SecretStoreFailure.UnreadableStore, read.Reason);
        Assert.Equal(SecretStoreFailure.UnreadableStore, write.Reason);
        Assert.Equal(damaged, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task A_file_with_an_unknown_format_version_is_left_untouched()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        const string future = """{ "schemaVersion": 7, "protector": "file-key-aes256gcm", "secrets": {} }""";
        await File.WriteAllTextAsync(file, future);
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(file, protector);

        SecretStoreException exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.SetStringAsync("auth.password_hash", "x", CancellationToken.None));

        Assert.Equal(SecretStoreFailure.UnreadableStore, exception.Reason);
        Assert.Equal(future, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task A_file_written_by_another_protector_scheme_is_not_read_with_this_one()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        const string dpapi = """{ "schemaVersion": 1, "protector": "dpapi-machine", "secrets": { "auth.password_hash": "AAAA" } }""";
        await File.WriteAllTextAsync(file, dpapi);
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(file, protector);

        SecretStoreException exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.GetStringAsync("auth.password_hash", CancellationToken.None));

        Assert.Equal(SecretStoreFailure.ProtectorMismatch, exception.Reason);
        Assert.Equal(dpapi, await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task On_Unix_the_secrets_file_is_created_for_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        string file = temp.Combine("secrets", "secrets.json");
        using var protector = new FileKeyProtector(temp.Combine("secrets", "secrets.key"));
        using var store = new SecretStore(file, protector);

        await store.SetStringAsync("auth.password_hash", "x", CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Fact]
    public async Task A_crash_during_the_write_keeps_the_previous_secrets()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        var faults = new FaultInjectingFileOperations();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(file, protector, faults);
        await store.SetStringAsync("auth.password_hash", "kept", CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(file);

        faults.TruncateWrite = true;
        await Assert.ThrowsAsync<IOException>(() => store.SetStringAsync("auth.password_hash", "lost", CancellationToken.None));
        faults.TruncateWrite = false;
        faults.FailMove = true;
        await Assert.ThrowsAsync<IOException>(() => store.SetStringAsync("auth.password_hash", "lost", CancellationToken.None));

        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal("kept", await store.GetStringAsync("auth.password_hash", CancellationToken.None));
        using var reopened = new SecretStore(file, protector);
        Assert.Equal("kept", await reopened.GetStringAsync("auth.password_hash", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_writers_do_not_lose_secrets()
    {
        using var temp = new TempDirectory();
        string file = temp.Combine("secrets.json");
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(file, protector);

        await Task.WhenAll(Enumerable.Range(0, 25).Select(
            index => store.SetStringAsync("script.s" + index, "value" + index, CancellationToken.None)));

        using var reopened = new SecretStore(file, protector);
        Assert.Equal(25, (await reopened.ListNamesAsync(CancellationToken.None)).Count);
        Assert.Equal("value7", await reopened.GetStringAsync("script.s7", CancellationToken.None));
    }

    [Fact]
    public async Task Text_that_is_not_utf8_is_reported_instead_of_being_garbled()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        using var store = new SecretStore(temp.Combine("secrets.json"), protector);
        byte[] notText = [0xFF, 0xFE, 0xFD];
        await store.SetBytesAsync("script.binary", notText, CancellationToken.None);

        SecretStoreException exception = await Assert.ThrowsAsync<SecretStoreException>(
            () => store.GetStringAsync("script.binary", CancellationToken.None));

        Assert.Equal(SecretStoreFailure.DecryptionFailed, exception.Reason);
    }
}
