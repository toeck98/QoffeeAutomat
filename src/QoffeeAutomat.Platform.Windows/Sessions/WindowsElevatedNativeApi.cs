using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QoffeeAutomat.Platform.Windows.Sessions;

/// <summary>
/// The <see cref="IElevatedNativeApi"/> of a Windows computer (PLAN.md S11, D-036, T311): <c>WTSGetActiveConsoleSessionId</c>
/// and <c>WTSQueryUserToken</c> for the user, <c>GetTokenInformation(TokenLinkedToken)</c> for the elevated token,
/// <c>CreateEnvironmentBlock</c> for the environment of the user and <c>CreateProcessAsUser</c> on <c>winsta0\default</c>.
/// Output goes to anonymous pipes that the agent owns, and only the three standard handles are inherited (an inherit list),
/// so no other handle of the service reaches the process. The process starts suspended, joins a job object and is then
/// resumed, so <see cref="IElevatedProcess.Kill"/> ends the whole tree. Needs the privileges of LocalSystem
/// (<c>SeTcbPrivilege</c>, <c>SeAssignPrimaryTokenPrivilege</c>); verified on a real PC (human_verify).
/// </summary>
public sealed class WindowsElevatedNativeApi : IElevatedNativeApi
{
    private const uint NoActiveSession = 0xFFFFFFFF;

    private const int ErrorInvalidParameter = 87;
    private const int ErrorNoToken = 1008;
    private const int ErrorNotFound = 1168;

    private const int TokenElevationTypeClass = 18;
    private const int TokenLinkedTokenClass = 19;
    private const int TokenElevationClass = 20;
    private const int ElevationTypeLimited = 3;

    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint ResumeFailed = 0xFFFFFFFF;

    private const string InteractiveDesktop = "winsta0\\default";
    private const string ProfileVariable = "USERPROFILE";

    /// <inheritdoc />
    public int? GetActiveConsoleSessionId()
    {
        uint session = NativeMethods.WTSGetActiveConsoleSessionId();
        return session == NoActiveSession || session == 0 ? null : (int)session;
    }

