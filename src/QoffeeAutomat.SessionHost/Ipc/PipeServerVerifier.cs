using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace QoffeeAutomat.SessionHost.Ipc;

/// <summary>Decides whether the process on the other end of the pipe is the agent (PLAN.md S09 item 7).</summary>
public interface IPipeServerVerifier
{
    /// <summary>Checks the server of a connected client pipe.</summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <returns>Null when the server is the agent; otherwise why it is not.</returns>
    string? Verify(NamedPipeClientStream pipe);
}

/// <summary>Compares file system paths of executables the way Windows does.</summary>
internal static class ExecutablePaths
{
    /// <summary>Tells whether two paths name the same file, ignoring case on Windows and relative parts such as <c>..</c>.</summary>
    /// <param name="left">The first path.</param>
    /// <param name="right">The second path.</param>
    /// <returns>True when both are not empty and name the same file.</returns>
    public static bool AreSame(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}

/// <summary>
/// The <see cref="IPipeServerVerifier"/> backed by Windows: the server process must run in the services session 0 (a
/// standard user cannot start a process there), its image must be the agent executable, and, where the SessionHost may
/// look at the token of the server, the token user must be SYSTEM. A process that squats on the pipe name before the
/// service starts fails all three.
/// </summary>
public sealed class NativePipeServerVerifier : IPipeServerVerifier
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private const int MaxPath = 32768;
    private const string LocalSystemSid = "S-1-5-18";

    private readonly string _agentImagePath;

    /// <summary>Creates the verifier.</summary>
    /// <param name="agentImagePath">The executable of the agent.</param>
    public NativePipeServerVerifier(string agentImagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentImagePath);
        _agentImagePath = agentImagePath;
    }

    /// <inheritdoc />
    public string? Verify(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        if (!NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId))
        {
            return "the server process of the pipe is unknown.";
        }

        if (!NativeMethods.ProcessIdToSessionId(processId, out uint sessionId) || sessionId != 0)
        {
            return $"the server process {processId} does not run in the services session.";
        }

        IntPtr process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process == IntPtr.Zero)
        {
            return $"the server process {processId} cannot be inspected.";
        }

        try
        {
            string? image = QueryImagePath(process);
            if (!ExecutablePaths.AreSame(image, _agentImagePath))
            {
                return $"the server process {processId} is not the agent (its image is '{image ?? "unknown"}').";
            }

            // The token of a SYSTEM process is not readable for a standard user; then session 0 and the installed image
            // above are the proof. Where it can be read it must belong to SYSTEM.
            string? owner = QueryTokenUserSid(process);
            if (owner is not null && !string.Equals(owner, LocalSystemSid, StringComparison.Ordinal))
            {
                return $"the server process {processId} does not run as SYSTEM.";
            }

            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static string? QueryImagePath(IntPtr process)
    {
        char[] buffer = new char[MaxPath];
        uint size = (uint)buffer.Length;
        return NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)
            ? new string(buffer, 0, (int)size)
            : null;
    }

    private static string? QueryTokenUserSid(IntPtr process)
    {
        if (!NativeMethods.OpenProcessToken(process, TokenQuery, out IntPtr token))
        {
            return null;
        }

        try
        {
            NativeMethods.GetTokenInformation(token, TokenUserClass, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
            {
                return null;
            }

            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!NativeMethods.GetTokenInformation(token, TokenUserClass, buffer, needed, out _))
                {
                    return null;
                }

                // TOKEN_USER starts with the pointer to the SID.
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                return new SecurityIdentifier(sid).Value;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(token);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, [Out] char[] imageName, ref uint size);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr information, int length, out int returned);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
