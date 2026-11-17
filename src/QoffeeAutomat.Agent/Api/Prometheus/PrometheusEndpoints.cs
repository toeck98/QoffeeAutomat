using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Api.Prometheus;

/// <summary>
/// The management endpoints of the Prometheus export (PLAN.md S08, T709): the state of the export and the read-only token that
/// <c>GET /metrics</c> needs. Switching the export on and choosing the metrics is part of the settings (<c>PUT /settings</c>,
/// the member <c>prometheus</c>).
/// </summary>
public static class PrometheusEndpoints
{
    private const string Tag = "Prometheus";

    /// <summary>Maps the endpoints below <c>/api/v1/prometheus</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/prometheus", GetStatusAsync)
            .Describe(Tag, "getPrometheus", "Reports whether the Prometheus endpoint is on, whether its token exists and which metrics it exports.")
            .Returns<PrometheusStatus>()
            .Fails(StatusCodes.Status500InternalServerError);
        api.MapPost("/prometheus/token", CreateTokenAsync)
            .Describe(Tag, "createPrometheusToken", "Creates the read-only token of the Prometheus endpoint, replacing an existing one. The token is in this answer only.")
            .Returns<PrometheusToken>()
            .Fails(StatusCodes.Status500InternalServerError);
        api.MapDelete("/prometheus/token", DeleteTokenAsync)
            .Describe(Tag, "deletePrometheusToken", "Removes the token; the endpoint then refuses every request.")
            .ReturnsNothing()
            .Fails(StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetStatusAsync(IConfigStore config, PrometheusTokenStore tokens, CancellationToken cancellationToken)
    {
        try
        {
            AgentSettingsDocument? settings = config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
            bool tokenSet = await tokens.IsSetAsync(cancellationToken).ConfigureAwait(false);
            string[] metrics = [.. settings?.Prometheus?.Metrics ?? []];
            return ApiResults.Json(new PrometheusStatus(settings?.EffectivePrometheusEnabled ?? false, tokenSet, metrics), ApiJsonContext.Default.PrometheusStatus);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static async Task<IResult> CreateTokenAsync(PrometheusTokenStore tokens, HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            string token = await tokens.CreateAsync(cancellationToken).ConfigureAwait(false);

            // The token is a secret that is shown once: nothing may keep a copy of the answer.
            context.Response.Headers.CacheControl = "no-store";
            return ApiResults.Json(new PrometheusToken(token), ApiJsonContext.Default.PrometheusToken);
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static async Task<IResult> DeleteTokenAsync(PrometheusTokenStore tokens, CancellationToken cancellationToken)
    {
        try
        {
            await tokens.RevokeAsync(cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (SecretStoreException exception)
        {
            return StoreUnavailable(exception);
        }
    }

    private static IResult StoreUnavailable(SecretStoreException exception)
    {
        // The message of the exception never contains a secret value.
        return ApiResults.Problem(StatusCodes.Status500InternalServerError, "secret_store_unavailable", exception.Message);
    }
}
