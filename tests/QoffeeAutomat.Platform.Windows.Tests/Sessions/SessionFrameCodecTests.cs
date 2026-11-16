using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Platform.Windows.Tests.Sessions;

/// <summary>
/// The length-prefixed JSON frames of the session pipe (PLAN.md S11, T302): every message type survives the trip, the
/// framing is strict, and a real named pipe in the test process carries the frames. No service is needed.
/// </summary>
[Trait("Category", "SessionIpc")]
public sealed class SessionFrameCodecTests
{
    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<SessionMessage> SampleMessages()
    {
        var variables = new Dictionary<string, SessionVariable>
        {
            ["Name"] = SessionVariable.From("Grüße \"quoted\" {{x}}"),
            ["Level"] = SessionVariable.From(40L),
            ["Ratio"] = SessionVariable.From(5.0),
            ["Mute"] = SessionVariable.From(true),
        };
        var environment = new Dictionary<string, string> { ["QA_RUN_ID"] = "run-1", ["QA_VAR_NAME"] = "x y" };

        return
        [
            new SessionHelloMessage(1, SessionRoles.SessionHost, 4242, 3, "0.1.0", "PC\\alice", IsAdministrator: true),
            new SessionHelloMessage(1, SessionRoles.Agent, 100, 0, "0.1.0", ScriptsDirectory: "C:\\ProgramData\\QoffeeAutomat\\scripts"),
            new SessionRunMessage(
                "r1",
                SessionRunKinds.Process,
                60_000,
                Process: new SessionProcessSpec("C:\\Tools\\tool.exe", ["--a", "b c", "{{Name}}"], environment, "C:\\Work", WaitForExit: false)),
            new SessionRunMessage(
                "r2",
                SessionRunKinds.PowerShell,
                30_000,
                CaptureOutput: false,
                PowerShell: new SessionPowerShellSpec(PowerShellSources.Inline, "auto", variables, environment, Code: "param($Name) $Name")),
            new SessionRunMessage(
                "r3",
                SessionRunKinds.Builtin,
                5_000,
                Builtin: new SessionBuiltinSpec("audio.set_volume", variables)),
            new SessionRunOutputMessage("r1", SessionOutputStreams.StandardError, "line\nsecond\n", 1_760_000_000_000),
            new SessionRunResultMessage("r1", 3, "out", "err", 1250, ErrorCode: "timeout", OutputTruncated: true),
            new SessionRunResultMessage("r2", 0, string.Empty, string.Empty, 0),
            new SessionCancelMessage("r1"),
            new SessionMetricsSubscribeMessage(["session.idle_time", "session.volume"]),
            new SessionMetricsStateMessage(1_760_000_000_000, new Dictionary<string, JsonElement>
            {
                ["session.idle_time"] = Json("12.5"),
                ["session.muted"] = Json("true"),
                ["session.active_window"] = Json("null"),
            }),
            new SessionNotifyMessage("Title", "Text with ümlaut"),
            new SessionEventMessage("locked", 1_760_000_000_000, Json("{\"user\":\"alice\"}")),
            new SessionEventMessage("unlocked", 1_760_000_000_001),
            new SessionShutdownMessage(SessionShutdownReasons.AgentStopping),
            new SessionTrayStateMessage("Gaming-PC", Paused: true, PairedClients: 1, ConnectedClients: 0, ShowTrayIcon: true, UiUrl: "http://127.0.0.1:8480/"),
            new SessionTrayStateMessage("Gaming-PC", Paused: false, PairedClients: 0, ConnectedClients: 0, ShowTrayIcon: false),
            new SessionSetPauseMessage(true),
        ];
    }

    private static string PayloadOf(byte[] frame)
    {
        return Encoding.UTF8.GetString(frame, SessionFrameCodec.HeaderSize, frame.Length - SessionFrameCodec.HeaderSize);
    }

