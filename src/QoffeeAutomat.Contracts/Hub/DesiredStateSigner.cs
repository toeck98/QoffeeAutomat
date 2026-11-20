using System.Security.Cryptography;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Hub;

/// <summary>
/// Signs and verifies the desired state of an agent with ECDSA P-256 and SHA-256 (PLAN.md S20, D-027). The signed
/// content is the canonical JSON (<see cref="CanonicalJson"/>) of <see cref="DesiredState"/>:
/// <c>{agent_id, generation, policy, items}</c>. Because the item list contains the hash of every document, the
/// documents are authenticated too. The signature is the 64 byte IEEE P1363 form (r followed by s) as standard base64.
/// Verification fails when the agent id, the generation, the policy or any item (kind, id, hash, order, count) differs.
/// </summary>
public static class DesiredStateSigner
{
    /// <summary>The canonical JSON bytes that are signed.</summary>
    /// <param name="state">The desired state.</param>
    /// <returns>The canonical UTF-8 JSON.</returns>
    public static byte[] CanonicalPayload(DesiredState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return CanonicalJson.SerializeToUtf8(state, HubJsonContext.Default.DesiredState);
    }

    /// <summary>Signs a desired state.</summary>
    /// <param name="state">The desired state.</param>
    /// <param name="signingKey">The hub's key pair.</param>
    /// <returns>The signature as standard base64 with padding.</returns>
    public static string Sign(DesiredState state, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(signingKey);

        byte[] signature = signingKey.SignData(
            CanonicalPayload(state),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(signature);
    }

    /// <summary>Verifies the signature of a desired state.</summary>
    /// <param name="state">The desired state, with the agent's own id.</param>
    /// <param name="signature">The signature as standard base64.</param>
    /// <param name="publicKey">The hub's public key pinned at enrollment.</param>
    /// <returns>True when the signature is valid; false for any problem, including a malformed signature.</returns>
    public static bool Verify(DesiredState state, string signature, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKey);

        Span<byte> buffer = stackalloc byte[HubProtocol.SignatureBytes];
        if (!Convert.TryFromBase64String(signature, buffer, out int written) || written != HubProtocol.SignatureBytes)
        {
            return false;
        }

        try
        {
            return publicKey.VerifyData(
                CanonicalPayload(state),
                buffer,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>The desired state that a <c>config_desired</c> frame stands for on one agent.</summary>
    /// <param name="agentId">The agent's own id.</param>
    /// <param name="message">The frame.</param>
    /// <returns>The state whose canonical JSON the signature covers.</returns>
    public static DesiredState StateOf(string agentId, ConfigDesiredMessage message)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(message);
        return new DesiredState(agentId, message.Generation, message.Policy, message.Items);
    }

    /// <summary>Creates a signed <c>config_desired</c> frame.</summary>
    /// <param name="requestId">The request id.</param>
    /// <param name="state">The desired state; its agent id is signed but not sent.</param>
    /// <param name="signingKey">The hub's key pair.</param>
    /// <returns>The frame.</returns>
    public static ConfigDesiredMessage CreateMessage(long requestId, DesiredState state, ECDsa signingKey)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new ConfigDesiredMessage(requestId, state.Generation, state.Policy, state.Items, Sign(state, signingKey));
    }

    /// <summary>Verifies the signature of a <c>config_desired</c> frame for one agent.</summary>
    /// <param name="agentId">The agent's own id.</param>
    /// <param name="message">The frame.</param>
    /// <param name="publicKey">The hub's public key pinned at enrollment.</param>
    /// <returns>True when the signature is valid for this agent.</returns>
    public static bool Verify(string agentId, ConfigDesiredMessage message, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Verify(StateOf(agentId, message), message.Sig, publicKey);
    }
}
