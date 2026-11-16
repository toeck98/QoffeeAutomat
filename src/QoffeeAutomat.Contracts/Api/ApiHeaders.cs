namespace QoffeeAutomat.Contracts.Api;

/// <summary>
/// HTTP header conventions of the management API that both the agent and its clients (the UI, tools) rely on
/// (PLAN.md S09 item 1).
/// </summary>
public static class ApiHeaders
{
    /// <summary>
    /// The anti-forgery header that every request which changes state must carry. A browser cannot add a custom header
    /// to a cross-site request without a CORS preflight, which the agent never answers.
    /// </summary>
    public const string CsrfName = "X-QoffeeAutomat-Csrf";

    /// <summary>The value of <see cref="CsrfName"/>.</summary>
    public const string CsrfValue = "1";
}