    private static byte[] Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[SessionFrameCodec.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(SessionFrameCodec.HeaderSize));
        return frame;
    }

    [Fact]
    public async Task Every_message_type_survives_the_trip_through_a_frame()
    {
        foreach (SessionMessage message in SampleMessages())
        {
            using var stream = new MemoryStream();
            await SessionFrameCodec.WriteAsync(stream, message);
            stream.Position = 0;

            SessionMessage? read = await SessionFrameCodec.ReadAsync(stream);

            Assert.NotNull(read);
            Assert.Equal(message.GetType(), read.GetType());
            Assert.Equal(SessionFrameCodec.Encode(message), SessionFrameCodec.Encode(read));
        }
    }

    [Fact]
    public async Task Nothing_is_lost_in_a_message_with_nested_content()
    {
        var variables = new Dictionary<string, SessionVariable> { ["Name"] = SessionVariable.From("a \"b\" {{c}}") };
        var original = new SessionRunMessage(
            "r9",
            SessionRunKinds.Builtin,
            1234,
            Builtin: new SessionBuiltinSpec("app.launch", variables));

        using var stream = new MemoryStream();
        await SessionFrameCodec.WriteAsync(stream, original);
        stream.Position = 0;
        var read = Assert.IsType<SessionRunMessage>(await SessionFrameCodec.ReadAsync(stream));

        Assert.Equal("r9", read.RunId);
        Assert.Equal(SessionRunKinds.Builtin, read.Kind);
        Assert.Equal(1234, read.TimeoutMs);
        Assert.True(read.CaptureOutput);
        Assert.Null(read.Process);
        Assert.Null(read.PowerShell);
        Assert.Equal("app.launch", read.Builtin!.Name);
        Assert.Equal("a \"b\" {{c}}", read.Builtin.Variables["Name"].ToValue());
    }

    [Fact]
    public void A_frame_is_the_little_endian_length_followed_by_the_utf8_json_object()
    {
        byte[] frame = SessionFrameCodec.Encode(new SessionCancelMessage("r1"));

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        Assert.Equal((uint)(frame.Length - SessionFrameCodec.HeaderSize), length);
        string json = PayloadOf(frame);
        Assert.Equal("{\"t\":\"cancel\",\"run_id\":\"r1\"}", json);
    }

    [Fact]
    public void The_tray_messages_use_snake_case_members_and_omit_an_absent_ui_address()
    {
        string state = PayloadOf(SessionFrameCodec.Encode(new SessionTrayStateMessage("Gaming-PC", true, 1, 0, true)));
        string pause = PayloadOf(SessionFrameCodec.Encode(new SessionSetPauseMessage(false)));

        Assert.Equal(
            "{\"t\":\"tray_state\",\"agent_name\":\"Gaming-PC\",\"paused\":true,\"paired_clients\":1,\"connected_clients\":0,\"show_tray_icon\":true}",
            state);
        Assert.Equal("{\"t\":\"set_pause\",\"paused\":false}", pause);
    }

    [Fact]
    public void The_type_member_names_the_message_and_the_other_members_use_snake_case()
    {
        string json = PayloadOf(SessionFrameCodec.Encode(
            new SessionRunOutputMessage("r1", SessionOutputStreams.StandardOutput, "x", 5)));

        Assert.Contains("\"t\":\"run_output\"", json, StringComparison.Ordinal);
        Assert.Contains("\"run_id\":\"r1\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stream\":\"stdout\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_members_that_are_absent_are_left_out()
    {
        string json = PayloadOf(SessionFrameCodec.Encode(
            new SessionRunResultMessage("r1", 0, "o", string.Empty, 10)));

        Assert.DoesNotContain("error_code", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Frames_are_read_one_after_the_other_and_a_clean_end_of_the_stream_is_null()
    {
        using var stream = new MemoryStream();
        await SessionFrameCodec.WriteAsync(stream, new SessionCancelMessage("a"));
        await SessionFrameCodec.WriteAsync(stream, new SessionCancelMessage("b"));
        stream.Position = 0;

        Assert.Equal("a", Assert.IsType<SessionCancelMessage>(await SessionFrameCodec.ReadAsync(stream)).RunId);
        Assert.Equal("b", Assert.IsType<SessionCancelMessage>(await SessionFrameCodec.ReadAsync(stream)).RunId);
        Assert.Null(await SessionFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task A_frame_is_written_with_one_call_so_that_serialised_writers_cannot_interleave()
    {
        var recording = new CountingStream();

        await SessionFrameCodec.WriteAsync(recording, new SessionRunOutputMessage("r1", SessionOutputStreams.StandardOutput, "text", 1));

        Assert.Equal(1, recording.Writes);
    }

    [Fact]
    public async Task A_frame_above_the_limit_is_refused_before_its_payload_is_read()
    {
        byte[] header = new byte[SessionFrameCodec.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, SessionFrameCodec.MaxPayloadBytes + 1u);
        using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<SessionProtocolException>(async () => await SessionFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task An_empty_frame_is_refused()
    {
        using var stream = new MemoryStream(new byte[SessionFrameCodec.HeaderSize]);

        await Assert.ThrowsAsync<SessionProtocolException>(async () => await SessionFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task A_stream_that_ends_inside_the_length_prefix_is_refused()
    {
        using var stream = new MemoryStream([5, 0]);

        await Assert.ThrowsAsync<SessionProtocolException>(async () => await SessionFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public async Task A_stream_that_ends_inside_the_payload_is_refused()
    {
        byte[] frame = SessionFrameCodec.Encode(new SessionCancelMessage("r1"));
        using var stream = new MemoryStream(frame[..^3]);

        await Assert.ThrowsAsync<SessionProtocolException>(async () => await SessionFrameCodec.ReadAsync(stream));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"t\":\"unknown_message\"}")]
    [InlineData("{\"run_id\":\"r1\"}")]
    [InlineData("{\"t\":\"cancel\"}")]
    [InlineData("{\"t\":\"run_result\",\"run_id\":\"r1\",\"exit_code\":\"zero\",\"standard_output\":\"\",\"standard_error\":\"\",\"duration_ms\":1}")]
    public async Task A_payload_that_is_not_a_known_message_is_refused(string json)
    {
        using var stream = new MemoryStream(Frame(json));

        await Assert.ThrowsAsync<SessionProtocolException>(async () => await SessionFrameCodec.ReadAsync(stream));
    }

    [Fact]
    public void A_message_above_the_frame_limit_cannot_be_encoded()
    {
        var message = new SessionRunOutputMessage("r1", SessionOutputStreams.StandardOutput, new string('x', SessionFrameCodec.MaxPayloadBytes + 1), 0);

        Assert.Throws<SessionProtocolException>(() => SessionFrameCodec.Encode(message));
    }

    [Fact]
    public void Unknown_members_are_ignored_so_that_a_newer_peer_can_add_members()
    {
        byte[] frame = Frame("{\"t\":\"cancel\",\"run_id\":\"r1\",\"future\":{\"a\":1}}");

        SessionMessage message = SessionFrameCodec.Decode(frame.AsSpan(SessionFrameCodec.HeaderSize));

        Assert.Equal("r1", Assert.IsType<SessionCancelMessage>(message).RunId);
    }

    [Fact]
    public void Variables_keep_their_type_and_value_through_the_wire_form()
    {
        object[] values = ["text with \"quotes\" and ümlaut", 40L, 5.0, 0.1, -3.25e-7, true, false, string.Empty];

        foreach (object value in values)
        {
            SessionVariable wire = SessionVariable.From(value);
            object back = wire.ToValue();

            Assert.Equal(value.GetType(), back.GetType());
            Assert.Equal(value, back);
        }
    }

    [Fact]
    public void An_integer_that_is_not_a_long_is_sent_as_an_integer()
    {
        SessionVariable wire = SessionVariable.From(7);

        Assert.Equal(SessionVariableTypes.WholeNumber, wire.Type);
        Assert.Equal(7L, wire.ToValue());
    }

    [Fact]
    public void A_variable_of_an_unsupported_type_is_refused()
    {
        Assert.Throws<ArgumentException>(() => SessionVariable.From(new object()));
        Assert.Throws<FormatException>(() => new SessionVariable("nonsense", "x").ToValue());
        Assert.Throws<FormatException>(() => new SessionVariable(SessionVariableTypes.WholeNumber, "1.5").ToValue());
    }

    [Fact]
    public async Task Frames_travel_in_both_directions_through_a_named_pipe_in_the_same_process()
    {
        using var cts = new CancellationTokenSource(TestWait.Patience);
        string name = "qa-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(cts.Token), client.ConnectAsync(10_000, cts.Token)).WaitAsync(TestWait.Patience, cts.Token);

        await SessionFrameCodec.WriteAsync(client, new SessionHelloMessage(1, SessionRoles.SessionHost, 1, 3, "0.1.0", "alice"), cts.Token);
        SessionMessage? atServer = await SessionFrameCodec.ReadAsync(server, cts.Token);
        await SessionFrameCodec.WriteAsync(server, new SessionRunMessage("r1", SessionRunKinds.Process, 1000, Process: new SessionProcessSpec("tool", ["x"], new Dictionary<string, string>())), cts.Token);
        SessionMessage? atClient = await SessionFrameCodec.ReadAsync(client, cts.Token);

        Assert.Equal(3, Assert.IsType<SessionHelloMessage>(atServer).SessionId);
        Assert.Equal("r1", Assert.IsType<SessionRunMessage>(atClient).RunId);
    }

    [Fact]
    public async Task A_peer_that_closes_the_pipe_ends_the_stream_cleanly_between_frames()
    {
        string name = "qa-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(10_000)).WaitAsync(TestWait.Patience);

        await SessionFrameCodec.WriteAsync(client, new SessionCancelMessage("r1"));
        await client.DisposeAsync();

        Assert.IsType<SessionCancelMessage>(await SessionFrameCodec.ReadAsync(server).AsTask().WaitAsync(TestWait.Patience));
        Assert.Null(await SessionFrameCodec.ReadAsync(server).AsTask().WaitAsync(TestWait.Patience));
    }

    /// <summary>A write-only stream that counts the write calls.</summary>
    private sealed class CountingStream : Stream
    {
        public int Writes { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Writes++;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return ValueTask.CompletedTask;
        }
    }
}
