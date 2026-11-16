using System.Runtime.InteropServices;
using System.Security.Principal;

namespace QoffeeAutomat.SessionHost.Ipc;

/// <summary>Facts about the token of the user that the SessionHost runs as.</summary>
public static class UserTokenInfo
{
    private const int TokenElevationTypeClass = 18;
    private const int ElevationTypeFull = 2;
    private const int ElevationTypeLimited = 3;

    /// <summary>
    /// Tells whether the user is an administrator, which <c>runAs: user_elevated</c> needs (PLAN.md D-036). The SessionHost runs
    /// with the standard token, in which the Administrators group is only a deny entry, so the check looks at the
    /// elevation type: a limited token has an elevated linked token, which only administrators get.
    /// </summary>
    /// <returns>True when the user is an administrator.</returns>
    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        if (NativeMethods.GetTokenInformation(identity.Token, TokenElevationTypeClass, out int type, sizeof(int), out _)
            && type is ElevationTypeFull or ElevationTypeLimited)
        {
            return true;
        }

        // UAC switched off, or a standard user: the group membership tells.
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The name of the user, <c>DOMAIN\name</c>.</summary>
    /// <returns>The user name.</returns>
    public static string CurrentUserName()
    {
        return OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : Environment.UserName;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, out int information, int length, out int returned);
    }
}
