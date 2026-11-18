using QoffeeAutomat.Platform.Linux.Metrics.Session;
using static QoffeeAutomat.Platform.Linux.Tests.SystemMetricTestData;

namespace QoffeeAutomat.Platform.Linux.Tests.Metrics;

/// <summary>
/// The session metrics through logind with fake sources (PLAN.md S11, S17, T801): the parsers on the text of a real system, the
/// choice of the interactive session, the sources and the provider. They run on every system and need no logind.
/// </summary>
[Trait("Category", "SessionMetrics")]
public sealed class LogindSessionTests
{
    private const string ListSessions = """
            2  1000 tim    seat0 tty2
           c1   120 gdm    seat0 -
            4  1000 tim    -     -
        """;

    private const string ShowActiveSession = """
        Id=2
        User=1000
        Name=tim
        State=active
        Type=wayland
        Class=user
        Active=yes
        Remote=no
        Seat=seat0
        LockedHint=yes
        """;

    private const string ShowSshSession = """
        Id=4
        User=1000
        Name=tim
        State=online
        Type=tty
        Class=user
        Active=no
        Remote=yes
        Seat=
        LockedHint=no
        """;

    private const string SessionFile = """
        # This is private data. Do not parse.
        UID=1000
        USER=tim
        ACTIVE=1
        IS_DISPLAY=1
        STATE=active
        REMOTE=0
        TYPE=wayland
        CLASS=user
        SEAT=seat0
        VTNR=2
        """;

    private const string GreeterFile = "UID=120\nUSER=gdm\nACTIVE=1\nSTATE=active\nREMOTE=0\nTYPE=wayland\nCLASS=greeter\nSEAT=seat0\n";

    // ------------------------------------------------------------------
    // Parsers
    // ------------------------------------------------------------------

    [Fact]
    public void The_session_ids_are_the_first_column_of_the_list()
    {
        string[] expected = ["2", "c1", "4"];

        Assert.Equal(expected, LogindSessionParser.ParseSessionIds(ListSessions));
    }

    [Fact]
    public void Entries_that_are_not_plain_session_ids_never_reach_a_command_line()
    {
        IReadOnlyList<string> ids = LogindSessionParser.ParseSessionIds("2;reboot 1000 x\n$(id) 1 y\n--help 1 z\n7 1000 tim seat0 tty1\n");

        Assert.Equal("7", Assert.Single(ids));
    }

    [Fact]
    public void Show_session_is_parsed_into_a_session()
    {
        LogindSession? session = LogindSessionParser.ParseShowSession("2", ShowActiveSession);

        Assert.Equal(new LogindSession("2", "tim", 1000, "seat0", "user", "wayland", "active", IsActive: true, IsRemote: false, IsLocked: true), session);
    }

    [Fact]
    public void A_session_without_seat_that_comes_over_the_network_is_remote()
    {
        LogindSession? session = LogindSessionParser.ParseShowSession("4", ShowSshSession);

        Assert.NotNull(session);
        Assert.Null(session.Seat);
        Assert.True(session.IsRemote);
        Assert.False(session.IsActive);
        Assert.False(session.IsLocked);
    }

    [Fact]
    public void An_old_loginctl_that_prints_the_user_id_with_the_name_is_understood()
    {
        LogindSession? session = LogindSessionParser.ParseShowSession("2", "Name=tim\nUser=1000 (tim)\nSeat=seat0\n");

        Assert.Equal(1000, session?.Uid);
    }

