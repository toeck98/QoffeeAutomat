using System.Text.Json;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.History;

/// <summary>
/// The history page (PLAN.md T407): the runs with filters and paging, the detail of one run (variables, output, exit code, source)
/// and the recent events. The filters are applied with a button, so that typing does not ask the agent on every key.
/// </summary>
public partial class HistoryPage
{
    private const int PageSize = 25;
    private const int EventLimit = 100;

    private static readonly string[] Sources = ["ha", "ui", "hub", "cli", "sequence", "test"];

    private readonly List<RunSummary> _runs = [];
    private readonly List<EventInfo> _events = [];
    private readonly List<(string Name, string Value)> _variables = [];

    private string? _command;
    private string _source = string.Empty;
    private bool _failedOnly;
    private string? _eventType;
    private int _offset;
    private bool _hasMore;
    private bool _loadingRuns = true;
    private bool _loadingEvents = true;
    private ApiException? _runsError;
    private ApiException? _eventsError;
    private ApiException? _detailError;
    private string? _selectedId;
    private RunDetail? _detail;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(LoadRunsAsync(), LoadEventsAsync());
    }

    private string SourceText(string source) => L.Find("History.Source." + source) ?? source;

    private string StateText(string state) => L.Find("History.State." + state) ?? state;

    private static string ResultKey(RunSummary run) => run.State switch
    {
        "waiting" => "waiting",
        "running" => "running",
        _ => run.Succeeded ? "succeeded" : "failed",
    };

    private string ResultText(RunSummary run)
    {
        return ResultKey(run) switch
        {
            "waiting" => L["Dashboard.Runs.Waiting"],
            "running" => L["Dashboard.Runs.Running"],
            "succeeded" => L["Dashboard.Runs.Succeeded"],
            _ => string.IsNullOrEmpty(run.ErrorCode) ? L["Dashboard.Runs.Failed"] : $"{L["Dashboard.Runs.Failed"]} ({run.ErrorCode})",
        };
    }

    private async Task RefreshAllAsync()
    {
        await Task.WhenAll(LoadRunsAsync(), LoadEventsAsync());
    }

    private async Task ApplyFiltersAsync()
    {
        _offset = 0;
        await LoadRunsAsync();
    }

    private async Task PreviousPageAsync()
    {
        _offset = Math.Max(0, _offset - PageSize);
        await LoadRunsAsync();
    }

    private async Task NextPageAsync()
    {
        _offset += PageSize;
        await LoadRunsAsync();
    }

    private async Task LoadRunsAsync()
    {
        _loadingRuns = true;
        try
        {
            var query = new RunQuery(
                PageSize,
                _offset,
                string.IsNullOrWhiteSpace(_command) ? null : _command.Trim(),
                _source.Length == 0 ? null : _source,
                _failedOnly);
            RunList list = await Api.QueryRunsAsync(query);
            _runsError = null;
            _runs.Clear();
            _runs.AddRange(list.Runs);
            _hasMore = list.HasMore;
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _runsError = exception;
        }
        finally
        {
            _loadingRuns = false;
        }
    }

    private async Task LoadEventsAsync()
    {
        _loadingEvents = true;
        try
        {
            EventList list = await Api.QueryEventsAsync(EventLimit, string.IsNullOrWhiteSpace(_eventType) ? null : _eventType.Trim());
            _eventsError = null;
            _events.Clear();
            _events.AddRange(list.Events);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _eventsError = exception;
        }
        finally
        {
            _loadingEvents = false;
        }
    }

    private async Task OpenAsync(string id)
    {
        _detailError = null;
        try
        {
            RunDetail detail = await Api.GetRunAsync(id);
            _detail = detail;
            _selectedId = id;
            ReadVariables(detail);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            // The run may have been pruned meanwhile; the error says so and the list stays as it is.
            _detail = null;
            _selectedId = null;
            _detailError = exception;
        }
    }

    private void CloseDetail()
    {
        _detail = null;
        _selectedId = null;
        _variables.Clear();
    }

    /// <summary>Turns the variables object of a run into name and text pairs; the agent has redacted secret values already.</summary>
    private void ReadVariables(RunDetail detail)
    {
        _variables.Clear();
        if (detail.Variables is not { ValueKind: JsonValueKind.Object } variables)
        {
            return;
        }

        foreach (JsonProperty property in variables.EnumerateObject())
        {
            string text = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            _variables.Add((property.Name, text));
        }
    }
}
