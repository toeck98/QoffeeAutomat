using System.Text;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Secrets.Named;
using QoffeeAutomat.Core.Tests.Commands;
using QoffeeAutomat.Core.Tests.Validation;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Core.Tests.Secrets;

/// <summary>Named secrets of scripts: the store, the masking of output and the run (PLAN.md S09, T706).</summary>
[Trait("Category", "NamedSecrets")]
public sealed class NamedSecretTests
{
    private const string Secret = "Hunter2-Zebra-Quartz";
    private const string OtherSecret = "Second-Value-9876";

    /// <summary>A named secret store on a temporary secrets file.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly FileKeyProtector _protector;

        public Fixture()
        {
            SecretsFile = _temp.Combine("secrets", "secrets.json");
            _protector = new FileKeyProtector(_temp.Combine("secrets", "secrets.key"));
            Inner = new SecretStore(SecretsFile, _protector);
            Named = new NamedSecretStore(Inner);
        }

        public string SecretsFile { get; }

        public SecretStore Inner { get; }

        public NamedSecretStore Named { get; }

        public void Dispose()
        {
            Inner.Dispose();
            _protector.Dispose();
            _temp.Dispose();
        }
    }

    private static CommandDocument WithSecrets(CommandDocument command, params string[] names) => command with { Secrets = names };

    private static async Task<List<RunOutputItem>> ReadAllAsync(RunOutputFeed feed)
    {
        var items = new List<RunOutputItem>();
        await foreach (RunOutputItem item in feed.ReadAsync())
        {
            items.Add(item);
        }

        return items;
    }

    // ------------------------------------------------------------------
    // Names and values
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("api_token", true)]
    [InlineData("a", true)]
    [InlineData("backup_password_2", true)]
    [InlineData("Api_Token", false)]
    [InlineData("api-token", false)]
    [InlineData("api.token", false)]
    [InlineData("1token", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_name_is_a_lower_case_identifier(string? name, bool valid)
    {
        Assert.Equal(valid, NamedSecretNames.IsValid(name));
    }

    [Fact]
    public void A_name_can_be_at_most_63_characters_long()
    {
        Assert.True(NamedSecretNames.IsValid("a" + new string('b', 62)));
        Assert.False(NamedSecretNames.IsValid("a" + new string('b', 63)));
    }

    [Fact]
    public void The_environment_variable_is_the_upper_case_name_with_the_prefix()
    {
        Assert.Equal("QA_SECRET_API_TOKEN", NamedSecretNames.EnvironmentVariable("api_token"));
        Assert.Equal("script.api_token", NamedSecretNames.StoreName("api_token"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void A_value_that_is_missing_or_too_short_to_mask_is_refused(string? value)
    {
        Assert.NotNull(NamedSecretNames.DescribeValueProblem(value));
    }

    [Fact]
    public void A_value_with_a_nul_character_or_beyond_the_limit_is_refused_and_a_normal_one_is_accepted()
    {
        Assert.NotNull(NamedSecretNames.DescribeValueProblem("abcd\0efgh"));
        Assert.NotNull(NamedSecretNames.DescribeValueProblem(new string('x', NamedSecretNames.MaxValueLength + 1)));
        Assert.Null(NamedSecretNames.DescribeValueProblem(Secret));
        Assert.Null(NamedSecretNames.DescribeValueProblem(new string('x', NamedSecretNames.MaxValueLength)));
    }

    [Fact]
    public void The_message_about_a_refused_value_never_repeats_the_value()
    {
        string? message = NamedSecretNames.DescribeValueProblem("abc");

        Assert.DoesNotContain("abc", message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // The store
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_secret_is_created_replaced_listed_and_removed()
    {
        using var fixture = new Fixture();

        Assert.True(await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None));
        Assert.False(await fixture.Named.SetAsync("api_token", OtherSecret, CancellationToken.None));
        Assert.True(await fixture.Named.SetAsync("backup_password", Secret, CancellationToken.None));

        string[] names = ["api_token", "backup_password"];
        Assert.Equal(names, await fixture.Named.ListAsync(CancellationToken.None));
        ResolvedSecrets resolved = await fixture.Named.ResolveAsync(["api_token"], CancellationToken.None);
        Assert.Equal(OtherSecret, resolved.ToEnvironment()["QA_SECRET_API_TOKEN"]);

        Assert.True(await fixture.Named.RemoveAsync("api_token", CancellationToken.None));
        Assert.False(await fixture.Named.RemoveAsync("api_token", CancellationToken.None));
        string[] remaining = ["backup_password"];
        Assert.Equal(remaining, await fixture.Named.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_secrets_of_the_agent_itself_are_not_named_secrets()
    {
        using var fixture = new Fixture();
        await fixture.Inner.SetStringAsync(SecretNames.PasswordHash, "hash-value", CancellationToken.None);
        await fixture.Inner.SetStringAsync(SecretNames.LinkTokenHashes, "[]", CancellationToken.None);
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);

        string[] names = ["api_token"];
        Assert.Equal(names, await fixture.Named.ListAsync(CancellationToken.None));
        ResolvedSecrets probe = await fixture.Named.ResolveAsync(["auth.password_hash", "link.token_hashes"], CancellationToken.None);
        Assert.Equal(0, probe.Count);
        Assert.False(probe.IsComplete);
    }

    [Fact]
    public async Task Invalid_names_and_values_are_not_stored()
    {
        using var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Named.SetAsync("Bad Name", Secret, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Named.SetAsync("api_token", "abc", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Named.RemoveAsync("Bad Name", CancellationToken.None));
        Assert.Empty(await fixture.Named.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_value_is_encrypted_in_the_secrets_file()
    {
        using var fixture = new Fixture();

        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);

        string file = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(fixture.SecretsFile));
        Assert.DoesNotContain(Secret, file, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)), file, StringComparison.Ordinal);
        Assert.Contains("script.api_token", file, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_reports_the_secrets_that_do_not_exist_and_never_prints_a_value()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);

        ResolvedSecrets resolved = await fixture.Named.ResolveAsync(["api_token", "absent", "api_token"], CancellationToken.None);

        string[] missing = ["absent"];
        Assert.False(resolved.IsComplete);
        Assert.Equal(missing, resolved.Missing);
        Assert.Equal(1, resolved.Count);
        Assert.DoesNotContain(Secret, resolved.ToString(), StringComparison.Ordinal);
        Assert.Same(ResolvedSecrets.None, await fixture.Named.ResolveAsync(null, CancellationToken.None));
        Assert.Same(ResolvedSecrets.None, await fixture.Named.ResolveAsync([], CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Masking
    // ------------------------------------------------------------------

    [Fact]
    public void The_masker_replaces_every_occurrence_and_the_longer_value_first()
    {
        var masker = new SecretMasker([Secret, Secret + "-extended", OtherSecret]);

        string masked = masker.Apply($"a {Secret} b {Secret}-extended c {OtherSecret} d");

        Assert.Equal("a *** b *** c *** d", masked);
    }

    [Fact]
    public void The_masker_leaves_text_without_secrets_and_ignores_values_that_are_too_short()
    {
        var masker = new SecretMasker(["abc", Secret]);

        Assert.Equal("abc and nothing else", masker.Apply("abc and nothing else"));
        Assert.Equal("unchanged", SecretMasker.Empty.Apply("unchanged"));
        Assert.True(SecretMasker.Empty.IsEmpty);
        Assert.False(masker.IsEmpty);
        Assert.Equal(string.Empty, masker.Apply(string.Empty));
    }

    // ------------------------------------------------------------------
    // Validation of the secrets list
    // ------------------------------------------------------------------

    [Fact]
    public void A_powershell_or_process_command_may_list_secrets()
    {
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(WithSecrets(Cmd.Gated("c"), "api_token", "backup_password")));
        TestDocuments.AssertValid(DocumentValidator.ValidateCommand(WithSecrets(Cmd.Process("p", "tool.exe"), "api_token")));
    }

    [Fact]
    public void Bad_and_repeated_secret_names_are_reported_with_json_pointers()
    {
        ValidationResult result = DocumentValidator.ValidateCommand(WithSecrets(Cmd.Gated("c"), "api_token", "api_token", "Bad-Name", string.Empty));

        Assert.Contains(result.Errors, error => error.Path == "/secrets/1" && error.Code == ValidationCodes.Duplicate);
        Assert.Contains(result.Errors, error => error.Path == "/secrets/2" && error.Code == ValidationCodes.Invalid);
        Assert.Contains(result.Errors, error => error.Path == "/secrets/3" && error.Code == ValidationCodes.Required);
    }

    [Fact]
    public void Builtin_and_sequence_commands_cannot_list_secrets()
    {
        ValidationResult builtin = DocumentValidator.ValidateCommand(WithSecrets(Cmd.Builtin("b", "power.sleep"), "api_token"));
        ValidationResult sequence = DocumentValidator.ValidateCommand(WithSecrets(Cmd.Sequence("s", SequenceErrorModes.Stop, Cmd.Delay(1)), "api_token"));

        Assert.Contains(builtin.Errors, error => error.Path == "/secrets" && error.Code == ValidationCodes.NotApplicable);
        Assert.Contains(sequence.Errors, error => error.Path == "/secrets" && error.Code == ValidationCodes.NotApplicable);
    }

    // ------------------------------------------------------------------
    // The run
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_listed_secret_reaches_the_process_only_as_an_environment_variable()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);
        await fixture.Named.SetAsync("unused_secret", OtherSecret, CancellationToken.None);
        await using CommandHarness h = await CommandHarness.CreateAsync(secrets: fixture.Named);
        CommandDocument process = Cmd.Process("p", "tool.exe", ["--flag", "{{Name}}"], RunAsValues.System, true, null, true, Cmd.Text("Name"));
        await h.SaveAsync(WithSecrets(process, "api_token"));

        RunResult result = await h.RunAsync("p", ("Name", "visible"));

        Assert.True(result.Succeeded);
        ProcessLaunchRequest request = Assert.Single(h.Runner.Requests);
        Assert.Equal(Secret, request.EnvironmentVariables["QA_SECRET_API_TOKEN"]);
        Assert.DoesNotContain(request.EnvironmentVariables.Keys, key => key.Contains("UNUSED", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Arguments, argument => argument.Contains(Secret, StringComparison.Ordinal));
        Assert.Equal("tool.exe", request.FileName);
    }

    [Fact]
    public async Task A_command_without_secrets_gets_none_even_when_the_store_has_some()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);
        await using CommandHarness h = await CommandHarness.CreateAsync(secrets: fixture.Named);
        h.Gate!.AutoComplete = _ => new ExecutionResult(0, "ok\n", string.Empty);
        await h.SaveAsync(Cmd.Gated("plain"));

        RunResult result = await h.RunAsync("plain");

        Assert.True(result.Succeeded);
        GateCall call = Assert.Single(h.Gate.Calls);
        Assert.DoesNotContain(call.Context.Environment.Keys, key => key.StartsWith(NamedSecretNames.EnvironmentPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_secret_value_that_the_script_prints_is_masked_in_the_result_and_in_the_history()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);
        await fixture.Named.SetAsync("other_token", OtherSecret, CancellationToken.None);
        await using CommandHarness h = await CommandHarness.CreateAsync(secrets: fixture.Named);
        h.Runner.Answer = _ => new ProcessRunResult(
            1,
            $"token={Secret}\nsecond={OtherSecret}\nagain {Secret}\n",
            $"failed with {Secret}",
            TimeSpan.Zero,
            false,
            false);
        await h.SaveAsync(WithSecrets(Cmd.Process("p", "tool.exe"), "api_token", "other_token"));

        RunResult result = await h.RunAsync("p");

        Assert.Equal("token=***\nsecond=***\nagain ***\n", result.StandardOutput);
        Assert.Equal("failed with ***", result.StandardError);
        Assert.DoesNotContain(Secret, result.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
        RunRecord record = Assert.Single(await h.History.QueryAsync(new RunQuery(), CancellationToken.None));
        Assert.Equal("token=***\nsecond=***\nagain ***\n", record.StandardOutput);
        Assert.Equal("failed with ***", record.StandardError);
        string everything = string.Join('\n', record.Command, record.Source, record.ClientId, record.UserId, record.Variables.GetRawText(), record.StandardOutput, record.StandardError);
        Assert.DoesNotContain(Secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherSecret, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_secret_value_that_the_script_prints_is_masked_in_the_streamed_output()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);
        var hub = new RunOutputHub();
        await using CommandHarness h = await CommandHarness.CreateAsync(new CommandEngineOptions { RunOutput = hub }, secrets: fixture.Named);
        await h.SaveAsync(WithSecrets(Cmd.Gated("g"), "api_token"));

        RunHandle handle = await h.StartAsync("g");
        GateCall call = await h.Gate!.NextStartAsync();
        Assert.Equal(Secret, call.Context.Environment["QA_SECRET_API_TOKEN"]);
        RunOutputFeed feed = hub.Find(handle.RunId)!;
        Task<List<RunOutputItem>> reader = ReadAllAsync(feed);
        call.Finish(0, $"the token is {Secret}\n", $"warning: {Secret}");
        await CommandHarness.Finished(handle);
        List<RunOutputItem> items = await reader.WaitAsync(TimeSpan.FromSeconds(10));

        string streamed = string.Concat(items.Where(item => item.Chunk is not null).Select(item => item.Chunk!.Text));
        Assert.DoesNotContain(Secret, streamed, StringComparison.Ordinal);
        Assert.Contains("the token is ***", streamed, StringComparison.Ordinal);
        Assert.Contains("warning: ***", streamed, StringComparison.Ordinal);
        RunResult final = Assert.Single(items, item => item.Final is not null).Final!;
        Assert.DoesNotContain(Secret, final.StandardOutput + final.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_that_lists_a_secret_which_is_not_stored_fails_without_running()
    {
        using var fixture = new Fixture();
        await using CommandHarness h = await CommandHarness.CreateAsync(secrets: fixture.Named);
        await h.SaveAsync(WithSecrets(Cmd.Gated("g"), "absent_secret"));

        RunResult result = await h.RunAsync("g");

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Contains("absent_secret", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(h.Gate!.Calls);
    }

    [Fact]
    public async Task A_command_that_lists_a_secret_fails_when_the_agent_has_no_secret_store()
    {
        await using CommandHarness h = await CommandHarness.CreateAsync();
        await h.SaveAsync(WithSecrets(Cmd.Gated("g"), "api_token"));

        RunResult result = await h.RunAsync("g");

        Assert.Equal(ErrorCodes.Internal, result.ErrorCode);
        Assert.Empty(h.Gate!.Calls);
    }

    [Fact]
    public async Task The_secrets_survive_a_save_and_are_part_of_the_command_document_as_names_only()
    {
        using var fixture = new Fixture();
        await fixture.Named.SetAsync("api_token", Secret, CancellationToken.None);
        await using CommandHarness h = await CommandHarness.CreateAsync(secrets: fixture.Named);
        await h.SaveAsync(WithSecrets(Cmd.Gated("g"), "api_token"));

        CommandDocument stored = h.Registry.TryGet("g")!;
        string document = await File.ReadAllTextAsync(h.Temp.Combine("config", "commands", "g.json"));

        string[] names = ["api_token"];
        Assert.Equal(names, stored.Secrets);
        Assert.Contains("api_token", document, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, document, StringComparison.Ordinal);
    }
}
