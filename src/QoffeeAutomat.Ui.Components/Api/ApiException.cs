using System.Globalization;
using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Api;

/// <summary>
/// An error answer of the management API (RFC 9457 problem details with a machine-readable code, PLAN.md S08) or a
/// failure to reach the agent at all. Pages show it through <c>ErrorDisplay</c>, so every error looks the same.
/// </summary>
public sealed class ApiException : Exception
{
    /// <summary>The code of <see cref="Code"/> when the agent could not be reached.</summary>
    public const string NetworkErrorCode = "network_error";

    /// <summary>The code of a 401 answer.</summary>
    public const string UnauthorizedCode = "unauthorized";

    /// <summary>Creates an exception without details.</summary>
    public ApiException()
        : this(0, null)
    {
    }

    /// <summary>Creates an exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public ApiException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with a message and the exception that caused it.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public ApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for an error answer.</summary>
    /// <param name="statusCode">The HTTP status code; zero when no answer arrived.</param>
    /// <param name="problem">The problem details of the answer, when it had any.</param>
    /// <param name="retryAfterSeconds">The value of the <c>Retry-After</c> header, when present.</param>
    /// <param name="innerException">The transport exception, when no answer arrived.</param>
    public ApiException(int statusCode, ApiProblem? problem, int? retryAfterSeconds = null, Exception? innerException = null)
        : base(problem?.Detail ?? problem?.Title ?? "The agent answered with status " + statusCode.ToString(CultureInfo.InvariantCulture) + ".", innerException)
    {
        StatusCode = statusCode;
        Problem = problem;
        RetryAfterSeconds = retryAfterSeconds;
    }

    /// <summary>The HTTP status code; zero when the agent could not be reached.</summary>
    public int StatusCode { get; }

    /// <summary>The problem details of the answer, when it had any.</summary>
    public ApiProblem? Problem { get; }

    /// <summary>How long to wait before trying again, from the <c>Retry-After</c> header.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>The machine-readable code: the <c>code</c> of the problem, or one derived from the status.</summary>
    public string Code => Problem?.Code
        ?? (StatusCode switch
        {
            0 => NetworkErrorCode,
            401 => UnauthorizedCode,
            _ => "http_" + StatusCode.ToString(CultureInfo.InvariantCulture),
        });

    /// <summary>True when the agent answered 401: there is no valid session.</summary>
    public bool IsUnauthorized => StatusCode == 401;

    /// <summary>True when no answer arrived.</summary>
    public bool IsNetworkError => StatusCode == 0;

    /// <summary>The validation errors of a 422 answer, with JSON pointers into the request body; empty otherwise.</summary>
    public IReadOnlyList<ApiValidationError> ValidationErrors => Problem?.Errors ?? [];
}
