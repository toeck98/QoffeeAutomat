using QoffeeAutomat.Agent.Api.OpenApi;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Api;

/// <summary>
/// The custom sensor endpoints (PLAN.md S08): CRUD of the sensor documents and the dry run of a draft. A saved
/// sensor appears in the metric catalog as <c>custom.&lt;id&gt;</c> without a restart.
/// </summary>
public static class SensorEndpoints
{
    /// <summary>The largest sensor document that is read.</summary>
    public const int MaxBodyBytes = 256 * 1024;

    private const string Tag = "Sensors";

    /// <summary>Maps the endpoints below <c>/api/v1/sensors</c>.</summary>
    /// <param name="api">The <c>/api/v1</c> group.</param>
    public static void Map(RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/sensors", ListSensors)
            .Describe(Tag, "listSensors", "Lists all custom sensors.")
            .Returns<SensorList>();
        api.MapPost("/sensors", CreateSensorAsync)
            .Describe(Tag, "createSensor", "Creates a custom sensor; the id must not exist yet.")
            .Takes<SensorDocument>()
            .Returns<SensorDocument>(StatusCodes.Status201Created)
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status409Conflict, ApiResults.UnprocessableContent);
        api.MapPost("/sensors/test", TestSensorAsync)
            .Describe(Tag, "testSensor", "Samples a draft sensor once without saving it.")
            .Takes<SensorDocument>()
            .Returns<SensorTestResult>()
            .Fails(StatusCodes.Status400BadRequest, ApiResults.UnprocessableContent);
        api.MapGet("/sensors/{id}", GetSensor)
            .Describe(Tag, "getSensor", "Reads a custom sensor; the ETag is its revision.")
            .Returns<SensorDocument>()
            .Fails(StatusCodes.Status404NotFound);
        api.MapPut("/sensors/{id}", ReplaceSensorAsync)
            .Describe(Tag, "replaceSensor", "Replaces a custom sensor.")
            .Takes<SensorDocument>()
            .Returns<SensorDocument>()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound, ApiResults.UnprocessableContent);
        api.MapDelete("/sensors/{id}", DeleteSensorAsync)
            .Describe(Tag, "deleteSensor", "Deletes a custom sensor; its earlier revisions are kept.")
            .ReturnsNothing()
            .HonoursIfMatch()
            .Fails(StatusCodes.Status400BadRequest, StatusCodes.Status404NotFound);
    }

    private static IResult ListSensors(IConfigStore config)
    {
        return ApiResults.Json(new SensorList(config.Effective.List<SensorDocument>()), ApiDocumentJsonContext.Default.SensorList);
    }

    private static IResult GetSensor(string id, IConfigStore config, HttpContext context)
    {
        SensorDocument? sensor = config.Effective.GetDocument<SensorDocument>(id);
        if (sensor is null)
        {
            return UnknownSensor();
        }

        ApiETag.Set(context.Response, ApiETag.ForRevision(sensor.Revision));
        return ApiResults.Json(sensor, ApiDocumentJsonContext.Default.SensorDocument);
    }

    private static async Task<IResult> CreateSensorAsync(
        HttpContext context,
        IConfigStore config,
        CustomSensorProvider sensors,
        CancellationToken cancellationToken)
    {
        (SensorDocument? sensor, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.SensorDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (sensor is null)
        {
            return failure!;
        }

        IResult? unsupported = CheckPlatformSupport(sensors, sensor);
        if (unsupported is not null)
        {
            return unsupported;
        }

        SaveResult<SensorDocument> saved = await config.Local.SaveAsync(sensor, 0, cancellationToken).ConfigureAwait(false);
        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.SensorDocument, context, created: true, "/api/v1/sensors/" + sensor.Id);
    }

    private static async Task<IResult> ReplaceSensorAsync(
        string id,
        HttpContext context,
        IConfigStore config,
        CustomSensorProvider sensors,
        CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        (SensorDocument? sensor, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.SensorDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (sensor is null)
        {
            return failure!;
        }

        if (!string.Equals(sensor.Id, id, StringComparison.Ordinal))
        {
            return ApiResults.ValidationFailed([new ValidationError("/id", "mismatch", "The id of the document must be the id in the URL.")]);
        }

        if (config.Local.GetDocument<SensorDocument>(id) is null)
        {
            return UnknownSensor();
        }

        IResult? unsupported = CheckPlatformSupport(sensors, sensor);
        if (unsupported is not null)
        {
            return unsupported;
        }

        SaveResult<SensorDocument> saved = await config.Local.SaveAsync(sensor, expected, cancellationToken).ConfigureAwait(false);
        return ApiDocuments.SaveOutcome(saved, ApiDocumentJsonContext.Default.SensorDocument, context, created: false, "/api/v1/sensors/" + id);
    }

    private static async Task<IResult> DeleteSensorAsync(string id, HttpContext context, IConfigStore config, CancellationToken cancellationToken)
    {
        IResult? header = ApiETag.ReadRevision(context.Request, out int? expected);
        if (header is not null)
        {
            return header;
        }

        DeleteResult result = await config.Local.DeleteAsync<SensorDocument>(id, expected, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            ChangeStatus.Applied => Results.NoContent(),
            ChangeStatus.Conflict => ApiETag.Stale(result.CurrentRevision),
            _ => UnknownSensor(),
        };
    }

    private static async Task<IResult> TestSensorAsync(
        HttpContext context,
        CustomSensorProvider sensors,
        CancellationToken cancellationToken)
    {
        (SensorDocument? draft, IResult? failure) = await ApiResults.ReadBodyAsync(
            context.Request, ApiDocumentJsonContext.Default.SensorDocument, MaxBodyBytes, cancellationToken).ConfigureAwait(false);
        if (draft is null)
        {
            return failure!;
        }

        SensorDocument normalized = ApiDocuments.Normalize(draft);
        ValidationResult validation = DocumentValidator.ValidateSensor(normalized);
        if (!validation.IsValid)
        {
            return ApiResults.ValidationFailed(validation.Errors);
        }

        IResult? unsupported = CheckPlatformSupport(sensors, normalized);
        if (unsupported is not null)
        {
            return unsupported;
        }

        SensorTestOutcome outcome = await sensors.TestAsync(normalized, cancellationToken).ConfigureAwait(false);
        return ApiResults.Json(
            new SensorTestResult(outcome.Succeeded, ApiJson.ElementOrNull(outcome.Value), outcome.Error, (long)outcome.Duration.TotalMilliseconds),
            ApiJsonContext.Default.SensorTestResult);
    }

    /// <summary>
    /// Refuses a sensor type that this platform cannot sample (<c>perf_counter</c>, <c>cim_query</c> and <c>service_state</c>
    /// off Windows, code <c>unsupported</c>) and the definitions that the Windows handlers find malformed, so that the editor
    /// learns about it when the sensor is saved or tested and not from a sensor that stays unavailable.
    /// </summary>
    private static IResult? CheckPlatformSupport(CustomSensorProvider sensors, SensorDocument sensor)
    {
        IReadOnlyList<ValidationError> problems = sensors.TypeSupport.Validate(sensor);
        return problems.Count == 0 ? null : ApiResults.ValidationFailed(problems);
    }

    private static IResult UnknownSensor()
    {
        return ApiResults.NotFound("unknown_sensor", "There is no sensor with this id.");
    }
}
