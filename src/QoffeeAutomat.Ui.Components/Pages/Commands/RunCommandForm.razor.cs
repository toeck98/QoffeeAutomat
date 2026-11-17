using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.Commands;

/// <summary>
/// The run form (PLAN.md T403): one input per parameter, the rules of the parameters checked before a run is sent, the
/// answer of the agent to the variables shown next to the fields, and the output of the run while it happens. Closing the
/// form (leaving the page) closes the output stream; the run itself goes on at the agent.
/// </summary>
public partial class RunCommandForm
{
    /// <summary>The most characters of output that the form keeps; the agent keeps at most 64 KiB per stream anyway.</summary>
    private const int MaxOutputChars = 512 * 1024;

    private readonly List<RunField> _fields = [];
    private readonly List<OutputPiece> _output = [];
    private CommandDocument? _builtFor;
    private RunPhase _phase;
    private ApiException? _error;
    private ApiException? _streamError;
    private bool _rejected;
    private bool _connectionLost;
    private bool _outputClipped;
    private int _outputChars;
    private string? _runId;
    private RunEndEvent? _end;
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    private enum RunPhase
    {
        Editing = 0,
        Starting,
        Running,
        Finished,
    }

    /// <summary>The command to run.</summary>
    [Parameter]
    [EditorRequired]
    public CommandDocument Command { get; set; } = null!;

    /// <summary>Raised when the user asks to close the form.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    private bool Locked => _phase is RunPhase.Starting or RunPhase.Running;

    private string PhaseName => _phase.ToString().ToLowerInvariant();

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (ReferenceEquals(_builtFor, Command))
        {
            return;
        }

