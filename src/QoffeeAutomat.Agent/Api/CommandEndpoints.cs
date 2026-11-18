using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Commands;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The command endpoints of the management API (PLAN.md S08): list, create, read, replace, delete,
/// <c>PATCH</c> for <c>enabled</c> and <c>exposeToHa</c>, and the validation of a draft. Every write goes through
/// the config store, which validates, stamps and stores the document atomically; the command registry and the
/// manifest follow the change by themselves, so a saved command is runnable and announced at once.
/// </summary>
public static class CommandEndpoints
{
    /// <summary>The largest command document that is read.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private const string Tag = "Commands";

    /// <summary>Maps the endpoints below <c>/api/v1/commands</c> (the runtime endpoints are mapped by <c>RuntimeEndpoints</c>).</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/commands", ListCommands)
            .Describe(Tag, "listCommands", "Lists all commands.")
            .Returns<CommandList>();
        api.MapPost("/commands", CreateCommandAsync)
            .Describe(Tag, "createCommand", "Creates a command; the id must not exist yet.")
            .Takes<CommandDocument>()
            .Returns<CommandDocument>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, ApiResults.UnprocessableContent);
        api.MapPost("/commands/validate", ValidateCommandAsync)
            .Describe(Tag, "validateCommand", "Validates a draft command without saving it.")
            .Takes<CommandDocument>()
            .Returns<ValidationReport>()
            .Fails(StatusCodes.Status400BadRequest);
        api.MapGet("/commands/{id}", GetCommand)
            .Describe(Tag, "getCommand", "Reads a command; the ETag is its revision.")
            .Returns<CommandDocument>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/commands/{id}", ReplaceCommandAsync)
            .Describe(Tag, "replaceCommand", "Replaces a command.")
            .Takes<CommandDocument>()
            .Returns<CommandDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, ApiResults.UnprocessableContent);
        api.MapPatch("/commands/{id}", PatchCommandAsync)
            .Describe(Tag, "patchCommand", "Changes enabled and exposeToHa of a command.")
            .Takes<CommandPatch>()
            .Returns<CommandDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, ApiResults.UnprocessableContent);
        api.MapDelete("/commands/{id}", DeleteCommandAsync)
            .Describe(Tag, "deleteCommand", "Deletes a command; its earlier revisions are kept.")
            .ReturnsNothing()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
    }

    private static IResult ListCommands(IConfigStore config)
    {
        return ApiResults.Json(new CommandList(config.Effective.List<CommandDocument>()), ApiDocumentJsonContext.Default.CommandList);
    }

    private static IResult GetCommand(string id, IConfigStore config, HttpContext context)
    {
        CommandDocument? command = config.Effective.GetDocument<CommandDocument>(id);
        if (command is null)
        {
            return UnknownCommand();
        }

        ApiETag.Set(context.Response, ApiETag.ForRevision(command.Revision));
        return ApiResults.Json(command, ApiDocumentJsonContext.Default.CommandDocument);
    }

    private static async Task<IResult> CreateCommandAsync(HttpContext context, IConfigStore config, CommandPlatformSupport support, CancellationToken cancellationToken)
    {
        (CommandDocument? command, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.CommandDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (command is null)
        {
            return failure!;
        }

        // A new command that this platform cannot run is refused with the reason (PLAN.md S17, T803); documents that are already
        // stored (restored, copied from another PC, pushed by a hub) are never touched.
        IReadOnlyList<ValidationError> unsupported = support.Check(command);
        if (unsupported.Count > 0)
        {
            return ApiResults.ValidationFailed(unsupported);
        }

        // Revision zero means "must not exist yet": a second create of the same id is a conflict, not an overwrite.
        SaveResult<CommandDocument> saved = await config.Local.SaveAsync(command, 0, cancellationToken).ConfigureAwait(false);
        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.CommandDocument, context, created: true, "/api/v1/commands/" + command.Id);
    }

    private static async Task<IResult> ValidateCommandAsync(HttpContext context, IConfigStore config, CommandPlatformSupport support, CancellationToken cancellationToken)
    {
        // The body is read by hand so that a draft with missing members is reported with JSON pointers instead of 400.
        (CommandDocument? draft, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.CommandDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return failure!;
        }

        CommandDocument normalized = ApiDocuments.Normalize(draft);
        ValidationResult result = DocumentValidator.ValidateCommand(normalized, ApiDocuments.CommandLookup(config));

        // The report also says what this platform cannot run (T803), so the editor can flag a command before it is saved.
        ValidationResult combined = new([.. result.Errors, .. support.Check(normalized)]);
        return ApiResults.Json(ApiDocuments.Report(combined), ApiJsonContext.Default.ValidationReport);
    }

    private static async Task<IResult> ReplaceCommandAsync(string id, HttpContext context, IConfigStore config, CommandPlatformSupport support, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        (CommandDocument? command, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.CommandDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (command is null)
        {
            return failure!;
        }

        if (!string.Equals(command.Id, id, StringComparison.Ordinal))
        {
            return ApiResults.ValidationFailed([new ValidationError("/id", "mismatch", "The id of the document must be the id in the URL.")]);
        }

        CommandDocument? stored = config.Local.GetDocument<CommandDocument>(id);
        if (stored is null)
        {
            return UnknownCommand();
        }

        // Turning a supported command into one that this platform cannot run is refused; a command that is already flagged can still be
        // edited (its description, its parameters), so that nothing the user wrote is locked away.
        IReadOnlyList<ValidationError> unsupported = support.Check(command);
        if (unsupported.Count > 0 && support.IsSupported(stored))
        {
            return ApiResults.ValidationFailed(unsupported);
        }

        SaveResult<CommandDocument> saved = await config.Local.SaveAsync(command, expected, cancellationToken).ConfigureAwait(false);
        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.CommandDocument, context, created: false, "/api/v1/commands/" + id);
    }

    private static async Task<IResult> PatchCommandAsync(string id, HttpContext context, IConfigStore config, CommandPlatformSupport support, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        (CommandPatch? patch, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.CommandPatch, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (patch is null)
        {
            return failure!;
        }

        if (patch.Enabled is null && patch.ExposeToHa is null)
        {
            return ApiResults.ValidationFailed([new ValidationError("/", ValidationCodes.Required, "Give enabled, exposeToHa or both.")]);
        }

        CommandDocument? existing = config.Local.GetDocument<CommandDocument>(id);
        if (existing is null)
        {
            return UnknownCommand();
        }

        // Switching a command on that this platform cannot run only produces failing runs; switching it off is always possible.
        if (patch.Enabled == true && support.Check(existing) is { Count: > 0 } unsupported)
        {
            return ApiResults.ValidationFailed(unsupported);
        }

        CommandDocument changed = existing with
        {
            Enabled = patch.Enabled ?? existing.Enabled,
            ExposeToHa = patch.ExposeToHa ?? existing.ExposeToHa,
        };

        // Without If-Match the change still applies to the revision that was read, so a concurrent edit is not overwritten silently.
        SaveResult<CommandDocument> saved = await config.Local.SaveAsync(changed, expected ?? existing.Revision, cancellationToken).ConfigureAwait(false);
        if (saved.Status == ChangeStatus.Conflict && expected is null)
        {
            return ApiResults.Problem(StatusCodes.Status409Conflict, "concurrent_modification", "The command was changed while the request was handled. Repeat it.", currentRevision: saved.CurrentRevision);
        }

        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.CommandDocument, context, created: false, "/api/v1/commands/" + id);
    }

    private static async Task<IResult> DeleteCommandAsync(string id, HttpContext context, IConfigStore config, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        DeleteResult result = await config.Local.DeleteAsync<CommandDocument>(id, expected, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ChangeStatus.Applied => Results.NoContent(),
            ChangeStatus.Conflict => ApiETag.Stale(result.CurrentRevision),
            _ => UnknownCommand(),
        };
    }

    private static IResult UnknownCommand()
    {
        return ApiResults.NotFound("unknown_command", "There is no command with this id.");
    }
}
