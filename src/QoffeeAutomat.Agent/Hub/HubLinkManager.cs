using Microsoft.Extensions.Logging;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Core.Config;
using QoffeeAutomat.Core.Config.HubLayer;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>Why <see cref="HubLinkManager.JoinAsync"/> did not join.</summary>
public enum HubJoinFailure
{
    /// <summary>It did join.</summary>
    None,

    /// <summary>The enrollment string is not valid.</summary>
    Invalid,

    /// <summary>The agent is enrolled with a hub already; it has to leave first.</summary>
    AlreadyJoined,

    /// <summary>The hub refused the enrollment for good (unknown, expired or used-up token, rejected agent).</summary>
    Refused,

    /// <summary>The hub could not be reached or did not answer as expected. Trying again may help.</summary>
    Unreachable,
}

/// <summary>Why <see cref="HubLinkManager.JoinAsync"/> did or did not join.</summary>
/// <param name="Joined">True when the agent is enrolled or waits for approval (<see cref="Pending"/>).</param>
/// <param name="Pending">True when the hub requires approval: the agent stays pending until an administrator accepts it.</param>
/// <param name="Error">What is wrong with the enrollment string, or why the hub refused; null when joined. Never contains the token.</param>
/// <param name="Failure">Why it did not join; <see cref="HubJoinFailure.None"/> when it did.</param>
public sealed record HubJoinResult(bool Joined, bool Pending, string? Error, HubJoinFailure Failure = HubJoinFailure.None);

/// <summary>What the pending poll decided.</summary>
/// <param name="KeepPolling">True when the enrollment is still pending and has to be repeated.</param>
/// <param name="Delay">The time to wait before the next attempt.</param>
public sealed record HubPollResult(bool KeepPolling, TimeSpan Delay);

/// <summary>
/// Joins, leaves and reads the hub (PLAN.md S20, T905), the way <see cref="ReverseLinkManager"/> does for the reverse link. The hub state (address, pinned
/// fingerprint, public key, credential) lives in the secret store (<see cref="HubStateStore"/>); <see cref="ResolveAsync"/> turns it into the endpoint that
/// the connector dials, and <see cref="Changed"/> tells the service to dial (or stop) now. Joining calls the enrollment endpoint of the hub at once, so the
/// person who joins learns immediately whether the token was accepted.
/// </summary>
public sealed class HubLinkManager
{
    /// <summary>What <see cref="LeaveAsync"/> accepts for <c>documents</c>: the hub documents become local documents.</summary>
    public const string DocumentsConvert = "convert";

    /// <summary>What <see cref="LeaveAsync"/> accepts for <c>documents</c>: the hub documents are removed (the default).</summary>
    public const string DocumentsRemove = "remove";

    private readonly HubStateStore _store;
    private readonly HubEnrollmentClient _enroller;
    private readonly AgentIdentity _identity;
    private readonly ILogger _logger;
    private readonly IConfigStore? _config;
    private volatile bool _tunnelEnabled = true;
    private volatile string? _detail;

    /// <summary>Creates the manager.</summary>
    /// <param name="store">Where the state is kept.</param>
    /// <param name="enroller">Calls the enrollment endpoint.</param>
    /// <param name="identity">The identity that the agent presents.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The configuration store whose hub layer is removed when the agent leaves, is removed by the hub or is revoked (T908); null keeps no hub layer.</param>
    public HubLinkManager(
        HubStateStore store,
        HubEnrollmentClient enroller,
        AgentIdentity identity,
        ILogger<HubLinkManager> logger,
        IConfigStore? config = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(enroller);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _enroller = enroller;
        _identity = identity;
        _logger = logger;
        _config = config;
    }

    /// <summary>Raised after the agent joined or left, the credential was deleted, or the tunnel switch changed: the link has to dial (or stop) now.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether the management tunnel is allowed (the local switch); on unless it was switched off. Read when <c>hello</c> is built.</summary>
    public bool ManagementTunnelEnabled => _tunnelEnabled;

    /// <summary>Why the agent is not joined or not enrolled any more, for the status: for example that the hub rejected the agent. Null when there is nothing to say.</summary>
    public string? Detail => _detail;

