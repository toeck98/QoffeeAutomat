using System.Text;
using System.Text.Json;
using QoffeeAutomat.Agent.Hub.ConfigSync;
using QoffeeAutomat.Agent.Hub.Tunnel;
using QoffeeAutomat.Agent.Link;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Agent.Hub;

/// <summary>
/// What the hub adds to the QoffeeLink session that the agent serves to it (PLAN.md S20, T905): the features in <c>hello</c> and the messages that every
/// agent link understands, <c>credential_rotate</c> and <c>unenroll</c>, the messages of the feature <c>config_sync</c> (T908), which
/// <see cref="ConfigSyncService"/> serves, and those of the feature <c>mgmt_tunnel</c> (T912), which <see cref="ManagementTunnelService"/> serves. Everything else the hub sends is the standard protocol, served by the link server exactly as for any other client.
/// </summary>
public sealed class HubLinkExtension : ILinkExtension
{
    private const int MaxReasonLength = 64;

    private readonly HubLinkManager _manager;
    private readonly ConfigSyncService _sync;
    private readonly ManagementTunnelService _tunnel;

    /// <summary>Creates the extension.</summary>
    /// <param name="manager">Stores the new credential and handles the removal.</param>
    /// <param name="sync">Handles the desired state of the hub.</param>
    /// <param name="tunnel">Serves the management tunnel.</param>
    public HubLinkExtension(HubLinkManager manager, ConfigSyncService sync, ManagementTunnelService tunnel)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(sync);
        ArgumentNullException.ThrowIfNull(tunnel);

        _manager = manager;
        _sync = sync;
        _tunnel = tunnel;
    }

    /// <summary>The features of this agent for the hub: <see cref="HubFeatures.ConfigSync"/> always, <see cref="HubFeatures.MgmtTunnel"/> while the local switch allows the tunnel.</summary>
    public IReadOnlyList<string> HelloFeatures => _manager.ManagementTunnelEnabled ? [HubFeatures.ConfigSync, HubFeatures.MgmtTunnel] : [HubFeatures.ConfigSync];

    /// <inheritdoc />
    public ValueTask OnEstablishedAsync(IReadOnlyList<string> peerFeatures, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        _sync.OnEstablished(peerFeatures, context);
        _tunnel.OnEstablished(peerFeatures, context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public void OnClosed(ILinkExtensionContext context)
    {
        _sync.OnClosed(context);
        _tunnel.OnClosed(context);
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        switch (message.Type)
        {
            case HubMessageTypes.ConfigDesired:
            case HubMessageTypes.ConfigDocuments:
                return await _sync.TryHandleAsync(message, context, cancellationToken).ConfigureAwait(false);
            case HubMessageTypes.CredentialRotate:
                await RotateAsync(message, context, cancellationToken).ConfigureAwait(false);
                return true;
            case HubMessageTypes.Unenroll:
                await UnenrollAsync(message, context, cancellationToken).ConfigureAwait(false);
                return true;
            case HubMessageTypes.MgmtRequest:
            case HubMessageTypes.MgmtStreamOpen:
            case HubMessageTypes.MgmtStreamClose:
                return await _tunnel.TryHandleAsync(message, context, cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task RotateAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        CredentialRotateMessage? rotate = null;
        try
        {
            rotate = message.Raw.Deserialize(HubJsonContext.Default.CredentialRotateMessage);
        }
        catch (JsonException)
        {
            // Answered below, if the frame has a usable id.
        }

        if (rotate is null)
        {
            if (TryReadId(message.Raw, out long id))
            {
                context.Send(ResultMessage.Failure(id, ErrorCodes.BadRequest, "The credential_rotate request is malformed."));
            }

            return;
        }

        // The old credential stays valid at the hub until this answer arrives, so the new one is stored first.
        bool stored = await _manager.ReplaceCredentialAsync(rotate.Credential, cancellationToken).ConfigureAwait(false);
        context.Send(stored
            ? ResultMessage.Success(rotate.Id, LinkValues.EmptyObject)
            : ResultMessage.Failure(rotate.Id, ErrorCodes.Internal, "The new credential could not be stored."));
    }

    private async Task UnenrollAsync(UnknownMessage message, ILinkExtensionContext context, CancellationToken cancellationToken)
    {
        string reason = "removed";
        try
        {
            if (message.Raw.Deserialize(HubJsonContext.Default.UnenrollMessage) is { } unenroll)
            {
                reason = Sanitize(unenroll.Reason);
            }
        }
        catch (JsonException)
        {
            // An unenroll without a reason still means that the hub removed this agent.
        }

        await _manager.UnenrollAsync(reason, cancellationToken).ConfigureAwait(false);
        context.Close(CloseCodes.Normal, "This agent was removed from the hub.");
    }

    private static bool TryReadId(JsonElement raw, out long id)
    {
        id = 0;
        return raw.ValueKind == JsonValueKind.Object
            && raw.TryGetProperty("id", out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out id)
            && id >= 0;
    }

    /// <summary>The reason is an open vocabulary of the hub; it ends up in the log, so it is shortened and stripped of control characters.</summary>
    private static string Sanitize(string? reason)
    {
        var text = new StringBuilder();
        foreach (char c in reason ?? string.Empty)
        {
            if (text.Length >= MaxReasonLength)
            {
                break;
            }

            if (!char.IsControl(c))
            {
                text.Append(c);
            }
        }

        return text.Length == 0 ? "removed" : text.ToString();
    }
}
