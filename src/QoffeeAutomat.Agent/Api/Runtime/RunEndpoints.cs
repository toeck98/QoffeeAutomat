using System.Text.Json;
using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Agent.Hub.Tunnel;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution;
using QoffeeAutomat.Core.History;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api.Runtime;

/// <summary>
/// The run endpoints of the management API (PLAN.md S08): start a run, test a draft, the history, one run and its
/// live output. Every start goes through <see cref="CommandEngine"/>, so the rules are those of QoffeeLink:
/// disabled commands, the pause, the cooldown, the concurrency mode and the validation of the variables.
/// </summary>
public static class RunEndpoints
{
    /// <summary>The largest request body that is read (variables, or a draft document with variables).</summary>
    public const int MaxBodyBytes = 256 * 1024;

    /// <summary>The page size of <c>GET /runs</c> when the request names none.</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The largest page size of <c>GET /runs</c>.</summary>
    public const int MaxPageSize = 200;

    private const string Tag = "Runs";
    private const string UiClientId = "ui";

    /// <summary>Maps the endpoints.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/commands/{id}/run", RunCommandAsync)
            .Describe(Tag, "runCommand", "Starts a run of a command with variables; answers as soon as the run is accepted.")
            .Takes<RunStartRequest>(optional: true)
            .Returns<RunStarted>(StatusCodes.Status202Accepted)
            .Fails(
                StatusCodes.Status400BadRequest,
                StatusCodes.Status404NotFound,
                StatusCodes.Status409Conflict,
                ApiResults.UnprocessableContent,
                StatusCodes.Status429TooManyRequests);
        api.MapPost("/commands/test", TestCommandAsync)
            .Describe(Tag, "testCommand", "Validates and runs a draft command without saving it; the run is recorded in the history as a test run.")
            .Takes<CommandTestRequest>()
            .Returns<RunStarted>(StatusCodes.Status202Accepted)
            .Fails(
                StatusCodes.Status400BadRequest,
                StatusCodes.Status409Conflict,
                ApiResults.UnprocessableContent,
                StatusCodes.Status429TooManyRequests);
        api.MapGet("/runs", ListRunsAsync)
            .Describe(Tag, "listRuns", "Lists runs from the history, newest first.")
            .Query("command", "string", "Only runs of this command.")
            .Query("source", "string", "Only runs from this source: ha, ui, hub, cli, sequence or test.")
            .Query("failed", "boolean", "True lists only runs that did not succeed.")
            .Query("limit", "integer", "The page size, 1 to 200; the default is 50.")
            .Query("offset", "integer", "How many of the newest matching runs to skip.")
            .Returns<RunList>()
            .Fails(StatusCodes.Status400BadRequest);
        api.MapGet("/runs/{id}", GetRunAsync)
            .Describe(Tag, "getRun", "Reads one run with its variables and output.")
            .Returns<RunDetail>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapGet("/runs/{id}/stream", StreamRunAsync)
            .Describe(Tag, "streamRun", "Streams the output of a run as Server-Sent Events: output events, then one end event. A running run is replayed from its start.")
            .ReturnsFile(ServerSentEvents.ContentType)
            .Fails(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> RunCommandAsync(
        string id,
        HttpContext context,
        CommandEngine engine,
        CancellationToken cancellationToken)
    {
        (RunStartRequest? body, IResult? failure) = await ApiResults.ReadOptionalBodyAsync(
            context.Request, ApiJsonContext.Default.RunStartRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        // A run that the hub started through the management tunnel (T912) is a run of the hub, for the history and the audit: the hub link is the client, the hub user the user.
        TunnelIdentity? hub = TunnelIdentity.Of(context);
        string source = hub is null ? RunSources.Ui : RunSources.Hub;
        RunHandle handle = await engine.StartAsync(
            new RunRequest(id, body?.Vars, source, hub is null ? UiClientId : HubLinkService.PeerId, hub?.User),
            cancellationToken).ConfigureAwait(false);
        return await OutcomeAsync(handle, id, source, engine.Registry.TryGet(id), body?.Vars, context).ConfigureAwait(false);
    }

    private static async Task<IResult> TestCommandAsync(
        HttpContext context,
        CommandEngine engine,
        IConfigStore config,
        CancellationToken cancellationToken)
    {
        (CommandTestRequest? body, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.CommandTestRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body is null)
        {
            return failure!;
        }

        if (body.Command is null)
        {
            return ApiResults.ValidationFailed([new ValidationError("/command", ValidationCodes.Required, "A draft command is required.")]);
        }

        CommandDocument draft = ApiDocuments.Normalize(body.Command);
        ValidationResult validation = DocumentValidator.ValidateCommand(draft, ApiDocuments.CommandLookup(config));
        if (!validation.IsValid)
        {
            // The draft sits at /command in the request body, so the pointers of the validator are moved there.
            return ApiResults.ValidationFailed(validation.Errors, "/command");
        }

        RunHandle handle = await engine.StartDraftAsync(draft, body.Vars, UiClientId, cancellationToken).ConfigureAwait(false);
        return await OutcomeAsync(handle, draft.Id, RunSources.Test, draft, body.Vars, context).ConfigureAwait(false);
    }

    /// <summary>Turns the handle of a started run into 202 with the run id, or the problem that rejected it.</summary>
    private static async Task<IResult> OutcomeAsync(
        RunHandle handle,
        string commandId,
        string source,
        CommandDocument? command,
        IReadOnlyDictionary<string, JsonElement>? vars,
        HttpContext context)
    {
        if (handle.IsAccepted)
        {
            context.Response.Headers.Location = "/api/v1/runs/" + handle.RunId;
            return ApiResults.Json(new RunStarted(handle.RunId, commandId, source), ApiJsonContext.Default.RunStarted, StatusCodes.Status202Accepted);
        }

        // A rejected request is complete at once; the engine has recorded it in the history.
        RunResult rejected = await handle.Completion.ConfigureAwait(false);
        string message = rejected.ErrorMessage ?? "The run was rejected.";
        switch (rejected.ErrorCode)
        {
            case ErrorCodes.UnknownCommand:
                return ApiResults.Problem(StatusCodes.Status404NotFound, ErrorCodes.UnknownCommand, message);
            case ErrorCodes.InvalidVars:
                BindResult bound = VariableBinder.Bind(command?.Parameters, vars);
                return ApiResults.Problem(
                    ApiResults.UnprocessableContent,
                    ErrorCodes.InvalidVars,
                    "The variables are not valid for this command.",
                    [.. bound.Errors.Select(error => new ApiValidationError(error.Path, error.Code, error.Message))],
                    runId: rejected.RunId);
            case ErrorCodes.Cooldown:
                return ApiResults.Problem(StatusCodes.Status429TooManyRequests, ErrorCodes.Cooldown, message, runId: rejected.RunId);
            default:
                // command_disabled, paused, busy and the internal rejections of sequences: the request is fine, the state is not.
                return ApiResults.Problem(StatusCodes.Status409Conflict, rejected.ErrorCode ?? ErrorCodes.Internal, message, runId: rejected.RunId);
        }
    }

    private static async Task<IResult> ListRunsAsync(
        string? command,
        string? source,
        bool? failed,
        int? limit,
        int? offset,
        IRunHistory history,
        CancellationToken cancellationToken)
    {
        int size = limit ?? DefaultPageSize;
        int skip = offset ?? 0;
        if (size is < 1 or > MaxPageSize)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "limit must be between 1 and 200.");
        }

        if (skip < 0)
        {
            return ApiResults.Problem(StatusCodes.Status400BadRequest, "invalid_query", "offset must not be negative.");
        }

        // One more than the page is read, which tells whether another page follows.
        IReadOnlyList<RunRecord> records = await history
            .QueryAsync(new RunQuery(Blank(command), Blank(source), failed ?? false, size + 1, skip), cancellationToken)
            .ConfigureAwait(false);
        RunSummary[] page = [.. records.Take(size).Select(Summarize)];
        return ApiResults.Json(new RunList(page, size, skip, records.Count > size), ApiJsonContext.Default.RunList);
    }

