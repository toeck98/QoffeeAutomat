using System.Globalization;
using System.Runtime.InteropServices;
using QoffeeAutomat.Core.Updates;

namespace QoffeeAutomat.Platform.Windows.Updates;

/// <summary>
/// Verifies Authenticode signatures with <c>WinVerifyTrust</c> and the generic verify policy (PLAN.md S09 item 13, T704): the signature
/// must be present, must cover the file as it is, and must chain to a root that the system trusts. A verification also checks revocation
/// of the whole chain except the root and fails when the revocation status cannot be determined: the installer was downloaded a moment
/// ago, so the computer is online, and refusing is the safe side. Asking only whether a file is signed (<see cref="IsSigned"/>) checks
/// nothing but the presence of a signature.
/// </summary>
public sealed class WinTrustAuthenticodeVerifier : IAuthenticodeVerifier
{
    private const uint TrustENoSignature = 0x800B0100;
    private const uint TrustEProviderUnknown = 0x800B0001;
    private const uint TrustESubjectFormUnknown = 0x800B0003;
    private const uint TrustEBadDigest = 0x80096010;
    private const uint CertEUntrustedRoot = 0x800B0109;
    private const uint CertEChaining = 0x800B010A;
    private const uint CertEExpired = 0x800B0101;
    private const uint CertERevoked = 0x800B010C;
    private const uint CertERevocationFailure = 0x800B010E;
    private const uint FacilityCertificate = 0x800B0000;
    private const uint FacilityTrust = 0x80090000;
    private const uint FacilityMask = 0xFFFF0000;

    /// <inheritdoc />
    public bool IsSigned(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        uint code = WinTrustNative.Verify(path, checkRevocation: false);
        if (code == 0)
        {
            return true;
        }

        if (code is TrustENoSignature or TrustEProviderUnknown or TrustESubjectFormUnknown)
        {
            return false;
        }

        // A signature that is present but not trusted (an unknown root, an expired certificate, a changed file) still means that the file is signed.
        return (code & FacilityMask) is FacilityCertificate or FacilityTrust;
    }

    /// <inheritdoc />
    public AuthenticodeResult Verify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        uint code = WinTrustNative.Verify(path, checkRevocation: true);
        return code == 0 ? AuthenticodeResult.Trusted : AuthenticodeResult.Untrusted(Describe(code));
    }

    private static string Describe(uint code)
    {
        string text = code switch
        {
            TrustENoSignature => "the file is not signed",
            TrustEBadDigest => "the file was changed after it was signed",
            CertEUntrustedRoot => "the certificate chain ends in a root that is not trusted",
            CertEChaining => "the certificate chain is incomplete",
            CertEExpired => "the certificate has expired",
            CertERevoked => "the certificate was revoked",
            CertERevocationFailure => "the revocation status of the certificate could not be determined",
            _ => "the signature could not be verified",
        };
        return text + " (WinVerifyTrust 0x" + code.ToString("X8", CultureInfo.InvariantCulture) + ")";
    }
}

/// <summary>The call to <c>WinVerifyTrust</c> (wintrust.dll) for a file.</summary>
internal static class WinTrustNative
{
    // WINTRUST_ACTION_GENERIC_VERIFY_V2.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private const uint RevocationCheckNone = 0x00000010;
    private const uint RevocationCheckChainExcludeRoot = 0x00000080;

    /// <summary>Verifies a file.</summary>
    /// <param name="path">The file.</param>
    /// <param name="checkRevocation">Whether the revocation of the certificates is checked.</param>
    /// <returns>Zero when the signature is trusted, otherwise the code of <c>WinVerifyTrust</c> (an HRESULT).</returns>
    public static uint Verify(string path, bool checkRevocation)
    {
        IntPtr pathPointer = IntPtr.Zero;
        IntPtr filePointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var file = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
            };
            filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePointer, fDeleteOld: false);

            var data = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = checkRevocation ? RevokeWholeChain : RevokeNone,
                UnionChoice = ChoiceFile,
                File = filePointer,
                StateAction = StateActionIgnore,
                ProvFlags = checkRevocation ? RevocationCheckChainExcludeRoot : RevocationCheckNone,
            };

            Guid action = GenericVerifyV2;

            // INVALID_HANDLE_VALUE as the window: no user interface.
            return unchecked((uint)WinVerifyTrust(new IntPtr(-1), ref action, ref data));
        }
        finally
        {
            if (filePointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(filePointer);
            }

            if (pathPointer != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

#pragma warning disable CS0649 // The fields that stay zero are meant to be zero: the structures are read by Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
#pragma warning restore CS0649
}
