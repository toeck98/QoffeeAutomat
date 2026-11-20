using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>The phases of the stored hub state.</summary>
public static class HubPhases
{
    /// <summary>The token needs approval; the agent repeats the enrollment until an administrator of the hub decides.</summary>
    public const string Pending = "pending";

    /// <summary>The agent holds a credential and dials the hub.</summary>
    public const string Enrolled = "enrolled";

    /// <summary>The hub revoked the credential; it is deleted. Kept so that the status can say so until the agent joins again.</summary>
    public const string Revoked = "revoked";
}

/// <summary>
/// Everything the agent keeps about its hub (PLAN.md S20): the address, the pinned certificate fingerprint, the public key that verifies desired
/// states, the identity of the hub and, depending on the phase, the credential or the token of a pending enrollment. It is stored as one secret, so
/// it is protected like the other credentials of the agent (S09 item 6).
/// </summary>
/// <param name="Phase">One of <see cref="HubPhases"/>.</param>
/// <param name="Url">The base address of the hub.</param>
/// <param name="PublicKey">The signing key of the hub (base64 DER SubjectPublicKeyInfo).</param>
/// <param name="Fingerprint">The pinned SHA-256 fingerprint of the hub certificate; absent means the normal certificate validation.</param>
/// <param name="HubId">The id of the hub.</param>
/// <param name="HubName">The name of the hub.</param>
/// <param name="Credential">The agent credential; present in the phase <see cref="HubPhases.Enrolled"/> only.</param>
/// <param name="Token">The enrollment token; present in the phase <see cref="HubPhases.Pending"/> only, so that the enrollment can be repeated.</param>
/// <param name="RetryAfterSeconds">The seconds between two enrollment attempts while pending.</param>
public sealed record HubStoredState(
    string Phase,
    string Url,
    string PublicKey,
    string? Fingerprint,
    string HubId,
    string HubName,
    string? Credential = null,
    string? Token = null,
    int RetryAfterSeconds = 0)
{
    /// <summary>Hides the credential and the token from the generated <c>ToString</c> so that they do not end up in logs.</summary>
    /// <returns>A text without secrets.</returns>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"HubStoredState {{ Phase = {Phase}, Url = {Url}, HubId = {HubId} }}");
    }
}

/// <summary>Source-generated serialization of <see cref="HubStoredState"/>. No reflection is used (PLAN.md S14).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HubStoredState))]
internal sealed partial class HubStoredStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes the hub state in the secret store (<c>hub.link</c>) and the local switch of the management tunnel (<c>hub.tunnel_off</c>, present
/// only while the tunnel is switched off, so that the default is on). Operations that read and then write are serialised.
/// </summary>
public sealed class HubStateStore : IDisposable
{
    /// <summary>The secret that holds <see cref="HubStoredState"/>.</summary>
    public const string StateSecret = "hub.link";

    /// <summary>The secret whose presence means: the management tunnel is switched off.</summary>
    public const string TunnelOffSecret = "hub.tunnel_off";

    private readonly ISecretStore _secrets;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the store.</summary>
    /// <param name="secrets">The secret store of the agent.</param>
    public HubStateStore(ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        _secrets = secrets;
    }

    /// <summary>Reads the state.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state, or null when the agent has not joined a hub (or the stored state is damaged, which is treated the same way).</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<HubStoredState?> LoadAsync(CancellationToken cancellationToken)
    {
        string? text = await _secrets.GetStringAsync(StateSecret, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(text, HubStoredStateJsonContext.Default.HubStoredState);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Replaces the state.</summary>
    /// <param name="state">The new state.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the state is stored.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task SaveAsync(HubStoredState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        string text = JsonSerializer.Serialize(state, HubStoredStateJsonContext.Default.HubStoredState);
        await _secrets.SetStringAsync(StateSecret, text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes the state, and with it the credential and the token.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when there was a state.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public Task<bool> DeleteAsync(CancellationToken cancellationToken)
    {
        return _secrets.RemoveAsync(StateSecret, cancellationToken);
    }

    /// <summary>Runs a change of the state that reads it first, without another change in between.</summary>
    /// <param name="change">Receives the current state (or null) and returns the state to store; null leaves it as it is.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The state after the change.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<HubStoredState?> UpdateAsync(Func<HubStoredState?, HubStoredState?> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HubStoredState? current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            HubStoredState? next = change(current);
            if (next is null || ReferenceEquals(next, current))
            {
                return current;
            }

            await SaveAsync(next, cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Deletes the state when a condition on the current state holds, without another change in between.</summary>
    /// <param name="condition">Receives the current state (or null); true deletes it.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the state was deleted.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> DeleteIfAsync(Func<HubStoredState?, bool> condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HubStoredState? current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return condition(current) && await DeleteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Whether the management tunnel is allowed (the local switch); on unless it was switched off.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>True when the tunnel is on.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task<bool> IsTunnelEnabledAsync(CancellationToken cancellationToken)
    {
        return await _secrets.GetStringAsync(TunnelOffSecret, cancellationToken).ConfigureAwait(false) is null;
    }

    /// <summary>Switches the management tunnel on or off.</summary>
    /// <param name="enabled">True to allow the tunnel.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the switch is stored.</returns>
    /// <exception cref="SecretStoreException">The secret store cannot be used.</exception>
    public async Task SetTunnelEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (enabled)
        {
            _ = await _secrets.RemoveAsync(TunnelOffSecret, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _secrets.SetStringAsync(TunnelOffSecret, "off", cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
