using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Secrets.Named;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The endpoints for the named secrets of scripts (PLAN.md S08, S09, T706). A secret has a name and a value; the value is
/// write-only: it is stored encrypted in the secrets file and no endpoint, export, diagnostics bundle or log returns it. A
/// command lists the names in <c>secrets</c> and its run receives the values as <c>QA_SECRET_&lt;NAME&gt;</c> environment variables.
/// </summary>
public static class SecretEndpoints
{
    /// <summary>The largest request body: a value of 4096 characters with the JSON around it (escaping can multiply the size).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private const string Tag = "Secrets";

    /// <summary>Maps the endpoints below <c>/api/v1/secrets</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/secrets", ListSecretsAsync)
            .Describe(Tag, "listSecrets", "Lists the names of the secrets and the commands that use them. Values are never returned.")
            .Returns<SecretList>();
        api.MapPut("/secrets/{name}", PutSecretAsync)
            .Describe(Tag, "putSecret", "Creates a secret or replaces its value. The value is write-only.")
            .Takes<SecretWriteRequest>()
            .Returns<SecretSummary>()
            .Returns<SecretSummary>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, ApiResults.UnprocessableContent, StatusCodes.Status500InternalServerError);
        api.MapDelete("/secrets/{name}", DeleteSecretAsync)
            .Describe(Tag, "deleteSecret", "Deletes a secret. Commands that still list it fail with the error internal until the name is removed from them.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status404NotFound, ApiResults.UnprocessableContent, StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> ListSecretsAsync(INamedSecretStore secrets, IConfigStore config, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<string> names = await secrets.ListAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CommandDocument> commands = config.Effective.List<CommandDocument>();
            SecretSummary[] summaries = [.. names.Select(name => Summarize(name, commands))];
            return ApiResults.Json(new SecretList(summaries), ApiJsonContext.Default.SecretList);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static async Task<IResult> PutSecretAsync(string name, INamedSecretStore secrets, IConfigStore config, HttpContext context, CancellationToken cancellationToken)
    {
        if (!NamedSecretNames.IsValid(name))
        {
            return InvalidName();
        }

        (SecretWriteRequest? request, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiJsonContext.Default.SecretWriteRequest, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (request is null)
        {
            return failure!;
        }

        if (NamedSecretNames.DescribeValueProblem(request.Value) is { } problem)
        {
            return ApiResults.ValidationFailed([new ValidationError("/value", ValidationCodes.Invalid, problem)]);
        }

        try
        {
            bool created = await secrets.SetAsync(name, request.Value, cancellationToken).ConfigureAwait(false);
            if (created)
            {
                context.Response.Headers.Location = "/api/v1/secrets/" + name;
            }

            return ApiResults.Json(
                Summarize(name, config.Effective.List<CommandDocument>()),
                ApiJsonContext.Default.SecretSummary,
                created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static async Task<IResult> DeleteSecretAsync(string name, INamedSecretStore secrets, CancellationToken cancellationToken)
    {
        if (!NamedSecretNames.IsValid(name))
        {
            return InvalidName();
        }

        try
        {
            return await secrets.RemoveAsync(name, cancellationToken).ConfigureAwait(false)
                ? Results.NoContent()
                : ApiResults.NotFound("unknown_secret", "There is no secret with this name.");
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static SecretSummary Summarize(string name, IReadOnlyList<CommandDocument> commands)
    {
        string[] users =
        [
            .. commands
                .Where(command => command.Secrets?.Contains(name, StringComparer.Ordinal) == true)
                .Select(command => command.Id)
                .Order(StringComparer.Ordinal),
        ];
        return new SecretSummary(name, users.Length == 0 ? null : users);
    }

    private static IResult InvalidName()
    {
        return ApiResults.ValidationFailed(
            [new ValidationError("/name", ValidationCodes.Invalid, "The name must match ^[a-z][a-z0-9_]{0,62}$.")]);
    }

    private static IResult StoreUnavailable(SecretStoreException exception)
    {
        // The message of the exception never contains a secret value.
        return ApiResults.Problem(StatusCodes.Status500InternalServerError, "secret_store_unavailable", exception.Message);
    }
}