    [Fact]
    public void The_session_file_is_parsed_and_does_not_know_whether_the_session_is_locked()
    {
        LogindSession? session = LogindSessionParser.ParseSessionFile("2", SessionFile);

        Assert.Equal(new LogindSession("2", "tim", 1000, "seat0", "user", "wayland", "active", IsActive: true, IsRemote: false, IsLocked: null), session);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Id=2\nState=active\n")]
    [InlineData("Name=\n")]
    public void Output_without_a_user_is_not_a_session(string output)
    {
        Assert.Null(LogindSessionParser.ParseShowSession("2", output));
        Assert.Null(LogindSessionParser.ParseSessionFile("2", output));
    }

    // ------------------------------------------------------------------
    // Choosing the interactive session
    // ------------------------------------------------------------------

    [Fact]
    public void The_interactive_session_is_the_active_user_session_on_a_seat()
    {
        LogindSession[] sessions =
        [
            Session("c1", user: "gdm", sessionClass: "greeter"),
            Session("4", seat: null, remote: true, type: "tty", active: false, state: "online"),
            Session("2", user: "tim"),
        ];

        Assert.Equal("2", LogindSessionSelector.SelectInteractive(sessions)?.Id);
    }

    [Fact]
    public void An_active_session_wins_over_an_inactive_one_and_a_graphical_one_over_a_console()
    {
        LogindSession activeConsole = Session("1", type: "tty", active: true);
        LogindSession inactiveGraphical = Session("2", type: "x11", active: false, state: "online");
        LogindSession inactiveConsole = Session("3", type: "tty", active: false, state: "online");
        LogindSession otherGraphical = Session("0", type: "wayland", active: false, state: "online");

        Assert.Equal("1", LogindSessionSelector.SelectInteractive([inactiveGraphical, activeConsole, inactiveConsole])?.Id);
        Assert.Equal("0", LogindSessionSelector.SelectInteractive([inactiveConsole, inactiveGraphical, otherGraphical])?.Id);
        Assert.Equal("2", LogindSessionSelector.SelectInteractive([inactiveConsole, inactiveGraphical])?.Id);
    }

    [Fact]
    public void Nobody_is_logged_in_at_the_computer_when_there_are_only_remote_closing_or_seatless_sessions()
    {
        LogindSession[] sessions =
        [
            Session("1", remote: true, seat: null),
            Session("2", state: "closing"),
            Session("3", seat: null),
            Session("4", sessionClass: "background"),
            Session("5", sessionClass: "greeter"),
        ];

        Assert.Null(LogindSessionSelector.SelectInteractive(sessions));
        Assert.Null(LogindSessionSelector.SelectInteractive([]));
    }

    // ------------------------------------------------------------------
    // Sources
    // ------------------------------------------------------------------

    [Fact]
    public async Task Loginctl_is_asked_for_the_list_and_then_for_each_session()
    {
        var reader = new FakeProcessReader();
        reader.Answer("list-sessions", ListSessions);
        reader.Answer("show-session 2", ShowActiveSession);
        reader.Answer("show-session 4", ShowSshSession);
        reader.Answer("show-session c1", "Id=c1\nUser=120\nName=gdm\nClass=greeter\nSeat=seat0\nState=active\nActive=yes\nRemote=no\nType=wayland\n");
        var source = new LoginctlSessionSource(reader);

        IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(CancellationToken.None);

        Assert.NotNull(sessions);
        string[] ids = [.. sessions.Select(session => session.Id)];
        string[] expected = ["2", "c1", "4"];
        Assert.Equal(expected, ids);
        Assert.All(reader.Calls, call => Assert.Equal("loginctl", call.FileName));
        Assert.Equal(4, reader.Calls.Count);
    }

    [Fact]
    public async Task The_session_id_is_a_separate_argument_of_its_own()
    {
        var reader = new FakeProcessReader();
        reader.Answer("list-sessions", ListSessions);
        var source = new LoginctlSessionSource(reader);

        _ = await source.ListSessionsAsync(CancellationToken.None);

        string[] show = reader.Calls.Where(call => call.Arguments[0] == "show-session").Select(call => call.Arguments[1]).ToArray();
        string[] expected = ["2", "c1", "4"];
        Assert.Equal(expected, show);
        Assert.All(reader.Calls.Where(call => call.Arguments[0] == "show-session"), call => Assert.Contains("--no-pager", call.Arguments));
    }

    [Fact]
    public async Task A_session_id_with_shell_syntax_is_never_passed_to_the_program()
    {
        var reader = new FakeProcessReader();
        reader.Answer("list-sessions", "2;reboot 1000 x seat0 -\n7 1000 tim seat0 tty1\n");
        reader.Answer("show-session 7", ShowActiveSession);
        var source = new LoginctlSessionSource(reader);

        IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(CancellationToken.None);

        Assert.NotNull(sessions);
        Assert.Single(sessions);
        Assert.DoesNotContain(reader.Calls, call => call.Arguments.Any(argument => argument.Contains(';', StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Loginctl_that_is_not_installed_gives_null_which_means_unknown()
    {
        var source = new LoginctlSessionSource(new FakeProcessReader());

        Assert.Null(await source.ListSessionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_session_that_ended_between_the_list_and_the_query_is_skipped()
    {
        var reader = new FakeProcessReader();
        reader.Answer("list-sessions", ListSessions);
        reader.Answer("show-session 2", ShowActiveSession);
        var source = new LoginctlSessionSource(reader);

        IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(CancellationToken.None);

        Assert.Equal("2", Assert.Single(sessions!).Id);
    }

    [Fact]
    public async Task The_session_files_of_logind_are_read_and_the_reference_pipes_are_ignored()
    {
        using var root = new FixtureRoot();
        root.Write("run/systemd/sessions/2", SessionFile);
        root.Write("run/systemd/sessions/c1", GreeterFile);
        root.Write("run/systemd/sessions/2.ref", "not a session");
        root.Write("run/systemd/sessions/bad;name", SessionFile);
        var source = new SessionFilesLogindSource(root.Root);

        IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(CancellationToken.None);

        Assert.NotNull(sessions);
        string[] ids = [.. sessions.Select(session => session.Id)];
        string[] expected = ["2", "c1"];
        Assert.Equal(expected, ids);
        Assert.Equal("greeter", sessions.Single(session => session.Id == "c1").SessionClass);
    }

    [Fact]
    public async Task Without_the_directory_of_logind_the_answer_is_unknown_and_with_an_empty_one_nobody()
    {
        using var root = new FixtureRoot();
        var source = new SessionFilesLogindSource(root.Root);
        Assert.Null(await source.ListSessionsAsync(CancellationToken.None));

        root.CreateDirectory("run/systemd/sessions");

        Assert.Empty((await source.ListSessionsAsync(CancellationToken.None))!);
    }

    [Fact]
    public async Task The_fallback_asks_the_next_source_only_when_the_first_cannot_answer()
    {
        LogindSession session = Session("2");
        var cannotAnswer = new FakeSessionSource(null);
        var answers = new FakeSessionSource([session]);
        var emptyAnswer = new FakeSessionSource([]);
        var neverAsked = new FakeSessionSource([session]);

        IReadOnlyList<LogindSession>? fromSecond = await new FallbackLogindSessionSource([cannotAnswer, answers]).ListSessionsAsync(CancellationToken.None);
        IReadOnlyList<LogindSession>? fromFirst = await new FallbackLogindSessionSource([emptyAnswer, neverAsked]).ListSessionsAsync(CancellationToken.None);
        IReadOnlyList<LogindSession>? fromNone = await new FallbackLogindSessionSource([cannotAnswer]).ListSessionsAsync(CancellationToken.None);

        Assert.Equal(session, Assert.Single(fromSecond!));
        Assert.Empty(fromFirst!);
        Assert.Equal(0, neverAsked.Calls);
        Assert.Null(fromNone);
    }

    [Fact]
    public async Task The_default_source_falls_back_to_the_session_files_when_loginctl_is_missing()
    {
        using var root = new FixtureRoot();
        root.Write("run/systemd/sessions/2", SessionFile);
        ILogindSessionSource source = LogindSessionSources.CreateDefault(new FakeProcessReader(), root.Root);

        IReadOnlyList<LogindSession>? sessions = await source.ListSessionsAsync(CancellationToken.None);

        Assert.Equal("tim", Assert.Single(sessions!).UserName);
    }

    // ------------------------------------------------------------------
    // The provider
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_provider_reports_the_user_and_whether_the_session_is_locked()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource([Session("2", user: "tim", locked: true)]));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.user", "session.locked"], CancellationToken.None);

        Assert.Equal("tim", values["session.user"]);
        Assert.True(Assert.IsType<bool>(values["session.locked"]));
    }

    [Fact]
    public async Task An_unlocked_session_reports_false()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource([Session("2", locked: false)]));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.locked"], CancellationToken.None);

        Assert.False(Assert.IsType<bool>(values["session.locked"]));
    }

    [Fact]
    public async Task A_desktop_that_does_not_tell_logind_leaves_the_lock_state_unknown()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource([Session("2", user: "tim", locked: null)]));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.user", "session.locked"], CancellationToken.None);

        Assert.Equal("tim", values["session.user"]);
        Assert.Null(values["session.locked"]);
    }

    [Fact]
    public async Task Nobody_logged_in_makes_both_metrics_unavailable()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource([Session("4", remote: true, seat: null)]));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.user", "session.locked"], CancellationToken.None);

        Assert.Null(values["session.user"]);
        Assert.Null(values["session.locked"]);
    }

    [Fact]
    public async Task A_logind_that_cannot_be_asked_makes_both_metrics_unavailable()
    {
        var provider = new LinuxSessionMetricProvider(new FakeSessionSource(null));

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.user", "session.locked"], CancellationToken.None);

        Assert.Null(values["session.user"]);
        Assert.Null(values["session.locked"]);
        Assert.Null(await provider.ReadSessionAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Other_ids_do_not_ask_logind_at_all()
    {
        var source = new FakeSessionSource([Session("2")]);
        var provider = new LinuxSessionMetricProvider(source);

        IReadOnlyDictionary<string, object?> values = await provider.SampleAsync(["session.idle_seconds", "cpu.usage"], CancellationToken.None);

        Assert.Empty(values);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task One_sample_of_both_metrics_asks_logind_once()
    {
        var source = new FakeSessionSource([Session("2")]);
        var provider = new LinuxSessionMetricProvider(source);

        _ = await provider.SampleAsync(["session.user", "session.locked"], CancellationToken.None);

        Assert.Equal(1, source.Calls);
    }
}
