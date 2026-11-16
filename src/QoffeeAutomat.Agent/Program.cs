using Microsoft.AspNetCore.Builder;
using QoffeeAutomat.Agent.Discovery;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Security.Auth;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent;

/// <summary>
/// Entry point of the agent: <c>qoffeeautomat [run|version|service|emit|reset-password] [options]</c> (PLAN.md T201).
/// The class is public and not static so that <c>WebApplicationFactory&lt;Program&gt;</c> can start the
/// host in tests.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    /// <summary>Runs the command line.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The process exit code, see <see cref="ExitCodes"/>.</returns>
    public static async Task<int> Main(string[] args)
    {
        AgentOptions options = CommandLine.Parse(args);
        if (options.Error is not null)
        {
            WriteError(options.Error);
            return ExitCodes.Usage;
        }

        if (options.ShowHelp)
        {
            WriteLine(CommandLine.Usage);
            return ExitCodes.Success;
        }

        switch (options.Verb)
        {
            case AgentVerb.Version:
                WriteLine("QoffeeAutomat " + AgentVersion.Current);
                return ExitCodes.Success;
            case AgentVerb.Service:
                // The service verbs (install, remove, start, stop) arrive with T301.
                WriteError("The 'service' verb is not available yet.");
                return ExitCodes.Unavailable;
            case AgentVerb.Emit:
                return await EmitCommand.RunAsync(options, Console.Out, Console.Error, Environment.GetEnvironmentVariable).ConfigureAwait(false);
            case AgentVerb.ResetPassword:
                return await ResetPasswordCommand.RunAsync(options, Console.Out, Console.Error, ResetPasswordCommand.IsPrivileged).ConfigureAwait(false);
            default:
                return await RunAsync(options).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAsync(AgentOptions options)
    {
        try
        {
            // The executable announces itself with the multicast DNS responder of the agent (T216); the simulated
            // platform's recording advertiser stays the default so that tests never open a multicast socket.
            WebApplication app = AgentHost.Build(options, services => services.UseMdnsAdvertiser());
            await app.RunAsync().ConfigureAwait(false);
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is AgentStartupException or SecretStoreException or IOException or UnauthorizedAccessException)
        {
            WriteError("The agent could not start: " + exception.Message);
            return ExitCodes.Failure;
        }
    }

    private static void WriteLine(string text) => Console.Out.WriteLine(text);

    private static void WriteError(string text) => Console.Error.WriteLine(text);
}
