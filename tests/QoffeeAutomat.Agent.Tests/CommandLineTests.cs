using QoffeeAutomat.Agent.Hosting;

namespace QoffeeAutomat.Agent.Tests;

/// <summary>Command-line tests of T201: verbs, options, the version output and the service stub.</summary>
[Trait("Category", "Hosting")]
[Collection("Console")]
public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_means_run()
    {
        AgentOptions options = CommandLine.Parse([]);

        Assert.Equal(AgentVerb.Run, options.Verb);
        Assert.Null(options.Error);
    }

    [Fact]
    public void Options_are_parsed()
    {
        AgentOptions options = CommandLine.Parse(["run", "--data-dir", "/tmp/x", "--platform=Simulated", "--ui-port", "0", "--link-port", "9000"]);

        Assert.Null(options.Error);
        Assert.Equal("/tmp/x", options.DataDir);
        Assert.Equal("simulated", options.Platform);
        Assert.Equal(0, options.UiPort);
        Assert.Equal(9000, options.LinkPort);
    }

    [Theory]
    [InlineData("--platform", "beos")]
    [InlineData("--ui-port", "70000")]
    [InlineData("--link-port", "abc")]
    public void Invalid_values_are_reported(string option, string value)
    {
        Assert.NotNull(CommandLine.Parse([option, value]).Error);
    }

    [Fact]
    public void Unknown_options_are_passed_to_aspnet_core()
    {
        AgentOptions options = CommandLine.Parse(["--environment", "Testing"]);

        Assert.Null(options.Error);
        Assert.Equal(["--environment", "Testing"], options.PassThrough);
    }

    [Fact]
    public async Task Version_prints_the_version_and_succeeds()
    {
        (int code, string output, _) = await RunMainAsync("version");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("QoffeeAutomat " + AgentVersion.Current, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_verb_is_a_stub_until_T301()
    {
        (int code, _, string error) = await RunMainAsync("service", "install");

        Assert.Equal(ExitCodes.Unavailable, code);
        Assert.Contains("service", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_bad_command_line_exits_with_the_usage_code()
    {
        (int code, _, string error) = await RunMainAsync("--platform", "beos");

        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEmpty(error);
    }

    private static async Task<(int Code, string Output, string Error)> RunMainAsync(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            int code = await Program.Main(args);
            return (code, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleTestDefinition
{
}