    private static async Task<IResult> GetRunAsync(
        string id,
        IRunHistory history,
        CommandEngine engine,
        CancellationToken cancellationToken)
    {
        RunRecord? record = await history.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            return ApiResults.Json(Detail(record), ApiJsonContext.Default.RunDetail);
        }

        // The history is written before a run leaves the active list, so a run that is in neither does not exist (any more).
        ActiveRunInfo? active = engine.ActiveRuns.FirstOrDefault(run => string.Equals(run.RunId, id, StringComparison.Ordinal));
        if (active is null)
        {
            return ApiResults.NotFound("unknown_run", "There is no run with this id.");
        }

        var running = new RunDetail(
            active.RunId,
            active.CommandId,
            active.Source,
            active.State == ActiveRunState.Running ? "running" : "waiting",
            active.AcceptedAt,
            0,
            null,
            null,
            false,
            null,
            null,
            active.ParentRunId,
            null,
            string.Empty,
            string.Empty,
            false);
        return ApiResults.Json(running, ApiJsonContext.Default.RunDetail);
    }

    private static async Task<IResult> StreamRunAsync(
        string id,
        HttpContext context,
        RunOutputHub hub,
        IRunHistory history,
        CancellationToken cancellationToken)
    {
        RunOutputFeed? feed = hub.Find(id);
        RunRecord? record = null;
        if (feed is null)
        {
            record = await history.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                return ApiResults.NotFound("unknown_run", "There is no run with this id.");
            }
        }

        try
        {
            await ServerSentEvents.StartAsync(context, cancellationToken).ConfigureAwait(false);
            if (feed is not null)
            {
                await foreach (RunOutputItem item in feed.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (item.Chunk is { } chunk)
                    {
                        await WriteOutputAsync(context.Response, chunk.Source, chunk.Text, chunk.Timestamp, cancellationToken).ConfigureAwait(false);
                    }
                    else if (item.Final is { } final)
                    {
                        await WriteEndAsync(context.Response, EndOf(final), cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                // The run is over: replay what the history kept.
                if (record!.StandardOutput.Length > 0)
                {
                    await WriteOutputAsync(context.Response, ProcessOutputSource.StandardOutput, record.StandardOutput, record.StartedAt, cancellationToken).ConfigureAwait(false);
                }

                if (record.StandardError.Length > 0)
                {
                    await WriteOutputAsync(context.Response, ProcessOutputSource.StandardError, record.StandardError, record.StartedAt, cancellationToken).ConfigureAwait(false);
                }

                await WriteEndAsync(context.Response, EndOf(record), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // The client went away; the run is not affected.
        }

        return Results.Empty;
    }

    private static Task WriteOutputAsync(HttpResponse response, ProcessOutputSource source, string text, DateTimeOffset timestamp, CancellationToken cancellationToken)
    {
        var data = new RunOutputEvent(source == ProcessOutputSource.StandardOutput ? "stdout" : "stderr", text, timestamp);
        return ServerSentEvents.WriteAsync(response, "output", JsonSerializer.Serialize(data, ApiJsonContext.Default.RunOutputEvent), cancellationToken);
    }

    private static Task WriteEndAsync(HttpResponse response, RunEndEvent end, CancellationToken cancellationToken)
    {
        return ServerSentEvents.WriteAsync(response, "end", JsonSerializer.Serialize(end, ApiJsonContext.Default.RunEndEvent), cancellationToken);
    }

    private static RunEndEvent EndOf(RunResult result)
    {
        return new RunEndEvent(
            result.RunId,
            result.Succeeded,
            result.ExitCode,
            result.ErrorCode,
            result.ErrorMessage,
            (long)result.Duration.TotalMilliseconds,
            result.OutputTruncated);
    }

    private static RunEndEvent EndOf(RunRecord record)
    {
        return new RunEndEvent(record.Id, record.Succeeded, record.ExitCode, record.ErrorCode, null, (long)record.Duration.TotalMilliseconds, record.OutputTruncated);
    }

    private static RunSummary Summarize(RunRecord record)
    {
        return new RunSummary(
            record.Id,
            record.Command,
            record.Source,
            "finished",
            record.StartedAt,
            (long)record.Duration.TotalMilliseconds,
            record.ExitCode,
            record.ErrorCode,
            record.Succeeded,
            record.ClientId,
            record.ParentRunId);
    }

    private static RunDetail Detail(RunRecord record)
    {
        return new RunDetail(
            record.Id,
            record.Command,
            record.Source,
            "finished",
            record.StartedAt,
            (long)record.Duration.TotalMilliseconds,
            record.ExitCode,
            record.ErrorCode,
            record.Succeeded,
            record.ClientId,
            record.UserId,
            record.ParentRunId,
            record.Variables,
            record.StandardOutput,
            record.StandardError,
            record.OutputTruncated);
    }

    private static string? Blank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
