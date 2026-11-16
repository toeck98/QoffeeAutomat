using System.Text;
using System.Threading.Channels;
using QoffeeAutomat.Platform.Windows.Security;
using QoffeeAutomat.Platform.Windows.Sessions;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>A readable stream that the test feeds with text, standing in for a pipe that the agent owns.</summary>
internal sealed class FeedStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public void Feed(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Feed(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

    /// <summary>Ends the stream: the reader gets zero bytes once everything fed was read.</summary>
    public void Complete() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_chunks.Reader.TryRead(out byte[]? next))
            {
                _current = next;
                _offset = 0;
            }
        }

        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsSpan(_offset, count).CopyTo(buffer.Span);
        _offset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>The elevated token of a made-up user; it only remembers that it was disposed.</summary>
internal sealed class FakeElevatedToken : IElevatedToken
{
    public FakeElevatedToken(string userSid, string? userName)
    {
        UserSid = userSid;
        UserName = userName;
    }

    public string UserSid { get; }

    public string? UserName { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose() => IsDisposed = true;
}

/// <summary>A process that the test lets end, or never ends: nothing is started on the computer.</summary>
internal sealed class FakeElevatedProcess : IElevatedProcess
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeElevatedProcess(int processId, bool attached)
    {
        ProcessId = processId;
        if (attached)
        {
            Output = new FeedStream();
            Error = new FeedStream();
        }
    }

    public int ProcessId { get; }

    public FeedStream? Output { get; }

    public FeedStream? Error { get; }

    public Stream? StandardOutput => Output;

    public Stream? StandardError => Error;

    public bool WasKilled { get; private set; }

    public bool IsDisposed { get; private set; }

    /// <summary>Ends the process. A descendant that keeps the pipes open is simulated with <paramref name="closePipes"/> false.</summary>
    public void Exit(int exitCode, bool closePipes = true)
    {
        if (closePipes)
        {
            Output?.Complete();
            Error?.Complete();
        }

        _exit.TrySetResult(exitCode);
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        return await _exit.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Kill()
    {
        WasKilled = true;
        Exit(1);
    }

    public void Dispose() => IsDisposed = true;
}

/// <summary>
/// The Windows calls of <see cref="ElevatedLauncher"/> without Windows: the console session, the token of its user and
/// the process that would be created are all decided by the test, and nothing is ever started.
/// </summary>
internal sealed class FakeElevatedNativeApi : IElevatedNativeApi
{
    public const string UserSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private int _nextProcessId = 5000;

    /// <summary>The session of the console; null when nobody is attached to it.</summary>
    public int? ConsoleSession { get; set; } = 3;

    /// <summary>What <see cref="OpenElevatedToken"/> reports.</summary>
    public ElevatedTokenStatus TokenStatus { get; set; } = ElevatedTokenStatus.Available;

    public string? TokenMessage { get; set; }

    public string? UserName { get; set; } = "PC\\alice";

    /// <summary>Thrown by <see cref="OpenElevatedToken"/>.</summary>
    public Exception? OpenFailure { get; set; }

    /// <summary>Thrown by <see cref="Start"/>.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Called inside <see cref="Start"/>, so a test can observe the state at the moment the process would be created, or end it.</summary>
    public Action<FakeElevatedProcess, ElevatedLaunch>? OnStart { get; set; }

    public int ConsoleLookups { get; private set; }

    public List<int> OpenedSessions { get; } = [];

    public List<FakeElevatedToken> Tokens { get; } = [];

    public List<IElevatedToken> StartedWith { get; } = [];

    public List<ElevatedLaunch> Launches { get; } = [];

    public List<FakeElevatedProcess> Processes { get; } = [];

    public int? GetActiveConsoleSessionId()
    {
        ConsoleLookups++;
        return ConsoleSession;
    }

    public ElevatedTokenResult OpenElevatedToken(int sessionId)
    {
        OpenedSessions.Add(sessionId);
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        switch (TokenStatus)
        {
            case ElevatedTokenStatus.NoSession:
                return new ElevatedTokenResult(ElevatedTokenStatus.NoSession, null, TokenMessage);
            case ElevatedTokenStatus.NoLinkedToken:
                return new ElevatedTokenResult(ElevatedTokenStatus.NoLinkedToken, null, TokenMessage);
            case ElevatedTokenStatus.Failed:
                return new ElevatedTokenResult(ElevatedTokenStatus.Failed, null, TokenMessage);
            default:
                var token = new FakeElevatedToken(UserSid, UserName);
                Tokens.Add(token);
                return ElevatedTokenResult.Success(token);
        }
    }

    public IElevatedProcess Start(IElevatedToken token, ElevatedLaunch launch)
    {
        StartedWith.Add(token);
        Launches.Add(launch);
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        var process = new FakeElevatedProcess(_nextProcessId++, launch.Attached);
        Processes.Add(process);
        OnStart?.Invoke(process, launch);
        return process;
    }
}

/// <summary>An <see cref="IFileSystemAcl"/> that refuses to change permissions.</summary>
internal sealed class RefusingAcl : IFileSystemAcl
{
    public int Attempts { get; private set; }

    public AclSnapshot? Read(string path) => null;

    public void Protect(string path, IReadOnlyList<AclGrant> grants)
    {
        Attempts++;
        throw new UnauthorizedAccessException("Not allowed.");
    }
}
