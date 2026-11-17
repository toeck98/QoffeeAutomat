using Microsoft.Extensions.Logging;

namespace QoffeeAutomat.Platform.Windows.Metrics.HardwareSensors;

/// <summary>Source-generated log messages of the hardware sensors (event ids 341 to 345).</summary>
internal static partial class HardwareLog
{
    [LoggerMessage(EventId = 341, Level = LogLevel.Information, Message = "Hardware sensors: turned on, loading the hardware library (driver {Driver}, installed: {Installed}).")]
    public static partial void Opening(ILogger logger, string driver, bool installed);

    [LoggerMessage(EventId = 342, Level = LogLevel.Information, Message = "Hardware sensors: {Count} sensors found.")]
    public static partial void Opened(ILogger logger, int count);

    [LoggerMessage(EventId = 343, Level = LogLevel.Warning, Message = "Hardware sensors: the kernel driver {Driver} is not installed; only the sensors that need no driver are read.")]
    public static partial void DriverMissing(ILogger logger, string driver);

    [LoggerMessage(EventId = 344, Level = LogLevel.Warning, Message = "Hardware sensors: could not be read: {Reason}")]
    public static partial void Failed(ILogger logger, string reason);

    [LoggerMessage(EventId = 345, Level = LogLevel.Information, Message = "Hardware sensors: turned off, the hardware library was closed and the driver handle released.")]
    public static partial void Closed(ILogger logger);
}
