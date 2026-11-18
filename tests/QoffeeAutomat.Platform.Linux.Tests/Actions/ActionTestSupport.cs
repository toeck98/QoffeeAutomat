using System.Text.Json;
using QoffeeAutomat.Core.Events;
using QoffeeAutomat.Core.Platform;
using QoffeeAutomat.Platform.Linux.Actions;
using QoffeeAutomat.Platform.Linux.Service;
using QoffeeAutomat.Platform.Linux.Sessions;

namespace QoffeeAutomat.Platform.Linux.Tests.Actions;

/// <summary>A <c>systemctl</c>, <c>loginctl</c>, <c>wpctl</c> and so on that records its calls and answers from a function; it never starts a program.</summary>
internal sealed class ScriptedRunner : ICommandRunner
{
    public List<(string FileName, string[] Arguments)> Calls { get; } = [];

    /// <summary>Answers a call; null means that the program is not installed.</summary>
    public Func<string, string[], CommandResult?> Answer { get; set; } = (_, _) => new CommandResult(0, string.Empty, string.Empty);

    public ValueTask<CommandResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string[] copy = [.. arguments];
        Calls.Add((fileName, copy));
        return ValueTask.FromResult(Answer(fileName, copy));
    }

    public IReadOnlyList<string> Programs => [.. Calls.Select(call => call.FileName)];
}

/// <summary>The user's session as the actions see it.</summary>
internal sealed class FakeUserSession : ILinuxUserSession
{
    public string? SessionId { get; set; } = "c2";

    public List<(string Name, IReadOnlyDictionary<string, object> Variables)> Builtins { get; } = [];

    public List<(string Title, string Message)> Notifications { get; } = [];

    public Func<string, ExecutionResult> Answer { get; set; } = _ => new ExecutionResult(0, "done in the session", string.Empty);

    public ValueTask<ExecutionResult> RunBuiltinAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Builtins.Add((name, variables));
        return ValueTask.FromResult(Answer(name));
    }

    public ValueTask NotifyAsync(string title, string message, CancellationToken cancellationToken)
    {
        Notifications.Add((title, message));
        return ValueTask.CompletedTask;
    }
}

/// <summary>Programs that were started on the desktop.</summary>
internal sealed class RecordingProgramLauncher : IProgramLauncher
{
    public List<(string FileName, string[] Arguments)> Started { get; } = [];

    public string? Error { get; set; }

    public bool TryStart(string fileName, IReadOnlyList<string> arguments, out string? failure)
    {
        failure = Error;
        if (Error is not null)
        {
            return false;
        }

        Started.Add((fileName, [.. arguments]));
        return true;
    }
}

/// <summary>Processes of the user.</summary>
internal sealed class FakeUserProcesses : IUserProcesses
{
    public List<string> Killed { get; } = [];

    public int Count { get; set; } = 1;

    public int Kill(string name)
    {
        Killed.Add(name);
        return Count;
    }
}

/// <summary>An event bus that records what was published.</summary>
internal sealed class RecordingEventBus : IEventBus
{
    public List<(string Type, JsonElement? Data)> Published { get; } = [];

    public Task<AgentEvent> PublishAsync(string type, JsonElement? data = null, CancellationToken cancellationToken = default)
    {
        Published.Add((type, data));
        return Task.FromResult(new AgentEvent(0, type, DateTimeOffset.UnixEpoch, data));
    }

    public EventSubscription Subscribe(IReadOnlyCollection<string>? types = null) => throw new NotSupportedException();

    public Task<IReadOnlyList<AgentEvent>> GetRecentAsync(int limit, string? type = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentEvent>>([]);
}

/// <summary>Helpers for variables.</summary>
internal static class Vars
{
    public static Dictionary<string, object> Of(params (string Name, object Value)[] values)
    {
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach ((string name, object value) in values)
        {
            result[name] = value;
        }

        return result;
    }

    public static CommandResult Ok(string output = "") => new(0, output, string.Empty);

    public static CommandResult Fails(string error, int code = 1) => new(code, string.Empty, error);
}
