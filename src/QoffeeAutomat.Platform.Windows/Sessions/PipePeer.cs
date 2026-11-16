using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>The process on the other end of the session pipe, as Windows reports it (not as the process claims).</summary>
/// <param name="ProcessId">The process id of the client.</param>
/// <param name="SessionId">The Windows session of the client process.</param>
/// <param name="ImagePath">The full path of the client's executable; null when it could not be determined.</param>
public sealed record PipePeer(int ProcessId, int SessionId, string? ImagePath);

/// <summary>Asks the operating system who is on the other end of a pipe connection; a seam for tests.</summary>
public interface IPipePeerInspector
{
    /// <summary>Identifies the client of a connected server pipe.</summary>
    /// <param name="pipe">The connected pipe.</param>
    /// <returns>The client process.</returns>
    /// <exception cref="InvalidOperationException">The peer cannot be determined; the connection must be dropped.</exception>
    PipePeer Inspect(NamedPipeServerStream pipe);
}

/// <summary>Compares file system paths of executables the way Windows does.</summary>
public static class ImagePaths
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
/// The <see cref="IPipePeerInspector"/> backed by <c>GetNamedPipeClientProcessId</c>, <c>ProcessIdToSessionId</c> and
/// <c>QueryFullProcessImageName</c> (PLAN.md S09 item 7). The image path comes from the kernel, so a client cannot
/// lie about it; the limited query right that it needs is available to LocalSystem for every unprotected process.
/// </summary>
public sealed class NativePipePeerInspector : IPipePeerInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxPath = 32768;

    /// <inheritdoc />
    public PipePeer Inspect(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint processId))
        {
            throw new InvalidOperationException(
                "The client process of the pipe is unknown: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        if (!NativeMethods.ProcessIdToSessionId(processId, out uint sessionId))
        {
            throw new InvalidOperationException(
                "The session of the client process is unknown: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        return new PipePeer((int)processId, (int)sessionId, QueryImagePath(processId));
    }

    private static string? QueryImagePath(uint processId)
    {
        IntPtr process = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            char[] buffer = new char[MaxPath];
            uint size = (uint)buffer.Length;
            return NativeMethods.QueryFullProcessImageNameW(process, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size)
                : null;
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

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

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
