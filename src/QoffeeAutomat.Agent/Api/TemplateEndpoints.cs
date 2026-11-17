using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Core.Templates;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The template gallery endpoints of the management API (PLAN.md S08, T701): the templates that ship with the agent, a
/// preview of one with its scripts, and adding one to the configuration as a new command, sensor or mode group. Adding goes through
/// the config store like any other write, so the copy is validated, stamped and announced at once.
/// </summary>
public static class TemplateEndpoints
{
    private const string Tag = "Templates";

    /// <summary>Maps the endpoints below <c>/api/v1/templates</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/templates", ListTemplatesAsync)
            .Describe(Tag, "listTemplates", "Lists the templates of the gallery.")
            .Returns<TemplateList>();
        api.MapGet("/templates/{id}", GetTemplateAsync)
            .Describe(Tag, "getTemplate", "Reads a template with its scripts, for the preview.")
            .Returns<TemplateDetail>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPost("/templates/{id}/instantiate", InstantiateTemplateAsync)
            .Describe(Tag, "instantiateTemplate", "Adds a template as a new command, sensor or mode group with a unique id and copied scripts.")
            .Returns<TemplateInstantiation>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status404NotFound, ApiResults.UnprocessableContent);
    }

    private static async Task<IResult> ListTemplatesAsync(TemplateCatalogSource templates, CancellationToken cancellationToken)
    {
        TemplateCatalog catalog = await templates.GetAsync(cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(new TemplateList([.. catalog.Entries.Select(Summarize)]), ApiDocumentJsonContext.Default.TemplateList);
    }

    private static async Task<IResult> GetTemplateAsync(string id, TemplateCatalogSource templates, CancellationToken cancellationToken)
    {
        TemplateCatalog catalog = await templates.GetAsync(cancellationToken).ConfigureAwait(false);
        TemplateEntry? entry = catalog.Find(id);
        if (entry is null)
        {
            return UnknownTemplate();
        }

        var detail = new TemplateDetail(
            Summarize(entry),
            entry.Command,
            entry.Sensor,
            [.. entry.Scripts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new TemplateScript(pair.Key, pair.Value))],
            entry.Mode);
        return ApiResults.Json(detail, ApiDocumentJsonContext.Default.TemplateDetail);
    }

    private static async Task<IResult> InstantiateTemplateAsync(string id, TemplateInstantiator instantiator, HttpContext context, CancellationToken cancellationToken)
    {
        TemplateInstantiationResult result = await instantiator.InstantiateAsync(id, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case TemplateInstantiationStatus.Created:
                TemplateInstantiation created = result.Instantiation!;
                context.Response.Headers.Location = LocationPrefix(created.Kind) + created.Id;
                return ApiResults.Json(created, ApiDocumentJsonContext.Default.TemplateInstantiation, StatusCodes.Status201Created);
            case TemplateInstantiationStatus.NotFound:
                return UnknownTemplate();
            default:
                return ApiResults.ValidationFailed(result.Errors);
        }
    }

    private static string LocationPrefix(string kind)
    {
        return kind switch
        {
            TemplateKinds.Command => "/api/v1/commands/",
            TemplateKinds.Mode => "/api/v1/modes/",
            _ => "/api/v1/sensors/",
        };
    }

    private static TemplateSummary Summarize(TemplateEntry entry)
    {
        return new TemplateSummary(
            entry.Id,
            entry.Kind,
            entry.Name,
            entry.Description,
            entry.Icon,
            entry.ActionType,
            entry.Requires.Count > 0 ? entry.Requires : null);
    }

    private static IResult UnknownTemplate()
    {
        return ApiResults.NotFound("unknown_template", "There is no template with this id.");
    }
}
