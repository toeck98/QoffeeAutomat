using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Execution.PowerShell;
using QoffeeAutomat.Core.Metrics.Custom;
using QoffeeAutomat.Core.Validation;
using QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

namespace QoffeeAutomat.Platform.Windows.Tests.Metrics;

internal sealed class FakeCounterReader : IPerformanceCounterReader
{
    public List<(string Category, string Counter, string? Instance)> Reads { get; } = [];

    public Func<SensorParseResult> Result { get; set; } = () => SensorParseResult.Success(37.5);

    public ValueTask<SensorParseResult> ReadAsync(string category, string counter, string? instance, CancellationToken cancellationToken)
    {
        Reads.Add((category, counter, instance));
        return ValueTask.FromResult(Result());
    }
}

internal sealed class FakeCimRunner : ICimQueryRunner
{
    public List<(string Namespace, string Class, string Property, string? Filter)> Queries { get; } = [];

    public CimReading Reading { get; set; } = new("Microsoft Windows 11 Pro", null);

    public Task<CimReading> QueryFirstAsync(string cimNamespace, string className, string property, string? filter, CancellationToken cancellationToken)
    {
        Queries.Add((cimNamespace, className, property, filter));
        return Task.FromResult(Reading);
    }
}

internal sealed class FakeServiceReader : IServiceStateReader
{
    public Dictionary<string, ServiceReading> Services { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ServiceReading Read(string serviceName)
    {
        return Services.GetValueOrDefault(serviceName) ?? ServiceReading.NotInstalled;
    }
}

/// <summary>
/// The Windows custom sensor types <c>perf_counter</c>, <c>cim_query</c> and <c>service_state</c> (PLAN.md S06, T307):
/// validation, conversion of values and the failures. The tests with fake readers run on every operating system; the tests with a
/// counter, a class and a service that every Windows installation has run on Windows only.
/// </summary>
[Trait("Category", "CustomSensors")]
public sealed class CustomSensorHandlerTests
{
    private static SensorDocument Sensor(SensorSource source, string valueType, IReadOnlyList<string>? options = null, int? precision = null) => new(
        DocumentSchema.CurrentVersion,
        "sensor_under_test",
        "Sensor under test",
        true,
        false,
        valueType == "bool" ? "binary_sensor" : "sensor",
        valueType,
        source,
        Options: options,
        Precision: precision,
        IntervalSeconds: 10);

    private static SensorDocument Counter(string category = "Processor", string counter = "% Processor Time", string? instance = "_Total", string valueType = "number") =>
        Sensor(new PerfCounterSensorSource(category, counter, instance), valueType);

    private static SensorDocument Cim(string valueType = "string", string cimNamespace = "root\\cimv2", string className = "Win32_OperatingSystem", string property = "Caption", string? filter = null, IReadOnlyList<string>? options = null) =>
        Sensor(new CimQuerySensorSource(cimNamespace, className, property, filter), valueType, options);

    private static SensorDocument Service(string service = "EventLog", string valueType = "enum", IReadOnlyList<string>? options = null) =>
        Sensor(new ServiceStateSensorSource(service), valueType, options ?? ["running", "stopped"]);

    private static string[] Paths(IReadOnlyList<ValidationError> errors) => [.. errors.Select(error => error.Path)];

    // ------------------------------------------------------------------
    // perf_counter
    // ------------------------------------------------------------------

    [Fact]
    public void A_perf_counter_sensor_reports_a_number_and_its_names_are_plain_text()
    {
        var handler = new PerfCounterSensorHandler(new FakeCounterReader());

        Assert.Equal(SensorSourceTypes.PerfCounter, handler.SourceType);
        Assert.Empty(handler.Validate(Counter()));
        Assert.Empty(handler.Validate(Counter(instance: null)));
        Assert.Empty(handler.Validate(Counter("Process", "Working Set", "chrome#1")));
        Assert.Equal(["/valueType"], Paths(handler.Validate(Counter(valueType: "string"))));
        Assert.Equal(["/source/category"], Paths(handler.Validate(Counter(category: new string('x', 129)))));
        Assert.Equal(["/source/counter"], Paths(handler.Validate(Counter(counter: "a\u0007b"))));
        Assert.Equal(["/source/instance"], Paths(handler.Validate(Counter(instance: new string('i', 257)))));

        // Missing members are the business of the document validator, not of the handler.
        Assert.Empty(handler.Validate(Counter(category: "", counter: " ")));
        Assert.Empty(handler.Validate(Cim()));
    }

