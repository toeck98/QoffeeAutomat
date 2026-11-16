using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>
/// The authenticated local channel of <c>emit</c> (T217): "emit works from a script started by the
/// agent and is rejected for callers without access to the local channel".
/// </summary>
[Trait("Category", "Events")]
public sealed class LocalChannelTests : IDisposable
{
    private static readonly Uri Address = new("http://127.0.0.1:8480/");

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Every_run_gets_its_own_random_secret()
    {
        var first = new LocalChannel();
        var second = new LocalChannel();

        Assert.NotEqual(first.Token, second.Token);
        Assert.True(first.Token.Length >= 43, "32 random bytes encoded as base64url need 43 characters.");
        Assert.DoesNotContain('=', first.Token);
    }

    [Fact]
    public void Only_the_exact_secret_is_accepted()
    {
        var channel = new LocalChannel("s3cret-token");

        Assert.True(channel.Verify("s3cret-token"));
        Assert.False(channel.Verify("s3cret-token "));
        Assert.False(channel.Verify("S3CRET-TOKEN"));
        Assert.False(channel.Verify("s3cret"));
        Assert.False(channel.Verify("s3cret-token-and-more"));
        Assert.False(channel.Verify(string.Empty));
        Assert.False(channel.Verify(null));
    }

    [Fact]
    public void The_environment_is_empty_until_the_agent_listens()
    {
        var channel = new LocalChannel("abc");
        Assert.Empty(channel.ToEnvironment());

        channel.SetBaseAddress(Address);

        IReadOnlyDictionary<string, string> environment = channel.ToEnvironment();
        Assert.Equal(Address.AbsoluteUri, environment[LocalChannelNames.UrlVariable]);
        Assert.Equal("abc", environment[LocalChannelNames.TokenVariable]);
        Assert.Equal(2, environment.Count);
    }

    [Fact]
    public void A_process_started_by_the_agent_finds_the_channel_in_its_environment()
    {
        var channel = new LocalChannel("abc");
        channel.SetBaseAddress(Address);
        IReadOnlyDictionary<string, string> environment = channel.ToEnvironment();

        LocalChannelEndpoint? endpoint = LocalChannelEndpoint.FromEnvironment(name => environment.GetValueOrDefault(name));

        Assert.NotNull(endpoint);
        Assert.Equal(Address, endpoint.BaseAddress);
        Assert.True(channel.Verify(endpoint.Token));
    }

    [Fact]
    public void A_caller_without_the_environment_finds_no_channel()
    {
        Assert.Null(LocalChannelEndpoint.FromEnvironment(_ => null));
        Assert.Null(LocalChannelEndpoint.FromEnvironment(name => name == LocalChannelNames.UrlVariable ? Address.AbsoluteUri : null));
        Assert.Null(LocalChannelEndpoint.FromEnvironment(name => name == LocalChannelNames.TokenVariable ? "abc" : null));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://127.0.0.1/")]
    [InlineData("file:///etc/passwd")]
    public void Only_http_addresses_are_accepted_from_the_environment(string address)
    {
        Assert.Null(LocalChannelEndpoint.FromEnvironment(name => name == LocalChannelNames.UrlVariable ? address : "abc"));
    }

    [Fact]
    public async Task The_file_round_trips_and_an_administrator_can_use_it()
    {
        string path = _temp.Combine("secrets", LocalChannelNames.FileName);
        var channel = new LocalChannel("file-secret");
        channel.SetBaseAddress(Address);

        await channel.WriteFileAsync(path, files: null, CancellationToken.None);
        LocalChannelEndpoint? endpoint = await LocalChannelEndpoint.FromFileAsync(path, CancellationToken.None);

        Assert.NotNull(endpoint);
        Assert.Equal(Address, endpoint.BaseAddress);
        Assert.Equal("file-secret", endpoint.Token);
    }

    [Fact]
    public async Task The_file_is_readable_by_its_owner_only_on_unix()
    {
        // On Windows the secrets directory is restricted by ACL instead (PLAN.md S09, T301).
        if (!OperatingSystem.IsWindows())
        {
            string path = _temp.Combine("secrets", LocalChannelNames.FileName);
            var channel = new LocalChannel("file-secret");
            channel.SetBaseAddress(Address);

            await channel.WriteFileAsync(path, files: null, CancellationToken.None);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task Writing_the_file_before_the_address_is_known_is_an_error()
    {
        var channel = new LocalChannel("x");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => channel.WriteFileAsync(_temp.Combine(LocalChannelNames.FileName), files: null, CancellationToken.None));
    }

    [Fact]
    public async Task A_missing_or_broken_file_yields_no_endpoint()
    {
        string missing = _temp.Combine("nothing.json");
        string broken = _temp.Combine("broken.json");
        string incomplete = _temp.Combine("incomplete.json");
        await File.WriteAllTextAsync(broken, "{ not json");
        await File.WriteAllTextAsync(incomplete, """{"url":"http://127.0.0.1:1/"}""");

        Assert.Null(await LocalChannelEndpoint.FromFileAsync(missing, CancellationToken.None));
        Assert.Null(await LocalChannelEndpoint.FromFileAsync(broken, CancellationToken.None));
        Assert.Null(await LocalChannelEndpoint.FromFileAsync(incomplete, CancellationToken.None));
    }

    [Fact]
    public async Task The_file_can_be_deleted_and_deleting_a_missing_file_is_harmless()
    {
        string path = _temp.Combine(LocalChannelNames.FileName);
        var channel = new LocalChannel("x");
        channel.SetBaseAddress(Address);
        await channel.WriteFileAsync(path, files: null, CancellationToken.None);

        LocalChannel.DeleteFile(path);
        LocalChannel.DeleteFile(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void The_secret_does_not_appear_in_the_text_form_of_the_objects()
    {
        var channel = new LocalChannel("very-secret-value");
        channel.SetBaseAddress(Address);
        var endpoint = new LocalChannelEndpoint(Address, "very-secret-value");

        Assert.DoesNotContain("very-secret-value", channel.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-value", endpoint.ToString(), StringComparison.Ordinal);
    }
}
