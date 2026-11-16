using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using QoffeeAutomat.Platform.Windows.Service;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The <see cref="ISessionNativeApi"/> backed by the terminal services and the process creation functions of Windows:
/// <c>WTSEnumerateSessions</c> lists the sessions, <c>WTSQueryUserToken</c> gets the token of the user (the standard,
/// not elevated one) and <c>CreateProcessAsUser</c> starts the process with the environment block of the user on the
/// desktop <c>winsta0\default</c>. Needs the privileges of LocalSystem; verified on a real PC (human_verify).
/// </summary>
public sealed class WtsSessionNativeApi : ISessionNativeApi
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const string InteractiveDesktop = "winsta0\\default";

    private static readonly char[] QuoteTriggers = [' ', '\t', '\n', '\v', '"'];

    private readonly ISessionUserLookup _users;

    /// <summary>Creates the API.</summary>
    /// <param name="users">Resolves the user name of a session.</param>
    public WtsSessionNativeApi(ISessionUserLookup users)
    {
        ArgumentNullException.ThrowIfNull(users);
        _users = users;
    }

    /// <inheritdoc />
    public IReadOnlyList<WtsSessionInfo> EnumerateSessions()
    {
        if (!NativeMethods.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr buffer, out uint count))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        try
        {
            int size = Marshal.SizeOf<NativeMethods.WtsSessionInfoNative>();
            var sessions = new List<WtsSessionInfo>((int)count);
            for (int index = 0; index < count; index++)
            {
                IntPtr item = IntPtr.Add(buffer, index * size);
                NativeMethods.WtsSessionInfoNative native = Marshal.PtrToStructure<NativeMethods.WtsSessionInfoNative>(item);
                int id = (int)native.SessionId;
                sessions.Add(new WtsSessionInfo(id, (WtsConnectState)native.State, _users.GetUserName(id)));
            }

            return sessions;
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
    }

    /// <inheritdoc />
    public ISessionProcess StartInSession(int sessionId, SessionLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentException.ThrowIfNullOrWhiteSpace(launch.ExecutablePath);

        if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out IntPtr token))
        {
            throw new SessionLaunchException(
                $"No user token for session {sessionId}: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        IntPtr environment = IntPtr.Zero;
        IntPtr commandLine = IntPtr.Zero;
        IntPtr desktop = IntPtr.Zero;
        try
        {
            if (!NativeMethods.CreateEnvironmentBlock(out environment, token, inherit: false))
            {
                throw new SessionLaunchException(
                    $"The environment of the user in session {sessionId} could not be created: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            commandLine = Marshal.StringToHGlobalUni(BuildCommandLine(launch.ExecutablePath, launch.Arguments));
            desktop = Marshal.StringToHGlobalUni(InteractiveDesktop);
            var startup = new NativeMethods.StartupInfo { cb = Marshal.SizeOf<NativeMethods.StartupInfo>(), lpDesktop = desktop };
            string workingDirectory = launch.WorkingDirectory ?? Path.GetDirectoryName(launch.ExecutablePath) ?? string.Empty;

            if (!NativeMethods.CreateProcessAsUserW(
                    token,
                    launch.ExecutablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateUnicodeEnvironment,
                    environment,
                    workingDirectory.Length == 0 ? null : workingDirectory,
                    ref startup,
                    out NativeMethods.ProcessInformation information))
            {
                throw new SessionLaunchException(
                    $"'{launch.ExecutablePath}' could not be started in session {sessionId}: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            try
            {
                // Take hold of the process while our handle keeps the id from being reused.
                return new ManagedSessionProcess(Process.GetProcessById((int)information.dwProcessId));
            }
            finally
            {
                NativeMethods.CloseHandle(information.hThread);
                NativeMethods.CloseHandle(information.hProcess);
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                NativeMethods.DestroyEnvironmentBlock(environment);
            }

            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(desktop);
            NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>Builds the command line of a process: the program in quotes, each argument quoted as the C runtime parses it.</summary>
    /// <param name="executable">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <returns>The command line.</returns>
    public static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var line = new StringBuilder();
        line.Append('"').Append(executable).Append('"');
        foreach (string argument in arguments)
        {
            line.Append(' ');
            AppendQuoted(line, argument);
        }

        return line.ToString();
    }

    private static void AppendQuoted(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(QuoteTriggers) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                // Backslashes before a quote are doubled, and the quote is escaped.
                line.Append('\\', (backslashes * 2) + 1);
                line.Append('"');
            }
            else
            {
                line.Append('\\', backslashes);
                line.Append(character);
            }

            backslashes = 0;
        }

        // Backslashes before the closing quote are doubled so that they do not escape it.
        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    private sealed class ManagedSessionProcess : ISessionProcess
    {
        private readonly Process _process;

        public ManagedSessionProcess(Process process)
        {
            _process = process;
            ProcessId = process.Id;
        }

        public int ProcessId { get; }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return _process.ExitCode;
        }

        public void Kill()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Gone already.
            }
        }

        public void Dispose()
        {
            _process.Dispose();
        }
    }

#pragma warning disable CS0649 // The fields of the native structures are filled in by Windows.
    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct WtsSessionInfoNative
        {
            public uint SessionId;
            public IntPtr WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSEnumerateSessionsW(IntPtr server, uint reserved, uint version, out IntPtr sessions, out uint count);

        [DllImport("wtsapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("userenv.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessAsUserW(
            IntPtr token,
            string? applicationName,
            IntPtr commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