    [Fact]
    public async Task A_perf_counter_sensor_reads_the_counter_with_its_category_counter_and_instance()
    {
        var reader = new FakeCounterReader();
        var handler = new PerfCounterSensorHandler(reader);

        SensorParseResult result = await handler.SampleAsync(Counter(), CancellationToken.None);
        SensorParseResult blankInstance = await handler.SampleAsync(Counter(instance: "  "), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(37.5, result.Value);
        Assert.Equal(("Processor", "% Processor Time", (string?)"_Total"), reader.Reads[0]);
        Assert.Null(reader.Reads[1].Instance);
        Assert.True(blankInstance.IsValid);
    }

    [Fact]
    public async Task A_counter_that_cannot_be_read_is_unavailable_with_the_reason()
    {
        var reader = new FakeCounterReader { Result = () => SensorParseResult.Fail("The performance counter 'X\\Y' cannot be opened: no such category.") };
        var handler = new PerfCounterSensorHandler(reader);

        SensorParseResult result = await handler.SampleAsync(Counter("X", "Y", null), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("no such category", result.Failure, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // cim_query
    // ------------------------------------------------------------------

    [Fact]
    public void A_cim_query_sensor_takes_only_plain_identifiers_so_nothing_can_change_the_query()
    {
        var handler = new CimQuerySensorHandler(new FakeCimRunner());

        Assert.Equal(SensorSourceTypes.CimQuery, handler.SourceType);
        Assert.Empty(handler.Validate(Cim()));
        Assert.Empty(handler.Validate(Cim(cimNamespace: "root/cimv2")));
        Assert.Empty(handler.Validate(Cim(cimNamespace: "root\\Microsoft\\Windows\\Storage", className: "MSFT_PhysicalDisk", property: "HealthStatus")));
        Assert.Empty(handler.Validate(Cim(className: "Win32_LogicalDisk", property: "FreeSpace", filter: "DeviceID='C:'")));

        Assert.Equal(["/source/namespace"], Paths(handler.Validate(Cim(cimNamespace: "root\\cimv2; drop"))));
        Assert.Equal(["/source/namespace"], Paths(handler.Validate(Cim(cimNamespace: "\\\\.\\root\\cimv2"))));
        Assert.Equal(["/source/class"], Paths(handler.Validate(Cim(className: "Win32_Process WHERE 1=1"))));
        Assert.Equal(["/source/class"], Paths(handler.Validate(Cim(className: "1Win32"))));
        Assert.Equal(["/source/property"], Paths(handler.Validate(Cim(property: "Caption, Name"))));
        Assert.Equal(["/source/property"], Paths(handler.Validate(Cim(property: "*"))));
        Assert.Equal(["/source/filter"], Paths(handler.Validate(Cim(filter: new string('f', 513)))));
        Assert.Equal(["/source/filter"], Paths(handler.Validate(Cim(filter: "a\nb"))));
        Assert.Empty(handler.Validate(Counter()));
    }

    [Fact]
    public async Task A_cim_query_asks_for_the_property_of_the_first_result_with_the_filter()
    {
        var runner = new FakeCimRunner();
        var handler = new CimQuerySensorHandler(runner);

        SensorParseResult result = await handler.SampleAsync(Cim(className: "Win32_LogicalDisk", property: "DeviceID", filter: "DriveType=3"), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("Microsoft Windows 11 Pro", result.Value);
        Assert.Equal(("root\\cimv2", "Win32_LogicalDisk", "DeviceID", (string?)"DriveType=3"), runner.Queries.Single());
    }

    private static readonly string[] ListSample = ["a", "b"];

    [Fact]
    public async Task A_query_that_fails_or_returns_nothing_is_unavailable_with_the_reason()
    {
        var runner = new FakeCimRunner { Reading = new CimReading(null, "The query returned no result.") };
        var handler = new CimQuerySensorHandler(runner);

        SensorParseResult none = await handler.SampleAsync(Cim(), CancellationToken.None);
        runner.Reading = new CimReading(null, null);
        SensorParseResult nothing = await handler.SampleAsync(Cim(), CancellationToken.None);
        runner.Reading = new CimReading(ListSample, null);
        SensorParseResult list = await handler.SampleAsync(Cim(), CancellationToken.None);

        Assert.False(none.IsValid);
        Assert.Equal("The query returned no result.", none.Failure);
        Assert.False(nothing.IsValid);
        Assert.False(list.IsValid);
        Assert.Contains("list", list.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("number", 16384000UL, 16384000.0)]
    [InlineData("number", 3, 3.0)]
    [InlineData("number", 12.5f, 12.5)]
    [InlineData("number", "42.25", 42.25)]
    public void A_cim_value_is_converted_to_a_number(string valueType, object raw, double expected)
    {
        SensorParseResult result = CimQuerySensorHandler.ConvertValue(raw, Cim(valueType));

        Assert.True(result.IsValid, result.Failure);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public void A_cim_value_is_converted_to_the_value_type_of_the_sensor()
    {
        Assert.Equal("Hello", CimQuerySensorHandler.ConvertValue("  Hello \r\n", Cim("string")).Value);
        Assert.Equal(string.Empty, CimQuerySensorHandler.ConvertValue("   ", Cim("string")).Value);
        Assert.Equal(true, CimQuerySensorHandler.ConvertValue(true, Cim("bool")).Value);
        Assert.Equal(false, CimQuerySensorHandler.ConvertValue((byte)0, Cim("bool")).Value);
        Assert.Equal(true, CimQuerySensorHandler.ConvertValue((ushort)1, Cim("bool")).Value);
        Assert.Equal("OK", CimQuerySensorHandler.ConvertValue("ok", Cim("enum", options: ["OK", "Warning"])).Value);

        SensorParseResult notANumber = CimQuerySensorHandler.ConvertValue("abc", Cim("number"));
        Assert.False(notANumber.IsValid);
        Assert.StartsWith("The value 'abc' is not a number", notANumber.Failure, StringComparison.Ordinal);
        Assert.False(CimQuerySensorHandler.ConvertValue("Unhealthy", Cim("enum", options: ["OK", "Warning"])).IsValid);
        Assert.False(CimQuerySensorHandler.ConvertValue("  ", Cim("number")).IsValid);
    }

    [WindowsFact]
    public void A_cim_date_and_time_becomes_a_timestamp()
    {
        SensorParseResult fromCim = CimQuerySensorHandler.ConvertValue("20261116060000.500000+000", Cim("timestamp"));
        SensorParseResult fromDate = CimQuerySensorHandler.ConvertValue(new DateTime(2026, 11, 16, 6, 0, 0, DateTimeKind.Utc), Cim("timestamp"));
        SensorParseResult broken = CimQuerySensorHandler.ConvertValue("not a date", Cim("timestamp"));

        Assert.True(fromCim.IsValid, fromCim.Failure);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero), Assert.IsType<DateTimeOffset>(fromCim.Value).ToUniversalTime().AddMilliseconds(-500));
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 6, 0, 0, TimeSpan.Zero), fromDate.Value);
        Assert.False(broken.IsValid);
    }

    // ------------------------------------------------------------------
    // service_state
    // ------------------------------------------------------------------

    [Fact]
    public void A_service_state_sensor_is_an_enum_with_the_states_of_the_service_control_manager()
    {
        var handler = new ServiceStateSensorHandler(new FakeServiceReader());

        Assert.Equal(SensorSourceTypes.ServiceState, handler.SourceType);
        Assert.Empty(handler.Validate(Service()));
        Assert.Empty(handler.Validate(Service(options: ["running", "stopped", "paused", "not_installed"])));
        Assert.Empty(handler.Validate(Service(options: [.. ServiceStateNames.All])));

        Assert.Equal(["/valueType"], Paths(handler.Validate(Service(valueType: "string"))));
        Assert.Equal(["/options/1"], Paths(handler.Validate(Service(options: ["running", "sleeping"]))));
        Assert.Equal(["/source/service"], Paths(handler.Validate(Service(service: "C:\\Windows\\svc.exe"))));
        Assert.Equal(["/source/service"], Paths(handler.Validate(Service(service: new string('s', 257)))));
        Assert.Empty(handler.Validate(Service(service: "")));
        Assert.Empty(handler.Validate(Counter()));
    }

    [Fact]
    public async Task A_service_state_sensor_reports_the_state_when_it_is_one_of_the_options()
    {
        var reader = new FakeServiceReader();
        reader.Services["EventLog"] = new ServiceReading(ServiceStateNames.Running, null);
        reader.Services["Spooler"] = new ServiceReading(ServiceStateNames.Stopped, null);
        var handler = new ServiceStateSensorHandler(reader);

        SensorParseResult running = await handler.SampleAsync(Service("EventLog"), CancellationToken.None);
        SensorParseResult stopped = await handler.SampleAsync(Service("Spooler"), CancellationToken.None);

        Assert.Equal("running", running.Value);
        Assert.Equal("stopped", stopped.Value);
    }

    [Fact]
    public async Task A_state_that_is_not_an_option_makes_the_sensor_unavailable_instead_of_reporting_a_value_that_home_assistant_would_refuse()
    {
        var reader = new FakeServiceReader();
        reader.Services["Spooler"] = new ServiceReading(ServiceStateNames.StartPending, null);
        var handler = new ServiceStateSensorHandler(reader);

        SensorParseResult result = await handler.SampleAsync(Service("Spooler"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("start_pending", result.Failure, StringComparison.Ordinal);
        Assert.Contains("running, stopped", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_is_not_installed_is_a_state_only_when_the_sensor_lists_it()
    {
        var handler = new ServiceStateSensorHandler(new FakeServiceReader());

        SensorParseResult without = await handler.SampleAsync(Service("NoSuchService"), CancellationToken.None);
        SensorParseResult with = await handler.SampleAsync(Service("NoSuchService", options: ["running", "not_installed"]), CancellationToken.None);

        Assert.False(without.IsValid);
        Assert.Contains("not installed", without.Failure, StringComparison.Ordinal);
        Assert.Equal("not_installed", with.Value);
    }

    [Fact]
    public async Task A_service_that_cannot_be_asked_is_unavailable_with_the_reason()
    {
        var reader = new FakeServiceReader();
        reader.Services["Locked"] = new ServiceReading(null, "The state of the service 'Locked' cannot be read: Access is denied.");
        var handler = new ServiceStateSensorHandler(reader);

        SensorParseResult result = await handler.SampleAsync(Service("Locked"), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("Access is denied", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void The_states_of_the_service_control_manager_have_snake_case_names()
    {
        Assert.Equal("stopped", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.Stopped));
        Assert.Equal("start_pending", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.StartPending));
        Assert.Equal("stop_pending", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.StopPending));
        Assert.Equal("running", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.Running));
        Assert.Equal("continue_pending", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.ContinuePending));
        Assert.Equal("pause_pending", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.PausePending));
        Assert.Equal("paused", ServiceControllerStateReader.Name(System.ServiceProcess.ServiceControllerStatus.Paused));
    }

    // ------------------------------------------------------------------
    // Together with the custom sensor provider of the agent
    // ------------------------------------------------------------------

    private static async Task<(CustomSensorProvider Provider, ConfigStore Store, TempDirectory Temp)> CreateProviderAsync(IReadOnlyList<ISensorSourceHandler> handlers)
    {
        var temp = new TempDirectory();
        var time = new ManualTime();
        var store = new ConfigStore(
            new ConfigStoreOptions
            {
                ConfigDirectory = Path.Combine(temp.Path, "config"),
                ScriptsDirectory = Path.Combine(temp.Path, "scripts"),
                EnableWatcher = false,
            },
            time);
        await store.LoadAsync(CancellationToken.None);
        var runner = new PowerShellRunner(new FakeEngineLocator(), new PowerShellOptions { ScriptsDirectory = Path.Combine(temp.Path, "scripts") }, time);
        var provider = new CustomSensorProvider(store, runner, new RecordingLauncher(), time, handlers: handlers);
        return (provider, store, temp);
    }

    [Fact]
    public async Task The_sensor_provider_samples_the_three_types_through_their_handlers_and_a_dry_run_gives_the_same_value()
    {
        var counters = new FakeCounterReader();
        var cim = new FakeCimRunner();
        var services = new FakeServiceReader();
        services.Services["EventLog"] = new ServiceReading(ServiceStateNames.Running, null);
        (CustomSensorProvider provider, ConfigStore store, TempDirectory temp) = await CreateProviderAsync(
            [new PerfCounterSensorHandler(counters), new CimQuerySensorHandler(cim), new ServiceStateSensorHandler(services)]);
        using (store)
        using (temp)
        {
            SensorTestOutcome counter = await provider.TestAsync(Counter(), CancellationToken.None);
            SensorTestOutcome query = await provider.TestAsync(Cim(), CancellationToken.None);
            SensorTestOutcome service = await provider.TestAsync(Service(), CancellationToken.None);

            Assert.Equal((true, (object?)37.5), (counter.Succeeded, counter.Value));
            Assert.Equal((true, (object?)"Microsoft Windows 11 Pro"), (query.Succeeded, query.Value));
            Assert.Equal((true, (object?)"running"), (service.Succeeded, service.Value));
            Assert.True(provider.TypeSupport.IsSupported(SensorSourceTypes.PerfCounter));
            Assert.True(provider.TypeSupport.IsSupported(SensorSourceTypes.CimQuery));
            Assert.True(provider.TypeSupport.IsSupported(SensorSourceTypes.ServiceState));
            Assert.Equal(["/valueType"], Paths(provider.TypeSupport.Validate(Counter(valueType: "string"))));
        }
    }

    [Fact]
    public async Task Without_the_windows_handlers_the_three_types_are_reported_as_unsupported_by_validation()
    {
        (CustomSensorProvider provider, ConfigStore store, TempDirectory temp) = await CreateProviderAsync([]);
        using (store)
        using (temp)
        {
            foreach (SensorDocument sensor in new[] { Counter(), Cim(), Service() })
            {
                ValidationError error = Assert.Single(provider.TypeSupport.Validate(sensor));
                Assert.Equal(("/source/type", ValidationCodes.Unsupported), (error.Path, error.Code));
            }

            SensorTestOutcome outcome = await provider.TestAsync(Counter(), CancellationToken.None);
            Assert.False(outcome.Succeeded);
        }
    }

    // ------------------------------------------------------------------
    // The real Windows: a counter, a class and a service that every installation has
    // ------------------------------------------------------------------

    [WindowsFact]
    public async Task The_real_processor_counter_returns_a_percentage_and_the_memory_counter_a_positive_number()
    {
        using var reader = new PerformanceCounterReader();
        var handler = new PerfCounterSensorHandler(reader);

        SensorParseResult processor = await handler.SampleAsync(Counter(), CancellationToken.None);
        SensorParseResult memory = await handler.SampleAsync(Counter("Memory", "Available MBytes", instance: null), CancellationToken.None);

        Assert.True(processor.IsValid, processor.Failure);
        Assert.InRange(Assert.IsType<double>(processor.Value), 0, 100);
        Assert.True(memory.IsValid, memory.Failure);
        Assert.True(Assert.IsType<double>(memory.Value) > 0);
    }

    [WindowsFact]
    public async Task A_counter_that_does_not_exist_is_unavailable_and_says_so()
    {
        using var reader = new PerformanceCounterReader();
        var handler = new PerfCounterSensorHandler(reader);

        SensorParseResult category = await handler.SampleAsync(Counter("NoSuchCategoryQoffeeAutomat", "Anything", null), CancellationToken.None);
        SensorParseResult instance = await handler.SampleAsync(Counter("Processor", "% Processor Time", "no-such-instance"), CancellationToken.None);

        Assert.False(category.IsValid);
        Assert.Contains("NoSuchCategoryQoffeeAutomat", category.Failure, StringComparison.Ordinal);
        Assert.False(instance.IsValid);
        Assert.Contains("no-such-instance", instance.Failure, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task The_real_wmi_returns_the_operating_system_caption_the_memory_size_and_the_last_boot_time()
    {
        var handler = new CimQuerySensorHandler(new WmiCimQueryRunner());

        SensorParseResult caption = await handler.SampleAsync(Cim(), CancellationToken.None);
        SensorParseResult memory = await handler.SampleAsync(Cim("number", property: "TotalVisibleMemorySize"), CancellationToken.None);
        SensorParseResult boot = await handler.SampleAsync(Cim("timestamp", property: "LastBootUpTime"), CancellationToken.None);
        SensorParseResult systemDisk = await handler.SampleAsync(Cim("number", className: "Win32_LogicalDisk", property: "DriveType", filter: "DeviceID='" + Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\') + "'"), CancellationToken.None);

        Assert.True(caption.IsValid, caption.Failure);
        Assert.Contains("Windows", Assert.IsType<string>(caption.Value), StringComparison.Ordinal);
        Assert.True(memory.IsValid, memory.Failure);
        Assert.True(Assert.IsType<double>(memory.Value) > 0);
        Assert.True(boot.IsValid, boot.Failure);
        DateTimeOffset moment = Assert.IsType<DateTimeOffset>(boot.Value);
        Assert.True(moment < DateTimeOffset.UtcNow && moment > DateTimeOffset.UtcNow.AddYears(-20));
        Assert.True(systemDisk.IsValid, systemDisk.Failure);
        Assert.Equal(3.0, systemDisk.Value);
    }

    [WindowsFact]
    public async Task A_class_or_property_that_does_not_exist_is_unavailable_and_says_so()
    {
        var handler = new CimQuerySensorHandler(new WmiCimQueryRunner());

        SensorParseResult unknownClass = await handler.SampleAsync(Cim(className: "Win32_NoSuchClassQoffeeAutomat"), CancellationToken.None);
        SensorParseResult unknownProperty = await handler.SampleAsync(Cim(property: "NoSuchPropertyQoffeeAutomat"), CancellationToken.None);
        SensorParseResult noMatch = await handler.SampleAsync(Cim(className: "Win32_LogicalDisk", property: "DeviceID", filter: "DeviceID='Q:\\\\no-such'"), CancellationToken.None);

        Assert.False(unknownClass.IsValid);
        Assert.False(unknownProperty.IsValid);
        Assert.False(noMatch.IsValid);
        Assert.Contains("CIM query", unknownClass.Failure, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task The_real_service_control_manager_reports_the_event_log_as_running_and_an_unknown_service_as_not_installed()
    {
        var handler = new ServiceStateSensorHandler(new ServiceControllerStateReader());

        SensorParseResult eventLog = await handler.SampleAsync(Service("EventLog", options: ["running", "stopped", "not_installed"]), CancellationToken.None);
        SensorParseResult unknown = await handler.SampleAsync(Service("QoffeeAutomatNoSuchService", options: ["running", "stopped", "not_installed"]), CancellationToken.None);
        SensorParseResult unknownWithoutOption = await handler.SampleAsync(Service("QoffeeAutomatNoSuchService"), CancellationToken.None);

        Assert.Equal("running", eventLog.Value);
        Assert.Equal("not_installed", unknown.Value);
        Assert.False(unknownWithoutOption.IsValid);
    }

    [WindowsFact]
    public async Task A_dry_run_of_each_type_works_through_the_sensor_provider_with_the_real_readers()
    {
        using var counters = new PerformanceCounterReader();
        (CustomSensorProvider provider, ConfigStore store, TempDirectory temp) = await CreateProviderAsync(
            [
                new PerfCounterSensorHandler(counters),
                new CimQuerySensorHandler(new WmiCimQueryRunner()),
                new ServiceStateSensorHandler(new ServiceControllerStateReader()),
            ]);
        using (store)
        using (temp)
        {
            SensorTestOutcome counter = await provider.TestAsync(Counter(), CancellationToken.None);
            SensorTestOutcome query = await provider.TestAsync(Cim(), CancellationToken.None);
            SensorTestOutcome service = await provider.TestAsync(Service("EventLog"), CancellationToken.None);

            Assert.True(counter.Succeeded, counter.Error);
            Assert.True(query.Succeeded, query.Error);
            Assert.True(service.Succeeded, service.Error);
            Assert.Equal("running", service.Value);
        }
    }
}
