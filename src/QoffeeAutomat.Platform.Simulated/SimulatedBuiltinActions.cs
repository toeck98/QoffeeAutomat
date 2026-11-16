using System.Globalization;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Platform;

namespace QoffeeAutomat.Platform.Simulated;

/// <summary>
/// Two built-in actions for tests: <c>sim.echo</c> returns its variables as a JSON object on standard
/// output, <c>sim.fail</c> exits with a non-zero code. Neither touches the machine.
/// </summary>
public sealed class SimulatedBuiltinActions : IBuiltinActionProvider
{
    /// <summary>The name of the echo action.</summary>
    public const string EchoName = "sim.echo";

    /// <summary>The name of the failing action.</summary>
    public const string FailName = "sim.fail";

    /// <summary>The default exit code of <see cref="FailName"/>.</summary>
    public const int DefaultFailExitCode = 1;

    private static readonly BuiltinActionDescriptor[] Descriptors =
    [
        new(
            EchoName,
            "Echo variables",
            RunAsValues.System,
            [
                new ParameterDefinition("Message", ParameterTypes.Text),
                new ParameterDefinition("Count", ParameterTypes.WholeNumber),
                new ParameterDefinition("Ratio", ParameterTypes.Real),
                new ParameterDefinition("Enabled", ParameterTypes.Flag),
            ],
            "Returns the variables it was called with as a JSON object."),
        new(
            FailName,
            "Fail",
            RunAsValues.System,
            [
                new ParameterDefinition("ExitCode", ParameterTypes.WholeNumber, Min: 1, Max: 255),
            ],
            "Exits with a non-zero code (1 unless ExitCode is given)."),
    ];

    /// <inheritdoc />
    public IReadOnlyList<BuiltinActionDescriptor> Actions => Descriptors;

    /// <inheritdoc />
    public ValueTask<ExecutionResult> ExecuteAsync(
        string name,
        IReadOnlyDictionary<string, object> variables,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variables);
        cancellationToken.ThrowIfCancellationRequested();

        ExecutionResult result = name switch
        {
            EchoName => new ExecutionResult(0, Echo(variables), string.Empty),
            FailName => Fail(variables),
            _ => new ExecutionResult(127, string.Empty, $"Unknown built-in action '{name}'.", ErrorCodes.BadRequest),
        };

        return ValueTask.FromResult(result);
    }

    private static ExecutionResult Fail(IReadOnlyDictionary<string, object> variables)
    {
        int exitCode = variables.TryGetValue("ExitCode", out object? value) && value is long requested
            ? (int)Math.Clamp(requested, 1, 255)
            : DefaultFailExitCode;
        return new ExecutionResult(exitCode, string.Empty, "sim.fail failed on purpose.");
    }

    private static string Echo(IReadOnlyDictionary<string, object> variables)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (KeyValuePair<string, object> pair in variables.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                switch (pair.Value)
                {
                    case string text:
                        writer.WriteString(pair.Key, text);
                        break;
                    case long whole:
                        writer.WriteNumber(pair.Key, whole);
                        break;
                    case int smallWhole:
                        writer.WriteNumber(pair.Key, smallWhole);
                        break;
                    case double real:
                        writer.WriteNumber(pair.Key, real);
                        break;
                    case bool flag:
                        writer.WriteBoolean(pair.Key, flag);
                        break;
                    default:
                        writer.WriteString(pair.Key, Convert.ToString(pair.Value, CultureInfo.InvariantCulture));
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
