using System.Text;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;
using static QoffeeAutomat.Core.Tests.Config.ConfigTestSupport;

namespace QoffeeAutomat.Core.Tests.Config;

[Trait("Category", "Config")]
public sealed class ConfigScriptTests
{
    [Fact]
    public async Task A_script_can_be_saved_read_listed_and_deleted()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        ScriptResult saved = await store.Local.SaveScriptAsync("set_display_profile.ps1", "param($Profile)\nWrite-Output $Profile", null, CancellationToken.None);
        ScriptFile? read = await store.Effective.GetScriptAsync("set_display_profile.ps1", CancellationToken.None);
        IReadOnlyList<ScriptInfo> listed = await store.Effective.ListScriptsAsync(CancellationToken.None);

        Assert.True(saved.IsApplied);
        Assert.NotNull(read);
        Assert.Equal("param($Profile)\nWrite-Output $Profile", read.Content);
        Assert.Equal(saved.ETag, read.ETag);
        Assert.Equal(64, read.ETag.Length);
        Assert.Equal("set_display_profile.ps1", Assert.Single(listed).Name);
        Assert.Equal(saved.ETag, listed[0].ETag);
        Assert.True(File.Exists(temp.Combine("scripts", "set_display_profile.ps1")));

        ScriptResult deleted = await store.Local.DeleteScriptAsync("set_display_profile.ps1", saved.ETag, CancellationToken.None);