    /// <summary>Reads the local switch of the tunnel; called once when the service starts.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the switch is read.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        _tunnelEnabled = await _store.IsTunnelEnabledAsync(cancellationToken).ConfigureAwait(false);

        // A hub layer without an enrollment is a leftover (the agent stopped between deleting the credential and removing the layer): without it the
        // agent would stay locked out of its own configuration by a hub that it has left.
        if (_config is { Hub.State.IsActive: true })
        {
            HubStoredState? stored = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (stored is not { Phase: HubPhases.Enrolled })
            {
                await RemoveLayerAsync(HubLeaveMode.Remove, "no enrollment", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Joins a hub: reads the enrollment string, calls <c>POST /hub/v1/enroll</c> and stores what comes back. With a token that needs approval the agent stays
    /// <c>pending</c> and the service repeats the call until an administrator decides. An agent that is enrolled already has to leave first; a pending or
    /// revoked agent may join again.
    /// </summary>
    /// <param name="enrollment">The enrollment string that the hub shows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The result; it has an error message when the string is not valid or the hub refused.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<HubJoinResult> JoinAsync(string? enrollment, CancellationToken cancellationToken)
    {
        if (!HubEnrollment.TryParse(enrollment, out HubEnrollment? parsed, out string? error))
        {
            return new HubJoinResult(false, false, error, HubJoinFailure.Invalid);
        }

        HubStoredState? existing = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is { Phase: HubPhases.Enrolled })
        {
            return AlreadyJoined();
        }

        HubEnrollOutcome outcome = await _enroller.EnrollAsync(parsed!.Url, parsed.Fingerprint, parsed.Token, _identity.Current(), cancellationToken).ConfigureAwait(false);
        if (outcome.Hub is null || outcome.Result is HubEnrollResult.Rejected or HubEnrollResult.Failed)
        {
            return new HubJoinResult(
                false,
                false,
                outcome.Message,
                outcome.Result == HubEnrollResult.Rejected ? HubJoinFailure.Refused : HubJoinFailure.Unreachable);
        }

        bool pending = outcome.Result == HubEnrollResult.Pending;
        var state = new HubStoredState(
            pending ? HubPhases.Pending : HubPhases.Enrolled,
            parsed.Url.AbsoluteUri.TrimEnd('/'),
            parsed.PublicKey,
            parsed.Fingerprint,
            outcome.Hub.Id,
            outcome.Hub.Name,
            pending ? null : outcome.Credential,
            pending ? parsed.Token : null,
            pending ? outcome.RetryAfterSeconds : 0);

        // A join that raced with another one that already enrolled the agent does not replace it.
        HubStoredState? stored = await _store.UpdateAsync(
            current => current is { Phase: HubPhases.Enrolled } ? null : state,
            cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(stored, state))
        {
            return AlreadyJoined();
        }

        _detail = null;
        if (pending)
        {
            HubLog.Pending(_logger, state.Url);
        }
        else
        {
            HubLog.Joined(_logger, state.Url, state.HubId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return new HubJoinResult(true, pending, null);
    }

    /// <summary>
    /// Leaves the hub: deletes the credential, the token and everything else the agent stored about the hub. The hub learns of it only because the agent no
    /// longer connects; an administrator removes the agent there. The hub layer (D-028) is removed and the local configuration is active again;
    /// <paramref name="documents"/> decides whether the documents that the hub distributed become local documents or are removed.
    /// </summary>
    /// <param name="documents"><see cref="DocumentsConvert"/> or <see cref="DocumentsRemove"/>; null means remove.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was something to leave; false when the agent had not joined a hub.</returns>
    /// <exception cref="ArgumentException"><paramref name="documents"/> is neither of the two values.</exception>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> LeaveAsync(string? documents, CancellationToken cancellationToken)
    {
        if (documents is not (null or DocumentsConvert or DocumentsRemove))
        {
            throw new ArgumentException("documents must be '" + DocumentsConvert + "' or '" + DocumentsRemove + "'.", nameof(documents));
        }

        // Leaving also ends the policy of the hub: the layer goes first, so that the documents can still become local ones.
        await RemoveLayerAsync(documents == DocumentsConvert ? HubLeaveMode.Convert : HubLeaveMode.Remove, "left", cancellationToken).ConfigureAwait(false);
        bool had = await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _detail = null;
        HubLog.Left(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
        return had;
    }

    /// <summary>
    /// Deletes the hub layer (PLAN.md S20): the local layer becomes active again and the policy of the hub ends. A failure is logged and does not stop
    /// the agent from leaving; the next start finds the layer without an enrollment and removes it then.
    /// </summary>
    private async Task RemoveLayerAsync(HubLeaveMode mode, string reason, CancellationToken cancellationToken)
    {
        if (_config is null)
        {
            return;
        }

        try
        {
            HubRemoveResult removed = await _config.Hub.RemoveAsync(mode, _config.Local, cancellationToken).ConfigureAwait(false);
            if (removed.Removed)
            {
                HubLog.ConfigLayerRemoved(_logger, reason, removed.Converted);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            HubLog.ConfigLayerRemovalFailed(_logger, exception.GetType().Name);
        }
    }

    private static HubJoinResult AlreadyJoined()
    {
        return new HubJoinResult(false, false, "This agent has joined a hub already. Leave it first.", HubJoinFailure.AlreadyJoined);
    }

    /// <summary>Reads the stored state.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state, or null when the agent has not joined a hub.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public Task<HubStoredState?> GetStateAsync(CancellationToken cancellationToken) => _store.LoadAsync(cancellationToken);

    /// <summary>Switches the management tunnel on or off locally. The next connection announces the feature, or not; an open connection is renewed at once.</summary>
    /// <param name="enabled">True to allow the tunnel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the switch is stored.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task SetManagementTunnelAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (_tunnelEnabled == enabled)
        {
            return;
        }

        await _store.SetTunnelEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        _tunnelEnabled = enabled;
        HubLog.TunnelSwitched(_logger, enabled ? "on" : "off");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The endpoint to dial: the address and fingerprint of the stored state with the stored credential.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint, or null when the agent is not enrolled (not joined, pending, revoked) or the stored address is unusable (a warning is logged for the last case).</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async ValueTask<OutboundEndpoint?> ResolveAsync(CancellationToken cancellationToken)
    {
        HubStoredState? state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is not { Phase: HubPhases.Enrolled, Credential: { Length: > 0 } credential })
        {
            return null;
        }

        if (!Uri.TryCreate(state.Url, UriKind.Absolute, out Uri? url) || HubUrls.CheckBaseUrl(url) is not null)
        {
            HubLog.InvalidStoredEndpoint(_logger, "the address is not a valid hub address.");
            return null;
        }

        string? fingerprint = null;
        if (state.Fingerprint is not null)
        {
            if (!OutboundLinkRules.TryNormalizeFingerprint(state.Fingerprint, out string normalized))
            {
                HubLog.InvalidStoredEndpoint(_logger, "the pinned fingerprint is not a SHA-256 fingerprint.");
                return null;
            }

            fingerprint = normalized;
        }

        // No compression: credential_rotate carries a credential, and S07 keeps credentials out of compressed frames.
        return new OutboundEndpoint(HubUrls.Combine(url, HubProtocol.AgentLinkPath, webSocket: true), credential, fingerprint, AllowCompression: false);
    }

    /// <summary>
    /// Stores the credential that the hub sent with <c>credential_rotate</c>. The old one stays valid at the hub until the agent has answered, so the caller
    /// answers only after this method returned true. The connection stays open: the new credential is used the next time the agent dials.
    /// </summary>
    /// <param name="credential">The new credential.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the credential is stored; false when it is not valid or the agent is not enrolled.</returns>
    public async Task<bool> ReplaceCredentialAsync(string credential, CancellationToken cancellationToken)
    {
        if (!HubCredentialRules.IsValid(credential))
        {
            return false;
        }

        try
        {
            HubStoredState? stored = await _store.UpdateAsync(
                current => current is { Phase: HubPhases.Enrolled } ? current with { Credential = credential } : null,
                cancellationToken).ConfigureAwait(false);
            bool replaced = string.Equals(stored?.Credential, credential, StringComparison.Ordinal);
            if (replaced)
            {
                HubLog.CredentialRotated(_logger);
            }

            return replaced;
        }
        catch (SecretStoreException exception)
        {
            HubLog.RotationFailed(_logger, exception.GetType().Name);
            return false;
        }
    }

    /// <summary>The hub answered the agent link with 401 and the code <c>revoked</c>: deletes the credential and remembers the state, so that the agent stops dialling.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the credential is gone.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task MarkRevokedAsync(CancellationToken cancellationToken)
    {
        HubStoredState? stored = await _store.UpdateAsync(
            current => current is { Phase: HubPhases.Enrolled } ? current with { Phase = HubPhases.Revoked, Credential = null, Token = null } : null,
            cancellationToken).ConfigureAwait(false);
        if (stored is { Phase: HubPhases.Revoked })
        {
            _detail = "The hub revoked the credential of this agent. Join the hub again with a new enrollment string.";
            HubLog.Revoked(_logger);
            await RemoveLayerAsync(HubLeaveMode.Remove, "revoked", cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The hub sent <c>unenroll</c>: deletes the credential and the hub state; the agent is not joined any more.</summary>
    /// <param name="reason">The reason that the hub gave, already shortened and free of control characters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the state is gone.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task UnenrollAsync(string reason, CancellationToken cancellationToken)
    {
        _ = await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _detail = "The hub removed this agent.";
        HubLog.Unenrolled(_logger, reason);
        await RemoveLayerAsync(HubLeaveMode.Remove, "unenrolled", cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Repeats the enrollment of a pending agent once. An approved agent becomes enrolled, one that an administrator rejected (or whose token is no longer
    /// valid) is removed, and a hub that cannot be reached leaves the state as it is.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whether to keep polling and how long to wait.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<HubPollResult> PollPendingAsync(CancellationToken cancellationToken)
    {
        HubStoredState? state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state is not { Phase: HubPhases.Pending, Token: { Length: > 0 } token } || !Uri.TryCreate(state.Url, UriKind.Absolute, out Uri? url))
        {
            return new HubPollResult(false, TimeSpan.Zero);
        }

        HubEnrollOutcome outcome = await _enroller.EnrollAsync(url, state.Fingerprint, token, _identity.Current(), cancellationToken).ConfigureAwait(false);
        switch (outcome.Result)
        {
            case HubEnrollResult.Enrolled:
                HubStoredState? enrolled = await _store.UpdateAsync(
                    current => current is { Phase: HubPhases.Pending } && string.Equals(current.Token, token, StringComparison.Ordinal)
                        ? current with { Phase = HubPhases.Enrolled, Credential = outcome.Credential, Token = null, RetryAfterSeconds = 0 }
                        : null,
                    cancellationToken).ConfigureAwait(false);
                if (enrolled is { Phase: HubPhases.Enrolled })
                {
                    _detail = null;
                    HubLog.Approved(_logger, state.Url);
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                return new HubPollResult(false, TimeSpan.Zero);

            case HubEnrollResult.Pending:
                _detail = outcome.Message;
                return new HubPollResult(true, TimeSpan.FromSeconds(Math.Max(1, outcome.RetryAfterSeconds)));

            case HubEnrollResult.Rejected:
                // The decision is final: forget the pending enrollment so that the agent does not ask again with a token that is dead.
                bool removed = await _store.DeleteIfAsync(
                    current => current is { Phase: HubPhases.Pending } && string.Equals(current.Token, token, StringComparison.Ordinal),
                    cancellationToken).ConfigureAwait(false);
                if (removed)
                {
                    _detail = outcome.Message;
                    HubLog.EnrollmentRefused(_logger, state.Url, outcome.Message);
                    Changed?.Invoke(this, EventArgs.Empty);
                }

                return new HubPollResult(false, TimeSpan.Zero);

            default:
                _detail = outcome.Message;
                TimeSpan wait = TimeSpan.FromSeconds(Math.Max(Math.Max(1, state.RetryAfterSeconds), outcome.RetryAfterSeconds));
                HubLog.EnrollmentRetry(_logger, state.Url, outcome.Message, wait.TotalSeconds);
                return new HubPollResult(true, wait);
        }
    }
}
