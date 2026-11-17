namespace QoffeeAutomat.Ui.Components.Api;

/// <summary>The filters and the page of <c>GET /api/v1/runs</c>.</summary>
/// <param name="Limit">The page size, 1 to 200.</param>
/// <param name="Offset">How many of the newest matching runs to skip.</param>
/// <param name="Command">Only runs of this command.</param>
/// <param name="Source">Only runs from this source: <c>ha</c>, <c>ui</c>, <c>hub</c>, <c>cli</c>, <c>sequence</c> or <c>test</c>.</param>
/// <param name="FailedOnly">True lists only runs that did not succeed.</param>
public sealed record RunQuery(int Limit = 50, int Offset = 0, string? Command = null, string? Source = null, bool FailedOnly = false);

/// <summary>A file that the agent sent (export or diagnostics): its name, type and bytes.</summary>
/// <param name="FileName">The file name from <c>Content-Disposition</c>.</param>
/// <param name="ContentType">The media type.</param>
/// <param name="Content">The bytes.</param>
public sealed record DownloadedFile(string FileName, string ContentType, byte[] Content);

/// <summary>The kinds of document that have a revision history, as the URLs of the agent name them.</summary>
public static class RevisionKinds
{
    /// <summary>A command.</summary>
    public const string Command = "command";

    /// <summary>A custom sensor.</summary>
    public const string Sensor = "sensor";

    /// <summary>The agent settings (their id is <c>settings</c>).</summary>
    public const string Settings = "settings";
}
