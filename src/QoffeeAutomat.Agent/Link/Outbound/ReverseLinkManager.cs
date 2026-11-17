using Microsoft.Extensions.Logging;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Link.Outbound;

/// <summary>What the agent knows about its reverse link; the token itself is never part of it.</summary>
/// <param name="Enabled">Whether the agent dials Home Assistant.</param>
/// <param name="Url">The endpoint, or null when the link was never set up.</param>
/// <param name="Pinned">Whether a certificate fingerprint is pinned.</param>
/// <param name="HasToken">Whether a token is stored.</param>
public sealed record ReverseLinkConfiguration(bool Enabled, string? Url, bool Pinned, bool HasToken);

/// <summary>Why <see cref="ReverseLinkManager.JoinAsync"/> did not join.</summary>
/// <param name="Joined">True when the agent joined and will dial.</param>
/// <param name="Error">What is wrong with the enrollment string, or why the settings were not saved; null when joined.</param>
public sealed record ReverseLinkJoinResult(bool Joined, string? Error);

/// <summary>
/// Sets up, reads and removes the reverse link (ADR 0003, T710). The URL and the fingerprint live in the settings document, the
/// token in the secret store (<see cref="SecretNames.ReverseLinkToken"/>); <see cref="ResolveAsync"/> combines them into the
/// endpoint that the connector dials. <see cref="Changed"/> tells the service to dial now. The CLI verb and the UI form that
/// call <see cref="JoinAsync"/> and <see cref="LeaveAsync"/> come with the hub join of T905, which follows the same pattern.
/// </summary>
public sealed class ReverseLinkManager
{
    private readonly IConfigStore _config;
    private readonly ISecretStore _secrets;
    private readonly ILogger _logger;

    /// <summary>Creates the manager.</summary>
    /// <param name="config">The settings.</param>
    /// <param name="secrets">Where the token is kept.</param>
    /// <param name="logger">The logger.</param>
    public ReverseLinkManager(IConfigStore config, ISecretStore secrets, ILogger<ReverseLinkManager> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _secrets = secrets;
        _logger = logger;
    }

    /// <summary>Raised after the link was joined or left.</summary>
    public event EventHandler? Changed;

    /// <summary>Joins the reverse link: stores the token, saves the URL and the fingerprint and turns the link on.</summary>
    /// <param name="enrollment">The enrollment string that Home Assistant shows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result; it has an error message when the string is not valid or the settings could not be saved.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<ReverseLinkJoinResult> JoinAsync(string enrollment, CancellationToken cancellationToken)
    {
        if (!ReverseLinkEnrollment.TryParse(enrollment, out ReverseLinkEnrollment? parsed, out string? error))
        {
            return new ReverseLinkJoinResult(false, error);
        }

        ILocalConfigLayer local = _config.Local;
        AgentSettingsDocument? existing = local.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
        AgentSettingsDocument settings = existing ?? new AgentSettingsDocument(DocumentSchema.CurrentVersion, Environment.MachineName);
        settings = settings with
        {
            ReverseLink = new ReverseLinkSettings(Enabled: true, Url: parsed!.Url.AbsoluteUri, Fingerprint: parsed.Fingerprint),
        };

        // Validate settings before storing the token, and store the token before saving settings so the config
        // change pump never wakes up without the token or races with Changed.Invoke.
        QoffeeAutomat.Core.Validation.ValidationResult validation = QoffeeAutomat.Core.Validation.DocumentValidator.ValidateSettings(settings);
        if (!validation.IsValid)
        {
            return new ReverseLinkJoinResult(false, string.Join(' ', validation.Errors.Select(problem => problem.Message)));
        }

        await _secrets.SetStringAsync(SecretNames.ReverseLinkToken, parsed.Token, cancellationToken).ConfigureAwait(false);
        SaveResult<AgentSettingsDocument> saved = await local.SaveAsync(settings, existing?.Revision, cancellationToken).ConfigureAwait(false);
        if (!saved.IsApplied)
        {
            string reason = saved.Errors.Count > 0 ? string.Join(' ', saved.Errors.Select(problem => problem.Message)) : "The settings were changed meanwhile; try again.";
            return new ReverseLinkJoinResult(false, reason);
        }
        if (_logger.IsEnabled(LogLevel.Information))
        {
            string joinedPath = parsed.Url.GetLeftPart(UriPartial.Path);
            OutboundLog.Joined(_logger, joinedPath);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return new ReverseLinkJoinResult(true, null);
    }

    /// <summary>Leaves the reverse link: deletes the token and turns the link off. The URL stays in the settings so that the form can show it.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was something to leave.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> LeaveAsync(CancellationToken cancellationToken)
    {
        bool hadToken = await _secrets.RemoveAsync(SecretNames.ReverseLinkToken, cancellationToken).ConfigureAwait(false);

        ILocalConfigLayer local = _config.Local;
        AgentSettingsDocument? existing = local.GetDocument<AgentSettingsDocument>(ConfigIds.Settings);
        bool wasEnabled = existing?.ReverseLink?.Enabled == true;
        if (existing is { ReverseLink: { } current } && wasEnabled)
        {
            SaveResult<AgentSettingsDocument> saved = await local.SaveAsync(
                existing with { ReverseLink = current with { Enabled = false } },
                existing.Revision,
                cancellationToken).ConfigureAwait(false);
            if (!saved.IsApplied)
            {
                // The token is gone, so the link cannot dial anyway; the flag is corrected by the next change of the settings.
                OutboundLog.InvalidEndpoint(_logger, "The settings could not be updated after leaving the reverse link.");
            }
        }

        OutboundLog.Left(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
        return hadToken || wasEnabled;
    }

    /// <summary>Reads what is configured.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The configuration.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<ReverseLinkConfiguration> GetAsync(CancellationToken cancellationToken)
    {
        ReverseLinkSettings? settings = ReadSettings();
        bool hasToken = await _secrets.GetStringAsync(SecretNames.ReverseLinkToken, cancellationToken).ConfigureAwait(false) is not null;
        return new ReverseLinkConfiguration(settings?.Enabled == true, settings?.Url, settings?.Fingerprint is not null, hasToken);
    }

    /// <summary>The endpoint to dial: the URL and fingerprint of the settings with the stored token.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint, or null when the link is off, has no token or has an unusable URL (a warning is logged for the last case).</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async ValueTask<OutboundEndpoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        ReverseLinkSettings? settings = ReadSettings();
        if (settings is not { Enabled: true, Url: { Length: > 0 } url })
        {
            return null;
        }

        string? token = await _secrets.GetStringAsync(SecretNames.ReverseLinkToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        if (OutboundLinkRules.CheckUrl(url) is { } problem)
        {
            OutboundLog.InvalidEndpoint(_logger, problem);
            return null;
        }

        string? fingerprint = null;
        if (settings.Fingerprint is not null)
        {
            if (!OutboundLinkRules.TryNormalizeFingerprint(settings.Fingerprint, out string normalized))
            {
                OutboundLog.InvalidEndpoint(_logger, "The pinned fingerprint is not a SHA-256 fingerprint.");
                return null;
            }

            fingerprint = normalized;
        }

        return new OutboundEndpoint(new Uri(url, UriKind.Absolute), token, fingerprint);
    }

    private ReverseLinkSettings? ReadSettings()
    {
        return _config.Effective.GetDocument<AgentSettingsDocument>(ConfigIds.Settings)?.ReverseLink;
    }
}
