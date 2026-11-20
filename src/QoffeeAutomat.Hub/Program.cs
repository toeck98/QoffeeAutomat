using Microsoft.Data.Sqlite;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Hub.Hosting;

namespace QoffeeAutomat.Hub;

/// <summary>
/// Entry point of the hub: <c>qoffeeautomat-hub [options]</c> (PLAN.md S20, T902). The class is public and not static so that
/// <c>WebApplicationFactory&lt;Program&gt;</c> can start the host in tests.
/// </summary>
public sealed class Program
{
    private Program()
    {
    }

    /// <summary>Runs the command line.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The process exit code, see <see cref="HubExitCodes"/>.</returns>
    public static async Task<int> Main(string[] args)
    {
        HubOptions options = HubCommandLine.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine("Try --help.");
            return HubExitCodes.Usage;
        }

        if (options.ShowHelp)
        {
            Console.Out.WriteLine(HubCommandLine.Usage);
            return HubExitCodes.Success;
        }

        if (options.ShowVersion)
        {
            Console.Out.WriteLine("QoffeeAutomat hub " + HubVersion.Current);
            return HubExitCodes.Success;
        }

        try
        {
            WebApplication app = HubHost.Build(options);
            await app.RunAsync().ConfigureAwait(false);
            return HubExitCodes.Success;
        }
        catch (Exception exception) when (exception is HubStartupException or SecretStoreException or SqliteException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("The hub could not start: " + exception.Message);
            return HubExitCodes.Failure;
        }
    }
}
