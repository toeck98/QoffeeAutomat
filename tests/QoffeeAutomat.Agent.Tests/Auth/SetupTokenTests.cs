using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>The setup token file and the <c>reset-password</c> verb (T212, PLAN.md S09 item 1).</summary>
[Trait("Category", "Auth")]
public sealed class SetupTokenTests : IDisposable
{
    private readonly TempDataDirectory _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Creating_the_token_writes_a_random_value_that_verifies()
    {
        var service = new SetupTokenService(AppPaths.ForRoot(_data.Path));
        Assert.False(service.Exists());

        await service.CreateAsync(CancellationToken.None);

        Assert.True(service.Exists());
        string token = (await File.ReadAllTextAsync(service.FilePath)).Trim();
        Assert.Equal(32, token.Length);
        Assert.True(await service.VerifyAsync(token, CancellationToken.None));
        Assert.False(await service.VerifyAsync(token + "x", CancellationToken.None));
        Assert.False(await service.VerifyAsync(string.Empty, CancellationToken.None));
        Assert.False(await service.VerifyAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task A_new_token_replaces_the_old_one()
    {
        var service = new SetupTokenService(AppPaths.ForRoot(_data.Path));
        await service.CreateAsync(CancellationToken.None);
        string old = (await File.ReadAllTextAsync(service.FilePath)).Trim();

        await service.CreateAsync(CancellationToken.None);

        Assert.False(await service.VerifyAsync(old, CancellationToken.None));
        Assert.Single(Directory.GetFiles(Path.Combine(_data.Path, "secrets")));
    }

    [Fact]
    public async Task The_token_file_is_readable_by_the_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows the secrets directory ACL does that (S05).
            return;
        }

        var service = new SetupTokenService(AppPaths.ForRoot(_data.Path));
        await service.CreateAsync(CancellationToken.None);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(service.FilePath));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(_data.Path, "secrets")));
    }

    [Fact]
    public async Task Deleting_removes_the_file_and_verification_fails()
    {
        var service = new SetupTokenService(AppPaths.ForRoot(_data.Path));
        await service.CreateAsync(CancellationToken.None);
        string token = (await File.ReadAllTextAsync(service.FilePath)).Trim();

        service.Delete();
        service.Delete();

        Assert.False(service.Exists());
        Assert.False(await service.VerifyAsync(token, CancellationToken.None));
    }

    [Fact]
    public void The_command_line_knows_reset_password()
    {
        AgentOptions options = CommandLine.Parse(["reset-password", "--data-dir", "/tmp/x"]);

        Assert.Null(options.Error);
        Assert.Equal(AgentVerb.ResetPassword, options.Verb);
        Assert.Equal("/tmp/x", options.DataDir);
        Assert.NotNull(CommandLine.Parse(["reset-password", "extra"]).Error);
        Assert.Contains("reset-password", CommandLine.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_password_without_administrator_rights_is_refused_and_writes_nothing()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var options = new AgentOptions(AgentVerb.ResetPassword, DataDir: _data.Path);

        int code = await ResetPasswordCommand.RunAsync(options, output, error, () => false);

        Assert.Equal(ExitCodes.NoPermission, code);
        Assert.Contains("administrator", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(output.ToString());
        Assert.False(File.Exists(Path.Combine(_data.Path, "secrets", SetupTokenService.FileName)));
    }

    [Fact]
    public async Task Reset_password_as_administrator_recreates_the_setup_token()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var options = new AgentOptions(AgentVerb.ResetPassword, DataDir: _data.Path);
        string path = Path.Combine(_data.Path, "secrets", SetupTokenService.FileName);

        int code = await ResetPasswordCommand.RunAsync(options, output, error, () => true);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Empty(error.ToString());
        Assert.True(File.Exists(path));
        string token = (await File.ReadAllTextAsync(path)).Trim();
        Assert.Contains(path, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, output.ToString(), StringComparison.Ordinal);

        // A second run replaces the token.
        using var again = new StringWriter();
        Assert.Equal(ExitCodes.Success, await ResetPasswordCommand.RunAsync(options, again, error, () => true));
        Assert.NotEqual(token, (await File.ReadAllTextAsync(path)).Trim());
    }
}