        Assert.True(deleted.IsApplied);
        Assert.Null(await store.Effective.GetScriptAsync("set_display_profile.ps1", CancellationToken.None));
        Assert.Empty(await store.Effective.ListScriptsAsync(CancellationToken.None));
        Assert.False(File.Exists(temp.Combine("scripts", "set_display_profile.ps1")));
    }

    [Fact]
    public async Task The_etag_changes_with_the_content_and_guards_against_stale_writes()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        ScriptResult first = await store.Local.SaveScriptAsync("a.ps1", "one", null, CancellationToken.None);
        ScriptResult second = await store.Local.SaveScriptAsync("a.ps1", "two", first.ETag, CancellationToken.None);

        ScriptResult stale = await store.Local.SaveScriptAsync("a.ps1", "three", first.ETag, CancellationToken.None);
        ScriptResult staleDelete = await store.Local.DeleteScriptAsync("a.ps1", first.ETag, CancellationToken.None);
        ScriptResult missing = await store.Local.DeleteScriptAsync("nothing.ps1", null, CancellationToken.None);
        ScriptResult createWithEtag = await store.Local.SaveScriptAsync("new.ps1", "x", first.ETag, CancellationToken.None);

        Assert.NotEqual(first.ETag, second.ETag);
        Assert.Equal(ChangeStatus.Conflict, stale.Status);
        Assert.Equal(second.ETag, stale.ETag);
        Assert.Equal(ChangeStatus.Conflict, staleDelete.Status);
        Assert.Equal(ChangeStatus.NotFound, missing.Status);
        Assert.Equal(ChangeStatus.Conflict, createWithEtag.Status);
        Assert.Equal("two", (await store.Effective.GetScriptAsync("a.ps1", CancellationToken.None))!.Content);
    }

    [Theory]
    [InlineData("../evil.ps1")]
    [InlineData("..\\evil.ps1")]
    [InlineData("sub/evil.ps1")]
    [InlineData("sub\\evil.ps1")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\evil.ps1")]
    [InlineData("c:evil.ps1")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a..b.ps1")]
    [InlineData("")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("with space.ps1")]
    [InlineData("tab\t.ps1")]
    [InlineData("nul")]
    [InlineData("CON.ps1")]
    [InlineData("lpt1.txt")]
    [InlineData("backup.tmp")]
    [InlineData("%2e%2e%2fevil.ps1")]
    public async Task Script_names_with_path_separators_or_dots_dots_are_rejected(string name)
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        ScriptResult save = await store.Local.SaveScriptAsync(name, "Write-Output 'pwned'", null, CancellationToken.None);
        ScriptResult delete = await store.Local.DeleteScriptAsync(name, null, CancellationToken.None);
        ScriptFile? read = await store.Effective.GetScriptAsync(name, CancellationToken.None);

        Assert.Equal(ChangeStatus.Invalid, save.Status);
        Assert.Equal(ChangeStatus.Invalid, delete.Status);
        Assert.Contains(save.Errors, error => error.Path == "/name");
        Assert.Null(read);
        Assert.False(ScriptNames.IsValid(name));

        // Nothing was written anywhere below the temporary root except the (empty) scripts directory.
        Assert.Empty(Directory.GetFiles(temp.FullPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_name_longer_than_128_characters_is_rejected()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        string longest = new string('a', 124) + ".ps1";
        string tooLong = new string('a', 125) + ".ps1";

        Assert.True((await store.Local.SaveScriptAsync(longest, "x", null, CancellationToken.None)).IsApplied);
        Assert.Equal(ChangeStatus.Invalid, (await store.Local.SaveScriptAsync(tooLong, "x", null, CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData("set_display_profile.ps1")]
    [InlineData("run.sh")]
    [InlineData("Run-Me_1.cmd")]
    [InlineData("a")]
    [InlineData("9lives.py")]
    [InlineData("version-1.2.3.ps1")]
    public void Plain_file_names_are_valid_script_names(string name)
    {
        Assert.True(ScriptNames.IsValid(name));
        Assert.Empty(ScriptNames.Validate(name, "/name"));
    }

    [Fact]
    public async Task A_script_is_stored_as_utf8_without_a_byte_order_mark_and_read_back_without_one()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));
        await store.Local.SaveScriptAsync("umlauts.ps1", "Write-Output 'Grüße'", null, CancellationToken.None);

        byte[] bytes = await File.ReadAllBytesAsync(temp.Combine("scripts", "umlauts.ps1"));
        Assert.Equal((byte)'W', bytes[0]);
        Assert.Equal("Write-Output 'Grüße'", Encoding.UTF8.GetString(bytes));

        byte[] withBom = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("Write-Output 1")];
        await File.WriteAllBytesAsync(temp.Combine("scripts", "bom.ps1"), withBom);
        ScriptFile? read = await store.Effective.GetScriptAsync("bom.ps1", CancellationToken.None);

        Assert.Equal("Write-Output 1", read!.Content);
    }

    [Fact]
    public async Task A_script_larger_than_one_mebibyte_is_rejected()
    {
        using var temp = new TempDirectory();
        using ConfigStore store = await OpenAsync(Options(temp));

        ScriptResult result = await store.Local.SaveScriptAsync("big.ps1", new string('x', (1024 * 1024) + 1), null, CancellationToken.None);

        Assert.Equal(ChangeStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, error => error.Path == "/content" && error.Code == ValidationCodes.Invalid);
        Assert.False(File.Exists(temp.Combine("scripts", "big.ps1")));
    }

    [Fact]
    public async Task Scripts_publish_changes_and_survive_a_restart()
    {
        using var temp = new TempDirectory();
        ConfigStore store = await OpenAsync(Options(temp));
        using ConfigSubscription subscription = store.Subscribe();

        await store.Local.SaveScriptAsync("a.ps1", "one", null, CancellationToken.None);
        await store.Local.SaveScriptAsync("a.ps1", "two", null, CancellationToken.None);
        await store.Local.DeleteScriptAsync("a.ps1", null, CancellationToken.None);
        await store.Local.SaveScriptAsync("b.ps1", "keep", null, CancellationToken.None);
        store.Dispose();

        var changes = new List<ConfigChange>();
        await foreach (ConfigChange change in subscription.Changes.ReadAllAsync())
        {
            changes.Add(change);
        }

        Assert.All(changes, change => Assert.Equal(ConfigKind.Script, change.Kind));
        ConfigChangeType[] expectedTypes = [ConfigChangeType.Added, ConfigChangeType.Updated, ConfigChangeType.Removed, ConfigChangeType.Added];
        Assert.Equal(expectedTypes, changes.Select(change => change.Type));

        using ConfigStore reopened = await OpenAsync(Options(temp));
        Assert.Equal("b.ps1", Assert.Single(await reopened.Effective.ListScriptsAsync(CancellationToken.None)).Name);
    }
}
