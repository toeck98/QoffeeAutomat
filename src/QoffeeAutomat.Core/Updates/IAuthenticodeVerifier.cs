namespace QoffeeAutomat.Core.Updates;

/// <summary>The outcome of <see cref="IAuthenticodeVerifier.Verify"/>.</summary>
/// <param name="IsTrusted">True when the file carries an Authenticode signature that Windows trusts and that matches the file.</param>
/// <param name="Reason">Why the signature is not trusted; null when it is.</param>
public sealed record AuthenticodeResult(bool IsTrusted, string? Reason = null)
{
    /// <summary>A trusted signature.</summary>
    public static AuthenticodeResult Trusted { get; } = new(true);

    /// <summary>Creates the result of a signature that cannot be trusted.</summary>
    /// <param name="reason">What is wrong.</param>
    /// <returns>The result.</returns>
    public static AuthenticodeResult Untrusted(string reason) => new(false, reason);
}

/// <summary>
/// Reads and verifies the Authenticode signature of an executable file (PLAN.md S09 item 13, T704). Only the Windows platform has an
/// implementation (<c>WinVerifyTrust</c>); everywhere else no file counts as signed.
/// </summary>
public interface IAuthenticodeVerifier
{
    /// <summary>Tells whether a file carries an Authenticode signature at all, whether it is trusted or not.</summary>
    /// <param name="path">The file.</param>
    /// <returns>False for an unsigned file, a file that cannot be read and every file on a platform without Authenticode.</returns>
    bool IsSigned(string path);

    /// <summary>Verifies the signature of a file against the trust store of the system and checks that it covers the file as it is.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The outcome; an unsigned file is not trusted.</returns>
    AuthenticodeResult Verify(string path);
}

/// <summary>The <see cref="IAuthenticodeVerifier"/> of a platform without Authenticode: nothing is signed.</summary>
public sealed class NoAuthenticodeVerifier : IAuthenticodeVerifier
{
    /// <inheritdoc />
    public bool IsSigned(string path) => false;

    /// <inheritdoc />
    public AuthenticodeResult Verify(string path) => AuthenticodeResult.Untrusted("This platform cannot verify Authenticode signatures.");
}