        _builtFor = Command;
        _fields.Clear();
        foreach (ParameterDefinition definition in Command.Parameters ?? [])
        {
            _fields.Add(new RunField(definition));
        }
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        // Leaving the page closes the output stream; the run is not affected.
        CancellationTokenSource? source = _streamCts;
        Task? streaming = _streamTask;
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The stream ended in the meantime.
        }

        if (streaming is not null)
        {
            await streaming;
        }
    }

    private static string LabelOf(RunField field)
    {
        return field.Definition.Required ? field.Name + " *" : field.Name;
    }

    private static Dictionary<string, object> NumberAttributes(RunField field)
    {
        var attributes = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["step"] = field.Definition.Step is { } step and > 0
                ? step.ToString("R", CultureInfo.InvariantCulture)
                : field.Definition.Type == ParameterTypes.WholeNumber ? "1" : "any",
        };
        if (field.Definition.Min is { } min)
        {
            attributes["min"] = min.ToString("R", CultureInfo.InvariantCulture);
        }

        if (field.Definition.Max is { } max)
        {
            attributes["max"] = max.ToString("R", CultureInfo.InvariantCulture);
        }

        return attributes;
    }

    private static RunField? FindField(IEnumerable<RunField> fields, string path)
    {
        const string prefix = "/vars/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        // JSON pointer escapes (RFC 6901): ~1 is a slash and ~0 a tilde.
        string name = path[prefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));
    }

    private static void SetText(RunField field, string? value)
    {
        field.Text = value ?? string.Empty;
        field.Message = null;
    }

    private static void SetFlag(RunField field, bool value)
    {
        field.Flag = value;
        field.Message = null;
    }

    private string FormatNumber(double value) => value.ToString("G", L.Culture);

    private string HintOf(RunField field)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(field.Definition.Description))
        {
            parts.Add(field.Definition.Description);
        }

        ParameterDefinition definition = field.Definition;
        if (field.IsNumber)
        {
            if (definition.Min is { } min && definition.Max is { } max)
            {
                parts.Add(L.Format("Commands.Run.Hint.Range", FormatNumber(min), FormatNumber(max)));
            }
            else if (definition.Min is { } onlyMin)
            {
                parts.Add(L.Format("Commands.Run.Hint.AtLeast", FormatNumber(onlyMin)));
            }
            else if (definition.Max is { } onlyMax)
            {
                parts.Add(L.Format("Commands.Run.Hint.AtMost", FormatNumber(onlyMax)));
            }

            if (definition.Step is { } step and > 0)
            {
                parts.Add(L.Format("Commands.Run.Hint.Step", FormatNumber(step)));
            }
        }
        else if (definition.Type == ParameterTypes.Text && definition.MaxLength is { } maxLength)
        {
            parts.Add(L.Format("Commands.Run.Hint.MaxLength", maxLength));
        }

        return string.Join(" · ", parts);
    }

    private string Describe(FieldProblem problem)
    {
        return problem.Kind switch
        {
            FieldProblemKind.Required => L["Commands.Run.Error.Required"],
            FieldProblemKind.NotANumber => L["Commands.Run.Error.NotANumber"],
            FieldProblemKind.NotAWholeNumber => L["Commands.Run.Error.NotAWholeNumber"],
            FieldProblemKind.BelowMinimum => L.Format("Commands.Run.Error.BelowMinimum", FormatNumber(problem.Bound)),
            FieldProblemKind.AboveMaximum => L.Format("Commands.Run.Error.AboveMaximum", FormatNumber(problem.Bound)),
            FieldProblemKind.NotOnStep => L.Format("Commands.Run.Error.NotOnStep", FormatNumber(problem.Bound)),
            FieldProblemKind.TooLong => L.Format("Commands.Run.Error.TooLong", FormatNumber(problem.Bound)),
            FieldProblemKind.PatternMismatch => L["Commands.Run.Error.PatternMismatch"],
            _ => L["Commands.Run.Error.NotAnOption"],
        };
    }

    /// <summary>The text for a validation error of the agent: a translation of its code, else its English message.</summary>
    private string DescribeServerError(ApiValidationError error)
    {
        return error.Code switch
        {
            "required" => L["Commands.Run.Error.Required"],
            "type_mismatch" => L["Commands.Run.Error.TypeMismatch"],
            "range" => L["Commands.Run.Error.Range"],
            "step" => L["Commands.Run.Error.Step"],
            "options" => L["Commands.Run.Error.NotAnOption"],
            "max_length" => L["Commands.Run.Error.TooLongHere"],
            "pattern" => L["Commands.Run.Error.PatternMismatch"],
            _ => error.Message,
        };
    }

    private string ResultText(RunEndEvent end)
    {
        string duration = Pages.Dashboard.DashboardFormat.Duration(end.DurationMs, L.Culture);
        return end.Succeeded
            ? L.Format("Commands.Run.Succeeded", duration)
            : L.Format("Commands.Run.Failed", duration);
    }

    private string ResultDetails(RunEndEvent end)
    {
        string exit = end.ExitCode?.ToString(L.Culture) ?? L["Common.Unavailable"];
        string code = end.ErrorCode ?? L["Common.Unavailable"];
        string text = L.Format("Commands.Run.Details", exit, code);
        return string.IsNullOrWhiteSpace(end.ErrorMessage) ? text : text + " " + end.ErrorMessage;
    }

    private async Task SubmitAsync()
    {
        if (Locked)
        {
            return;
        }

        _error = null;
        _rejected = false;
        foreach (RunField field in _fields)
        {
            field.Message = null;
        }

        // The rules of the parameters are checked here first, so that a value that cannot be right is not even sent.
        bool valid = true;
        foreach (RunField field in _fields)
        {
            if (field.Validate() is { } problem)
            {
                field.Message = Describe(problem);
                valid = false;
            }
        }

        if (!valid)
        {
            return;
        }

        var variables = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (RunField field in _fields)
        {
            if (field.ToJson() is { } value)
            {
                variables[field.Name] = value;
            }
        }

        _phase = RunPhase.Starting;
        RunStarted started;
        try
        {
            started = await Api.StartRunAsync(Command.Id, variables);
        }
        catch (ApiException exception)
        {
            _phase = RunPhase.Editing;
            Reject(exception);
            return;
        }

        if (IsDisposed)
        {
            return;
        }

        ResetOutput();
        _runId = started.RunId;
        _phase = RunPhase.Running;
        _streamCts = new CancellationTokenSource();
        _streamTask = StreamAsync(started.RunId, _streamCts.Token);
    }

    /// <summary>Shows what the agent said about the variables next to the fields; anything else is shown as an error.</summary>
    private void Reject(ApiException exception)
    {
        if (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
            return;
        }

        int placed = 0;
        foreach (ApiValidationError item in exception.ValidationErrors)
        {
            RunField? field = FindField(_fields, item.Path);
            if (field is not null)
            {
                // The first message of a field is the most useful one.
                field.Message ??= DescribeServerError(item);
                placed++;
            }
        }

        if (placed > 0 && placed == exception.ValidationErrors.Count)
        {
            _rejected = true;
        }
        else
        {
            _error = exception;
        }
    }

    private async Task StreamAsync(string runId, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RunStreamItem item in Api.StreamRunAsync(runId, cancellationToken))
            {
                await InvokeAsync(() =>
                {
                    if (item.Output is { } output)
                    {
                        Append(output);
                    }
                    else if (item.End is { } end)
                    {
                        _end = end;
                    }

                    StateHasChanged();
                });
            }

            if (_end is null && !cancellationToken.IsCancellationRequested)
            {
                // The stream ended without telling how the run ended: the connection was lost, not the run.
                _connectionLost = true;
            }
        }
        catch (OperationCanceledException)
        {
            // The form was left.
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _streamError = exception;
            _connectionLost = _end is null;
        }
        finally
        {
            _phase = RunPhase.Finished;
            _streamCts?.Dispose();
            _streamCts = null;
            if (!IsDisposed)
            {
                await InvokeAsync(StateHasChanged);
            }
        }
    }

    private void Append(RunOutputEvent output)
    {
        if (_outputChars >= MaxOutputChars)
        {
            _outputClipped = true;
            return;
        }

        _output.Add(new OutputPiece(string.Equals(output.Stream, "stderr", StringComparison.Ordinal), output.Text));
        _outputChars += output.Text.Length;
    }

    private void ResetOutput()
    {
        _output.Clear();
        _outputChars = 0;
        _outputClipped = false;
        _connectionLost = false;
        _streamError = null;
        _end = null;
        _runId = null;
    }

    private void RunAgain()
    {
        ResetOutput();
        _phase = RunPhase.Editing;
    }

    private Task CloseAsync() => OnClose.InvokeAsync();

    private readonly record struct OutputPiece(bool IsError, string Text);
}
