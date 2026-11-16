using System.Management;
using System.Runtime.InteropServices;

namespace QoffeeAutomat.Platform.Windows.Metrics.CustomSensors;

/// <summary>
/// The <see cref="ICimQueryRunner"/> of Windows (PLAN.md S06, T307): a WQL query <c>SELECT property FROM class [WHERE filter]</c>
/// through <c>System.Management</c>, of which only the first result is used. The names of the namespace, class and
/// property are checked by <see cref="CimQuerySensorHandler.Validate"/> before they get here, so they cannot change the shape of the query.
/// </summary>
public sealed class WmiCimQueryRunner : ICimQueryRunner
{
    /// <summary>How long a query may take inside WMI.</summary>
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(20);

    /// <inheritdoc />
    public Task<CimReading> QueryFirstAsync(
        string cimNamespace,
        string className,
        string propertyName,
        string? filter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cimNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        // WMI calls are synchronous and cannot be cancelled; the caller stops waiting, the call ends by itself (QueryTimeout).
        return Task.Run(() => Query(cimNamespace, className, propertyName, filter), cancellationToken);
    }

    private static CimReading Query(string cimNamespace, string className, string property, string? filter)
    {
        string text = string.IsNullOrWhiteSpace(filter)
            ? $"SELECT {property} FROM {className}"
            : $"SELECT {property} FROM {className} WHERE {filter}";
        try
        {
            var scope = new ManagementScope(cimNamespace.Replace('/', '\\'));
            var options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true };
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(text), options);
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    object? value = item[property];
                    return value is null
                        ? new CimReading(null, $"The property '{property}' of the first result is empty.")
                        : new CimReading(value, null);
                }
            }

            return new CimReading(null, "The query returned no result.");
        }
        catch (Exception exception) when (exception is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new CimReading(null, "The CIM query failed: " + exception.Message);
        }
    }
}