    /// <inheritdoc />
    public ElevatedTokenResult OpenElevatedToken(int sessionId)
    {
        if (!NativeMethods.WTSQueryUserToken((uint)sessionId, out IntPtr userToken))
        {
            int error = Marshal.GetLastPInvokeError();
            return error is ErrorNoToken or ErrorInvalidParameter or ErrorNotFound
                ? ElevatedTokenResult.NoSession($"Nobody is logged on in session {sessionId}.")
                : ElevatedTokenResult.Failed(
                    $"The token of the user in session {sessionId} could not be obtained: {new Win32Exception(error).Message}");
        }

        IntPtr elevated = IntPtr.Zero;
        try
        {
            if (!NativeMethods.GetTokenInformationInt32(userToken, TokenElevationTypeClass, out int elevationType, sizeof(int), out _))
            {
                return ElevatedTokenResult.Failed(
                    "The elevation type of the token of the user could not be read: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            if (elevationType != ElevationTypeLimited)
            {
                return ElevatedTokenResult.NoLinkedToken(
                    "The logged-in user has no elevated token: the user is not an administrator, or User Account Control is switched off.");
            }

            if (!NativeMethods.GetTokenInformationHandle(userToken, TokenLinkedTokenClass, out IntPtr linked, (uint)IntPtr.Size, out _))
            {
                return ElevatedTokenResult.NoLinkedToken(
                    "The logged-in user has no elevated token: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            try
            {
                // The linked token of a caller with SeTcbPrivilege is a primary token already; a copy makes sure of it.
                if (!NativeMethods.DuplicateTokenEx(linked, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out elevated))
                {
                    return ElevatedTokenResult.Failed(
                        "The elevated token of the user could not be duplicated: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(linked);
            }

            if (!NativeMethods.GetTokenInformationInt32(elevated, TokenElevationClass, out int isElevated, sizeof(int), out _) || isElevated == 0)
            {
                return ElevatedTokenResult.NoLinkedToken("The token that is linked to the token of the user is not elevated.");
            }

            string? sid;
            string? name;
            using (var identity = new WindowsIdentity(userToken))
            {
                sid = identity.User?.Value;
                name = TryGetName(identity);
            }

            if (sid is null)
            {
                return ElevatedTokenResult.Failed("The user of the session could not be identified.");
            }

            var token = new NativeElevatedToken(elevated, sid, name);
            elevated = IntPtr.Zero;
            return ElevatedTokenResult.Success(token);
        }
        finally
        {
            NativeMethods.CloseHandle(userToken);
            if (elevated != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(elevated);
            }
        }
    }

    /// <inheritdoc />
    public IElevatedProcess Start(IElevatedToken token, ElevatedLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(launch);
        if (token is not NativeElevatedToken native)
        {
            throw new ArgumentException("The token was not created by this API.", nameof(token));
        }

        if (string.IsNullOrWhiteSpace(launch.FileName) || launch.FileName.Contains('"', StringComparison.Ordinal))
        {
            throw new ElevatedLaunchException("The file name is not valid.");
        }

        IntPtr userEnvironment = IntPtr.Zero;
        if (!NativeMethods.CreateEnvironmentBlock(out userEnvironment, native.Handle, inherit: false))
        {
            throw new ElevatedLaunchException(
                "The environment of the user could not be created: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        Dictionary<string, string> userVariables;
        try
        {
            userVariables = ElevatedEnvironment.Parse(ReadBlock(userEnvironment));
        }
        finally
        {
            NativeMethods.DestroyEnvironmentBlock(userEnvironment);
        }

        // An ArgumentException from here (a NUL in a value) goes to the caller, which reports invalid_vars.
        string environmentBlock = ElevatedEnvironment.Build(userVariables, launch.Environment);
        string? workingDirectory = launch.WorkingDirectory is { Length: > 0 } directory
            ? directory
            : userVariables.GetValueOrDefault(ProfileVariable);

        return StartProcess(native, launch, environmentBlock, workingDirectory);
    }

    private static string? TryGetName(WindowsIdentity identity)
    {
#pragma warning disable CA1031 // The name is only used in log messages; whatever goes wrong while looking it up must not stop the run.
        try
        {
            return identity.Name;
        }
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }

    /// <summary>Reads the NUL-separated block that <c>CreateEnvironmentBlock</c> returned, up to its terminating empty entry.</summary>
    private static string ReadBlock(IntPtr block)
    {
        var text = new StringBuilder();
        int offset = 0;
        bool afterSeparator = false;
        while (true)
        {
            char character = (char)Marshal.ReadInt16(block, offset);
            offset += sizeof(char);
            if (character == '\0')
            {
                if (afterSeparator || text.Length == 0)
                {
                    return text.ToString();
                }

                afterSeparator = true;
            }
            else
            {
                afterSeparator = false;
            }

            text.Append(character);
        }
    }

    private static NativeElevatedProcess StartProcess(NativeElevatedToken token, ElevatedLaunch launch, string environmentBlock, string? workingDirectory)
    {
        AnonymousPipeServerStream? outputPipe = null;
        AnonymousPipeServerStream? errorPipe = null;
        AnonymousPipeServerStream? inputPipe = null;
        IntPtr environment = IntPtr.Zero;
        IntPtr commandLine = IntPtr.Zero;
        IntPtr desktop = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandles = IntPtr.Zero;
        IntPtr job = IntPtr.Zero;
        bool succeeded = false;
        try
        {
            if (launch.Attached)
            {
                outputPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                errorPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
                inputPipe = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            }

            environment = Marshal.StringToHGlobalUni(environmentBlock);
            commandLine = Marshal.StringToHGlobalUni(WtsSessionNativeApi.BuildCommandLine(launch.FileName, launch.Arguments));
            desktop = Marshal.StringToHGlobalUni(InteractiveDesktop);

            var startup = new NativeMethods.StartupInfoEx();
            startup.StartupInfo.lpDesktop = desktop;
            uint flags = CreateUnicodeEnvironment | CreateNoWindow;

            if (launch.Attached)
            {
                IntPtr[] standardHandles =
                [
                    inputPipe!.ClientSafePipeHandle.DangerousGetHandle(),
                    outputPipe!.ClientSafePipeHandle.DangerousGetHandle(),
                    errorPipe!.ClientSafePipeHandle.DangerousGetHandle(),
                ];
                startup.StartupInfo.dwFlags = StartfUseStdHandles;
                startup.StartupInfo.hStdInput = standardHandles[0];
                startup.StartupInfo.hStdOutput = standardHandles[1];
                startup.StartupInfo.hStdError = standardHandles[2];

                // Only these three handles are inherited; every other inheritable handle of the service stays out of the process.
                attributeList = CreateAttributeList(standardHandles, out inheritedHandles);
                startup.lpAttributeList = attributeList;
                startup.StartupInfo.cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>();
                flags |= ExtendedStartupInfoPresent | CreateSuspended;

                job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
            }
            else
            {
                startup.StartupInfo.cb = Marshal.SizeOf<NativeMethods.StartupInfo>();
            }

            if (!NativeMethods.CreateProcessAsUserW(
                    token.Handle,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: launch.Attached,
                    flags,
                    environment,
                    string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                    ref startup,
                    out NativeMethods.ProcessInformation information))
            {
                throw new ElevatedLaunchException(new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            // The child owns its ends now; keeping ours would stop the output from ever reaching its end.
            outputPipe?.DisposeLocalCopyOfClientHandle();
            errorPipe?.DisposeLocalCopyOfClientHandle();
            inputPipe?.Dispose();

            // The process handle stays open: it is what the wait, the exit code and the kill use, and it keeps the id from being reused.
            try
            {
                if (job != IntPtr.Zero && !NativeMethods.AssignProcessToJobObject(job, information.hProcess))
                {
                    // Not fatal: a timeout then ends the process itself, but not what it started.
                    NativeMethods.CloseHandle(job);
                    job = IntPtr.Zero;
                }

                if (launch.Attached && NativeMethods.ResumeThread(information.hThread) == ResumeFailed)
                {
                    int resumeError = Marshal.GetLastPInvokeError();
                    NativeMethods.TerminateProcess(information.hProcess, 1);
                    throw new ElevatedLaunchException("The process could not be resumed: " + new Win32Exception(resumeError).Message);
                }
            }
            catch
            {
                NativeMethods.CloseHandle(information.hProcess);
                throw;
            }
            finally
            {
                NativeMethods.CloseHandle(information.hThread);
            }

            succeeded = true;
            Stream? output = outputPipe;
            Stream? error = errorPipe;
            return new NativeElevatedProcess(information.hProcess, (int)information.dwProcessId, job, output, error);
        }
        finally
        {
            if (!succeeded)
            {
                outputPipe?.Dispose();
                errorPipe?.Dispose();
                inputPipe?.Dispose();
                if (job != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(job);
                }
            }

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (inheritedHandles != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(inheritedHandles);
            }

            Marshal.FreeHGlobal(environment);
            Marshal.FreeHGlobal(commandLine);
            Marshal.FreeHGlobal(desktop);
        }
    }

    /// <summary>Builds a process attribute list that limits the inherited handles to the given ones.</summary>
    /// <param name="handles">The handles that the process may inherit.</param>
    /// <param name="handleArray">The unmanaged copy of the handles, which must stay alive until the process was created; the caller frees it.</param>
    /// <returns>The attribute list; the caller deletes and frees it.</returns>
    private static IntPtr CreateAttributeList(IntPtr[] handles, out IntPtr handleArray)
    {
        handleArray = IntPtr.Zero;
        IntPtr size = IntPtr.Zero;

        // The first call only reports the size and fails with ERROR_INSUFFICIENT_BUFFER by design.
        NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        IntPtr list = Marshal.AllocHGlobal(size);
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(list, 1, 0, ref size))
            {
                throw new ElevatedLaunchException(
                    "The attribute list of the process could not be created: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            handleArray = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            for (int index = 0; index < handles.Length; index++)
            {
                Marshal.WriteIntPtr(handleArray, index * IntPtr.Size, handles[index]);
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    list,
                    0,
                    new IntPtr(ProcThreadAttributeHandleList),
                    handleArray,
                    new IntPtr(handles.Length * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                NativeMethods.DeleteProcThreadAttributeList(list);
                throw new ElevatedLaunchException(
                    "The inherited handles of the process could not be limited: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            return list;
        }
        catch
        {
            Marshal.FreeHGlobal(list);
            if (handleArray != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(handleArray);
                handleArray = IntPtr.Zero;
            }

            throw;
        }
    }

    private sealed class NativeElevatedToken : IElevatedToken
    {
        private IntPtr _handle;

        public NativeElevatedToken(IntPtr handle, string userSid, string? userName)
        {
            _handle = handle;
            UserSid = userSid;
            UserName = userName;
        }

        public string UserSid { get; }

        public string? UserName { get; }

        public IntPtr Handle => _handle;

        public void Dispose()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(handle);
            }
        }
    }

    private sealed class NativeElevatedProcess : IElevatedProcess
    {
        private const int KilledExitCode = 1;

        private readonly Stream? _output;
        private readonly Stream? _error;
        private IntPtr _process;
        private IntPtr _job;

        public NativeElevatedProcess(IntPtr process, int processId, IntPtr job, Stream? output, Stream? error)
        {
            _process = process;
            _job = job;
            _output = output;
            _error = error;
            ProcessId = processId;
        }

        public int ProcessId { get; }

        public Stream? StandardOutput => _output;

        public Stream? StandardError => _error;

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            IntPtr process = _process;
            ObjectDisposedException.ThrowIf(process == IntPtr.Zero, this);

            var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var waitHandle = new ProcessWaitHandle(process);
            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                waitHandle,
                (_, _) => completion.TrySetResult(ReadExitCode(process)),
                state: null,
                Timeout.Infinite,
                executeOnlyOnce: true);
            try
            {
                using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
                {
                    return await completion.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                registration.Unregister(null);
            }
        }

        public void Kill()
        {
            IntPtr job = _job;
            IntPtr process = _process;
            if (job != IntPtr.Zero)
            {
                NativeMethods.TerminateJobObject(job, KilledExitCode);
            }
            else if (process != IntPtr.Zero)
            {
                NativeMethods.TerminateProcess(process, KilledExitCode);
            }
        }

        public void Dispose()
        {
            _output?.Dispose();
            _error?.Dispose();

            // Closing the job does not end the processes in it: a program that the command started and left running stays.
            IntPtr job = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (job != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(job);
            }

            IntPtr process = Interlocked.Exchange(ref _process, IntPtr.Zero);
            if (process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(process);
            }
        }

        private static int ReadExitCode(IntPtr process)
        {
            return NativeMethods.GetExitCodeProcess(process, out uint code) ? unchecked((int)code) : -1;
        }
    }

    /// <summary>Lets the thread pool wait for a process handle that it does not own.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr process)
        {
            SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
        }
    }

#pragma warning disable CS0649 // The fields of the native structures are filled in by Windows.
    private static class NativeMethods
    {
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
        internal struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformationInt32(IntPtr token, int informationClass, out int information, uint length, out uint returnLength);

        [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetTokenInformationHandle(IntPtr token, int informationClass, out IntPtr information, uint length, out uint returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateTokenEx(
            IntPtr existingToken,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr newToken);

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
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr CreateJobObjectW(IntPtr jobAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
