using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;
using static QoffeeAutomat.Core.Tests.Metrics.Custom.Sensors;

namespace QoffeeAutomat.Core.Tests.Metrics.Custom;

/// <summary>A platform sensor type with a result that the test decides.</summary>
internal sealed class FakeSensorHandler : ISensorSourceHandler
{
    private int _samples;

    public FakeSensorHandler(string sourceType)
    {
        SourceType = sourceType;
    }

    public string SourceType { get; }

    public List<ValidationError> Problems { get; } = [];

    public Func<SensorDocument, SensorParseResult> Sampler { get; set; } = _ => SensorParseResult.Success(42.0);

    public int Samples => Volatile.Read(ref _samples);

    public IReadOnlyList<ValidationError> Validate(SensorDocument sensor) => Problems;

    public ValueTask<SensorParseResult> SampleAsync(SensorDocument sensor, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _samples);
        return ValueTask.FromResult(Sampler(sensor));
    }
}

/// <summary>The sensor types that need the operating system: handlers, unsupported types and validation (PLAN.md T307).</summary>
[Trait("Category", "CustomSensors")]
public sealed class SensorSourceHandlerTests
{
    private static SensorDocument Counter(string id) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        "sensor",
        "number",
        new PerfCounterSensorSource("Processor", "% Processor Time", "_Total"),
        IntervalSeconds: 1);

    private static SensorDocument Service(string id) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        "sensor",
        "enum",
        new ServiceStateSensorSource("EventLog"),
        Options: ["running", "stopped"],
        IntervalSeconds: 1);

    private static SensorDocument Cim(string id) => new(
        DocumentSchema.CurrentVersion,
        id,
        id,
        true,
        false,
        "sensor",
        "string",
        new CimQuerySensorSource("root\\cimv2", "Win32_OperatingSystem", "Caption"),
        IntervalSeconds: 1);

    [Fact]
    public void Without_handlers_the_platform_types_are_unsupported_and_the_others_are_not()
    {
        var support = new SensorTypeSupport();

        foreach (SensorDocument sensor in new[] { Counter("a"), Service("b"), Cim("c") })
        {
            ValidationError error = Assert.Single(support.Validate(sensor));
            Assert.Equal("/source/type", error.Path);
            Assert.Equal(ValidationCodes.Unsupported, error.Code);
            Assert.Contains(sensor.Source.Type, error.Message, StringComparison.Ordinal);
            Assert.False(support.IsSupported(sensor.Source.Type));
        }

        Assert.Empty(support.Validate(Script("script")));
        Assert.Empty(support.Validate(Process("process", "notepad")));
        Assert.True(support.IsSupported(SensorSourceTypes.PowerShell));
        Assert.True(support.IsSupported(SensorSourceTypes.ProcessRunning));
        Assert.True(support.IsSupported(SensorSourceTypes.State));
        Assert.False(support.IsSupported("something_else"));
    }

    [Fact]
    public void A_registered_handler_makes_its_type_supported_and_validates_the_definition()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.PerfCounter);
        var support = new SensorTypeSupport([handler]);

        Assert.Empty(support.Validate(Counter("ok")));
        Assert.True(support.IsSupported(SensorSourceTypes.PerfCounter));
        Assert.Same(handler, support.HandlerFor(SensorSourceTypes.PerfCounter));
        Assert.Null(support.HandlerFor(SensorSourceTypes.CimQuery));

        handler.Problems.Add(new ValidationError("/source/counter", ValidationCodes.Invalid, "bad"));
        ValidationError problem = Assert.Single(support.Validate(Counter("bad")));
        Assert.Equal("/source/counter", problem.Path);

        // The other platform types still have no handler.
        Assert.Equal(ValidationCodes.Unsupported, Assert.Single(support.Validate(Service("service"))).Code);
    }

    [Fact]
    public async Task A_sensor_of_a_platform_type_is_sampled_by_its_handler_and_its_status_is_recorded()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.PerfCounter);
        await using SensorHarness harness = await SensorHarness.CreateAsync(handlers: [handler]);
        await harness.SaveAsync(Counter("cpu_counter"));

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("cpu_counter");

        Assert.Equal(42.0, values["custom.cpu_counter"]);
        Assert.Equal(1, handler.Samples);
        CustomSensorStatus status = harness.Provider.GetStatus("cpu_counter")!;
        Assert.True(status.IsAvailable);
        Assert.Null(status.LastError);
        Assert.Empty(harness.Launcher.Calls);
    }

    [Fact]
    public async Task A_handler_that_cannot_read_makes_only_its_sensor_unavailable_with_the_reason()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.PerfCounter)
        {
            Sampler = _ => SensorParseResult.Fail("The performance counter 'X' cannot be opened: no such category."),
        };
        await using SensorHarness harness = await SensorHarness.CreateAsync(handlers: [handler]);
        await harness.SaveAsync(Counter("broken"), Script("good"));
        harness.Launcher.Output("good", "7");

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("broken", "good");

        Assert.Null(values["custom.broken"]);
        Assert.Equal(7.0, values["custom.good"]);
        CustomSensorStatus status = harness.Provider.GetStatus("broken")!;
        Assert.False(status.IsAvailable);
        Assert.Contains("no such category", status.LastError, StringComparison.Ordinal);
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task A_handler_that_throws_is_contained_like_a_failing_script()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.CimQuery)
        {
            Sampler = _ => throw new InvalidOperationException("provider exploded"),
        };
        await using SensorHarness harness = await SensorHarness.CreateAsync(handlers: [handler]);
        await harness.SaveAsync(Cim("os_caption"));

        IReadOnlyDictionary<string, object?> values = await harness.SampleAsync("os_caption");

        Assert.Null(values["custom.os_caption"]);
        Assert.Contains("provider exploded", harness.Provider.GetStatus("os_caption")!.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Testing_a_draft_uses_the_handler_and_rounds_to_the_precision()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.PerfCounter)
        {
            Sampler = _ => SensorParseResult.Success(12.3456),
        };
        await using SensorHarness harness = await SensorHarness.CreateAsync(handlers: [handler]);

        SensorTestOutcome outcome = await harness.Provider.TestAsync(Counter("draft") with { Precision = 1 }, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(12.3, outcome.Value);
        Assert.Null(outcome.Error);
        Assert.Null(harness.Provider.GetStatus("draft"));
    }

    [Fact]
    public async Task Testing_a_draft_without_a_handler_reports_the_type_as_not_supported()
    {
        await using SensorHarness harness = await SensorHarness.CreateAsync();

        SensorTestOutcome outcome = await harness.Provider.TestAsync(Service("draft"), CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Value);
        Assert.Contains("'service_state' is not supported", outcome.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_provider_exposes_the_support_table_it_was_built_with()
    {
        var handler = new FakeSensorHandler(SensorSourceTypes.ServiceState);
        await using SensorHarness harness = await SensorHarness.CreateAsync(handlers: [handler]);

        Assert.Same(handler, harness.Provider.TypeSupport.HandlerFor(SensorSourceTypes.ServiceState));
        Assert.Empty(harness.Provider.TypeSupport.Validate(Service("service")));
    }
}
