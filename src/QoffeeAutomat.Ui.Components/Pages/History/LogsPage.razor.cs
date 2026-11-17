using System.Globalization;
using Microsoft.AspNetCore.Components;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Ui.Components.Api;
using QoffeeAutomat.Ui.Components.Pages.Dashboard;
using QoffeeAutomat.Ui.Components.Services;

namespace QoffeeAutomat.Ui.Components.Pages.History;

/// <summary>
/// The log viewer (PLAN.md T407). The agent has no log stream, so follow mode reads the newest entries again at an interval, and
/// only while the page is open and the switch is on. The entries are shown newest first, so that the latest line is always in sight.
/// </summary>
public partial class LogsPage
{
    private static readonly string[] Levels = ["trace", "debug", "information", "warning", "error", "critical"];
    private static readonly int[] Limits = [100, 250, 500, 1000];

    private readonly List<LogEntry> _entries = [];
    private LiveFeed? _feed;
    private string _level = "information";
    private int _limit = 250;
    private string? _search;
    private bool _follow;
    private bool _loading = true;
    private ApiException? _error;

    [Inject]
    private IAgentApi Api { get; set; } = null!;

    [Inject]
    private SessionState Session { get; set; } = null!;

    [Inject]
    private LogViewerOptions Options { get; set; } = null!;

    [Inject]
    private TimeProvider Time { get; set; } = null!;

    /// <summary>The entries that match the search text, newest first.</summary>
    private IReadOnlyList<LogEntry> VisibleEntries
    {
        get
        {
            IEnumerable<LogEntry> entries = _entries;
            if (!string.IsNullOrWhiteSpace(_search))
            {
                string text = _search.Trim();
                entries = entries.Where(entry =>
                    entry.Message.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || entry.Category.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (entry.Exception?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            return [.. entries.Reverse()];
        }
    }

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    /// <inheritdoc />
    protected override async ValueTask OnDisposeAsync()
    {
        await StopFollowingAsync();
    }

    private static string LineClass(LogEntry entry) => "qa-log-line qa-log-" + entry.Level.ToLowerInvariant();

    private async Task ReloadAsync()
    {
        _loading = true;
        await LoadAsync();
        _loading = false;
    }

    private async Task SetLevelAsync(string level)
    {
        _level = level;
        await ReloadAsync();
    }

    private async Task SetLimitAsync(string limit)
    {
        if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            _limit = parsed;
            await ReloadAsync();
        }
    }

    private async Task SetFollowAsync(bool follow)
    {
        _follow = follow;
        if (follow)
        {
            await StopFollowingAsync();
            _feed = new LiveFeed(RunFollowAsync);
        }
        else
        {
            await StopFollowingAsync();
        }
    }

    private async Task StopFollowingAsync()
    {
        LiveFeed? feed = _feed;
        _feed = null;
        if (feed is not null)
        {
            await feed.DisposeAsync();
        }
    }

    private async Task LoadAsync()
    {
        try
        {
            LogList list = await Api.GetLogsAsync(_level, _limit);
            _error = null;
            _entries.Clear();
            _entries.AddRange(list.Entries);
        }
        catch (ApiException exception) when (exception.IsUnauthorized)
        {
            Session.MarkSignedOut();
        }
        catch (ApiException exception)
        {
            _error = exception;
        }
    }

    private async Task RunFollowAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Options.FollowInterval, Time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await InvokeAsync(async () =>
                {
                    if (IsDisposed || !_follow)
                    {
                        return;
                    }

                    await LoadAsync();
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Follow mode was switched off or the page was left.
        }
    }
}
