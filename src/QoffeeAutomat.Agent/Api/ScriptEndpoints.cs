using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The script endpoints (PLAN.md S08): list, read, write and delete the files in the scripts directory. A
/// name is a plain file name; the store rejects anything that could leave the directory. The ETag of a
/// script is the SHA-256 of its bytes.
/// </summary>
public static class ScriptEndpoints
{
    /// <summary>The largest request body: a script of 1 MiB plus the JSON around it (escaping can double the size).</summary>
    public const int MaxBodyBytes = 3 * 1024 * 1024;

    private const string Tag = "Scripts";

    /// <summary>Maps the endpoints below <c>/api/v1/scripts</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/scripts", ListScriptsAsync)
            .Describe(Tag, "listScripts", "Lists the script files.")
            .Returns<ScriptSummaryList>();
        api.MapGet("/scripts/{name}", GetScriptAsync)
            .Describe(Tag, "getScript", "Reads a script file; the ETag is the hash of its bytes.")
            .Returns<ScriptContent>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/scripts/{name}", PutScriptAsync)
            .GuardsLocalConfig(LocalConfigArea.Script)
            .Describe(Tag, "putScript", "Creates or replaces a script file.")
            .Takes<ScriptWriteRequest>()
            .Returns<ScriptContent>()
            .Returns<ScriptContent>(StatusCodes.Status201Created)
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, ApiResults.UnprocessableContent);
        api.MapDelete("/scripts/{name}", DeleteScriptAsync)
            .GuardsLocalConfig(LocalConfigArea.Script)
            .Describe(Tag, "deleteScript", "Deletes a script file.")
            .ReturnsNothing()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status404NotFound, ApiResults.UnprocessableContent);
    }

    private static async Task<IResult> ListScriptsAsync(IConfigStore config, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScriptInfo> scripts = await config.Effective.ListScriptsAsync(cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(
            new ScriptSummaryList([.. scripts.Select(script => new ScriptSummary(script.Name, script.ETag, script.ModifiedAt))]),
            ApiJsonContext.Default.ScriptSummaryList);
    }

    private static async Task<IResult> GetScriptAsync(string name, IConfigStore config, HttpContext context, CancellationToken cancellationToken)
    {
        ScriptFile? script = await config.Effective.GetScriptAsync(name, cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            return ApiResults.NotFound("unknown_script", "There is no script with this name.");
        }

        ApiETag.Set(context.Response, ApiETag.ForHash(script.ETag));
        return ApiResults.Json(new ScriptContent(script.Name, script.Content, script.ETag, script.ModifiedAt), ApiJsonContext.Default.ScriptContent);
    }

    private static async Task<IResult> PutScriptAsync(string name, IConfigStore config, HttpContext context, CancellationToken cancellationToken)
    {
        (ScriptWriteRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.ScriptWriteRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        bool existed = await config.Effective.GetScriptAsync(name, cancellationToken).ConfigureAwait(false) is not null;
        ScriptResult result = await config.Local.SaveScriptAsync(name, request.Content, ApiETag.ReadHash(context.Request), cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case ChangeStatus.Applied:
                ScriptFile? stored = await config.Effective.GetScriptAsync(name, cancellationToken).ConfigureAwait(false);
                ApiETag.Set(context.Response, ApiETag.ForHash(result.ETag!));
                if (!existed)
                {
                    context.Response.Headers.Location = "/api/v1/scripts/" + name;
                }

                return ApiResults.Json(
                    new ScriptContent(name, request.Content, result.ETag!, stored?.ModifiedAt ?? DateTimeOffset.UtcNow),
                    ApiJsonContext.Default.ScriptContent,
                    existed ? StatusCodes.Status200OK : StatusCodes.Status201Created);
            case ChangeStatus.Conflict:
                return ApiResults.Problem(
                    StatusCodes.Status412PreconditionFailed,
                    "revision_mismatch",
                    "The script was changed since it was read. Read it again and repeat the change.");
            default:
                return ApiResults.ValidationFailed(result.Errors);
        }
    }

    private static async Task<IResult> DeleteScriptAsync(string name, IConfigStore config, HttpContext context, CancellationToken cancellationToken)
    {
        ScriptResult result = await config.Local.DeleteScriptAsync(name, ApiETag.ReadHash(context.Request), cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ChangeStatus.Applied => Results.NoContent(),
            ChangeStatus.Conflict => ApiResults.Problem(
                StatusCodes.Status412PreconditionFailed,
                "revision_mismatch",
                "The script was changed since it was read. Read it again and repeat the change."),
            ChangeStatus.Invalid => ApiResults.ValidationFailed(result.Errors),
            _ => ApiResults.NotFound("unknown_script", "There is no script with this name."),
        };
    }
}
