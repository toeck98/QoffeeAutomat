using System.Text.Json;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Commands;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// The test panel of the command editor (PLAN.md T404): one input per parameter of the draft, the rules of the parameters
/// checked before anything is sent, the run of the unsaved draft through <c>POST /commands/test</c>, the messages of the agent
/// next to the fields (variables) or in the editor (draft), and the live output with the result. Leaving the page closes the
/// output stream; the run itself goes on at the agent.
/// </summary>
public partial class CommandTestPanel
{
    /// <summary>The most characters of output that the panel keeps; the agent keeps at most 64 KiB per stream anyway.</summary>
    private const int MaxOutputChars = 512 * 1024;

    private const string DraftPrefix = "/command";
    private const string VarsPrefix = "/vars/";

    private readonly List<RunField> _fields = [];
    private readonly List<OutputPiece> _output = [];
    private string _signature = string.Empty;
    private RunPhase _phase;
    private ApiException? _error;
    private ApiException? _streamError;
    private bool _rejected;
    private bool _draftRejected;
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

    /// <summary>The draft as it is on screen; this is what a test runs.</summary>
    [Parameter]
    [EditorRequired]
    public CommandDocument Document { get; set; } = null!;

    /// <summary>What the editor found wrong with the draft by itself; a draft with problems is not sent.</summary>
    [Parameter]
    public IReadOnlyList<ApiValidationError> Problems { get; set; } = [];

    /// <summary>Raised with the values that are entered, whenever one changes.</summary>
    [Parameter]
    public EventCallback<IReadOnlyDictionary<string, JsonElement>> OnVariablesChanged { get; set; }

    /// <summary>Raised with the errors that the agent found in the draft (paths relative to the draft), when it refused to run it.</summary>
    [Parameter]
    public EventCallback<IReadOnlyList<ApiValidationError>> OnDraftRejected { get; set; }

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    private bool Locked => _phase is RunPhase.Starting or RunPhase.Running;

    private string PhaseName => _phase.ToString().ToLowerInvariant();

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        IReadOnlyList<ParameterDefinition> parameters = Document.Parameters ?? [];
        string signature = Signature(parameters);
        if (string.Equals(signature, _signature, StringComparison.Ordinal))
        {
            return;
        }

        // The parameters of the draft changed: the inputs follow, and what was typed stays where the parameter stays.
        List<RunField> previous = [.. _fields];
        _signature = signature;
        _fields.Clear();
        foreach (ParameterDefinition definition in parameters)
        {
            if (string.IsNullOrWhiteSpace(definition.Name) || _fields.Exists(field => string.Equals(field.Name, definition.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            var field = new RunField(definition);
            RunField? old = previous.Find(candidate => string.Equals(candidate.Name, definition.Name, StringComparison.Ordinal) && candidate.Definition.Type == definition.Type);
            if (old is not null)
            {
                field.Text = old.Text;
                field.Flag = old.Flag;
            }

            _fields.Add(field);
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

    private static string Signature(IReadOnlyList<ParameterDefinition> parameters)
    {
        return string.Join(
            '\n',
            parameters.Select(parameter => FormattableString.Invariant(
                $"{parameter.Name}|{parameter.Type}|{parameter.Required}|{parameter.Default?.GetRawText()}|{parameter.Min}|{parameter.Max}|{parameter.Step}|{parameter.Pattern}|{parameter.MaxLength}|{string.Join(',', parameter.Options ?? [])}|{parameter.Secret}|{parameter.Unit}|{parameter.Description}")));
    }

    private static string LabelOf(RunField field) => field.Definition.Required ? field.Name + " *" : field.Name;

    private static RunField? FindField(IEnumerable<RunField> fields, string path)
    {
        if (!path.StartsWith(VarsPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // JSON pointer escapes (RFC 6901): ~1 is a slash and ~0 a tilde.
        string name = path[VarsPrefix.Length..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        return fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));
    }

    private string FormatNumber(double value) => value.ToString("G", L.Culture);

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

    private Task SetTextAsync(RunField field, string? value)
    {
        field.Text = value ?? string.Empty;
        field.Message = null;
        return NotifyVariablesAsync();
    }

    private Task SetFlagAsync(RunField field, bool value)
    {
        field.Flag = value;
        field.Message = null;
        return NotifyVariablesAsync();
    }

    /// <summary>Tells the editor which values are entered, for the YAML of the preview; a value that breaks the rules is left out.</summary>
    private Task NotifyVariablesAsync()
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (RunField field in _fields)
        {
            if (field.Validate() is null && field.ToJson() is { } value)
            {
                values[field.Name] = value;
            }
        }

        return OnVariablesChanged.InvokeAsync(values);
    }

    private async Task TestAsync()
    {
        if (Locked)
        {
            return;
        }

        _error = null;
        _rejected = false;
        _draftRejected = false;
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

        if (Problems.Count > 0)
        {
            // The draft has a value that is no value (a number field with text); the editor shows where.
            _draftRejected = true;
            valid = false;
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
            started = await Api.TestCommandAsync(Document, variables);
        }
        catch (ApiException exception)
        {
            _phase = RunPhase.Editing;
            await RejectAsync(exception);
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

    /// <summary>Shows what the agent said about the variables next to the fields and what it said about the draft in the editor.</summary>
    private async Task RejectAsync(ApiException exception)
    {
        if (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
            return;
        }

        int placed = 0;
        var draftErrors = new List<ApiValidationError>();
        foreach (ApiValidationError item in exception.ValidationErrors)
        {
            RunField? field = FindField(_fields, item.Path);
            if (field is not null)
            {
                // The first message of a field is the most useful one.
                field.Message ??= DescribeServerError(item);
                placed++;
            }
            else if (item.Path == DraftPrefix || item.Path.StartsWith(DraftPrefix + "/", StringComparison.Ordinal))
            {
                string relative = item.Path[DraftPrefix.Length..];
                draftErrors.Add(item with { Path = relative.Length == 0 ? "/" : relative });
                placed++;
            }
        }

        if (placed > 0 && placed == exception.ValidationErrors.Count)
        {
            _rejected = _fields.Exists(field => field.Message is not null);
            _draftRejected = draftErrors.Count > 0;
            if (_draftRejected)
            {
                await OnDraftRejected.InvokeAsync(draftErrors);
            }
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
            // The page was left.
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

    private readonly record struct OutputPiece(bool IsError, string Text);
}
