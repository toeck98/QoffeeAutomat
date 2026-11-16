using System.ComponentModel;
using System.ServiceProcess;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>The <see cref="IServiceStateReader"/> of the Windows service control manager (PLAN.md S06, T307).</summary>
public sealed class ServiceControllerStateReader : IServiceStateReader
{
    private const int ServiceDoesNotExist = 1060;

    /// <inheritdoc />
    public ServiceReading Read(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        try
        {
            using var controller = new ServiceController(serviceName);
            return new ServiceReading(Name(controller.Status), null);
        }
        catch (InvalidOperationException exception) when (exception.InnerException is Win32Exception { NativeErrorCode: ServiceDoesNotExist })
        {
            return ServiceReading.NotInstalled;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            return new ServiceReading(null, $"The state of the service '{serviceName}' cannot be read: {exception.Message}");
        }
    }

    /// <summary>Names a state of the service control manager.</summary>
    /// <param name="status">The state.</param>
    /// <returns>One of <see cref="ServiceStateNames"/>.</returns>
    public static string Name(ServiceControllerStatus status)
    {
        return status switch
        {
            ServiceControllerStatus.Stopped => ServiceStateNames.Stopped,
            ServiceControllerStatus.StartPending => ServiceStateNames.StartPending,
            ServiceControllerStatus.StopPending => ServiceStateNames.StopPending,
            ServiceControllerStatus.Running => ServiceStateNames.Running,
            ServiceControllerStatus.ContinuePending => ServiceStateNames.ContinuePending,
            ServiceControllerStatus.PausePending => ServiceStateNames.PausePending,
            ServiceControllerStatus.Paused => ServiceStateNames.Paused,
            _ => ServiceStateNames.Stopped,
        };
    }
}
