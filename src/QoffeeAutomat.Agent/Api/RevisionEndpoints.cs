using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The revision endpoints of the management API (PLAN.md S08): the kept revisions of a command, a sensor or the settings, and
/// the restore of one of them as a new revision. The store keeps the history; a restore is an ordinary save, so the engines
/// follow it like any other change.
/// </summary>
public static class RevisionEndpoints
{
    private const string Tag = "Configuration";
    private const string CommandKind = "command";
    private const string SensorKind = "sensor";
    private const string SettingsKind = "settings";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/config/revisions/{kind}/{id}", ListAsync)
            .Describe(Tag, "listRevisions", "Lists the kept revisions of a command, a sensor (kind command or sensor) or the settings (kind and id settings), newest first.")
            .Returns<RevisionList>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPost("/config/revisions/{kind}/{id}/{revision}/restore", RestoreAsync)
            .Describe(Tag, "restoreRevision", "Stores a kept revision as a new revision of the document.")
            .ReturnsNothing()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, ApiResults.UnprocessableContent);
    }

    private static Task<IResult> ListAsync(string kind, string id, IConfigStore config, CancellationToken cancellationToken)
    {
        return kind switch
        {
            CommandKind => ListTypedAsync<CommandDocument>(kind, id, config, cancellationToken),
            SensorKind => ListTypedAsync<SensorDocument>(kind, id, config, cancellationToken),
            SettingsKind => ListTypedAsync<AgentSettingsDocument>(kind, id, config, cancellationToken),
            _ => Task.FromResult(UnknownKind(kind)),
        };
    }

    private static async Task<IResult> ListTypedAsync<T>(string kind, string id, IConfigStore config, CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        string documentId = IsSettings(kind) ? ConfigIds.Settings : id;
        if (IsSettings(kind) && !string.Equals(id, ConfigIds.Settings, StringComparison.Ordinal))
        {
            return UnknownDocument(kind, id);
        }

        IReadOnlyList<RevisionInfo> history = await config.Local.GetHistoryAsync<T>(documentId, cancellationToken).ConfigureAwait(false);
        int? current = config.Local.GetDocument<T>(documentId)?.Revision;
        if (history.Count == 0 && current is null)
        {
            return UnknownDocument(kind, id);
        }

        var list = new RevisionList(
            kind,
            id,
            current,
            [.. history.OrderByDescending(entry => entry.Revision).Select(entry => new RevisionEntry(entry.Revision, entry.UpdatedAt))]);
        return ApiResults.Json(list, ApiJsonContext.Default.RevisionList);
    }

    private static Task<IResult> RestoreAsync(string kind, string id, string revision, HttpContext context, IConfigStore config, CancellationToken cancellationToken)
    {
        if (!int.TryParse(revision, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsedRevision))
        {
            return Task.FromResult(ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_revision", "Invalid revision.", "The revision must be a non-negative integer."));
        }

        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return Task.FromResult(header);
        }

        return kind switch
        {
            CommandKind => RestoreTypedAsync<CommandDocument>(kind, id, parsedRevision, expected, context, config, cancellationToken),
            SensorKind => RestoreTypedAsync<SensorDocument>(kind, id, parsedRevision, expected, context, config, cancellationToken),
            SettingsKind => RestoreTypedAsync<AgentSettingsDocument>(kind, id, parsedRevision, expected, context, config, cancellationToken),
            _ => Task.FromResult(UnknownKind(kind)),
        };
    }

    private static async Task<IResult> RestoreTypedAsync<T>(
        string kind,
        string id,
        int revision,
        int? expected,
        HttpContext context,
        IConfigStore config,
        CancellationToken cancellationToken)
        where T : class, IConfigDocument
    {
        if (IsSettings(kind) && !string.Equals(id, ConfigIds.Settings, StringComparison.Ordinal))
        {
            return UnknownDocument(kind, id);
        }

        string documentId = IsSettings(kind) ? ConfigIds.Settings : id;
        SaveResult<T> restored = await config.Local.RestoreAsync<T>(documentId, revision, expected, cancellationToken).ConfigureAwait(false);
        switch (restored.Status)
        {
            case ChangeStatus.Applied:
                ApiETag.Set(context.Response, ApiETag.ForRevision(restored.Document!.Revision));
                return Results.NoContent();
            case ChangeStatus.Conflict:
                return ApiETag.Stale(restored.CurrentRevision);
            case ChangeStatus.NotFound:
                return ApiResults.NotFound("unknown_revision", "The revision " + revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of " + kind + " '" + id + "' is not kept.");
            default:
                return ApiResults.ValidationFailed(restored.Errors);
        }
    }

    private static bool IsSettings(string kind) => string.Equals(kind, SettingsKind, StringComparison.Ordinal);

    private static IResult UnknownKind(string kind)
    {
        return ApiResults.NotFound("unknown_kind", "The kind '" + kind + "' has no revisions. Use command, sensor or settings.");
    }

    private static IResult UnknownDocument(string kind, string id)
    {
        return ApiResults.NotFound("unknown_document", "There is no " + kind + " '" + id + "' and no kept revision of one.");
    }
}
