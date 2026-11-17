using System.Text.Json;
using QoffeeAutomat.Contracts.Api;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Ui.Components.Pages.CommandEditor;
using QoffeeAutomat.Ui.Tests.Support;

namespace QoffeeAutomat.Ui.Tests.CommandEditor;

/// <summary>The conditions (T705) and the secrets (T706) of a command in the draft of the editor: they round-trip, and a command without them stays as it was.</summary>
[Trait("Category", "CommandEditor")]
public sealed class CommandConditionsDraftTests
{
    [Fact]
    public void A_command_without_conditions_and_secrets_builds_without_the_members()
    {
        CommandDocument original = Samples.Command("plain", action: new PowerShellAction(PowerShellSources.Inline, Code: "Write-Output 1"));

        CommandDocument rebuilt = CommandDraft.From(original).Build().Document;

        Assert.Null(rebuilt.Conditions);
        Assert.Null(rebuilt.Secrets);
        Assert.Equal(Serialize(original), Serialize(rebuilt));
    }

    [Fact]
    public void The_session_any_without_a_window_is_no_condition_at_all()
    {
        var draft = new CommandDraft { Name = "Anything", ConditionSession = SessionRequirements.Any, WindowEnabled = false };

        Assert.Null(draft.Build().Document.Conditions);
        Assert.Null(draft.BuildConditions());
    }

    [Theory]
    [InlineData(SessionRequirements.LoggedIn)]
    [InlineData(SessionRequirements.Unlocked)]
    [InlineData(SessionRequirements.Locked)]
    public void A_session_requirement_is_written_and_read_back(string requirement)
    {
        CommandDocument document = Samples.Command("needs_session") with { Conditions = new CommandConditions(requirement) };

        CommandDraft draft = CommandDraft.From(document);
        CommandConditions? conditions = draft.Build().Document.Conditions;

        Assert.Equal(requirement, draft.ConditionSession);
        Assert.False(draft.WindowEnabled);
        Assert.Equal(requirement, conditions?.Session);
        Assert.Null(conditions?.TimeWindow);
        Assert.Equal(Serialize(document), Serialize(draft.Build().Document));
    }

    [Fact]
    public void A_time_window_with_days_round_trips_and_the_days_come_in_weekday_order()
    {
        CommandDocument document = Samples.Command("night_job") with
        {
            Conditions = new CommandConditions(SessionRequirements.Locked, new TimeWindow("22:00", "06:00", ["mon", "tue", "fri"])),
        };

        CommandDraft draft = CommandDraft.From(document);

        Assert.True(draft.WindowEnabled);
        Assert.Equal("22:00", draft.WindowFrom);
        Assert.Equal("06:00", draft.WindowTo);
        Assert.Equal(["mon", "tue", "fri"], draft.WindowDays);
        Assert.Equal(Serialize(document), Serialize(draft.Build().Document));

        draft.WindowDays.Clear();
        draft.WindowDays.AddRange(["sun", "wed", "mon"]);
        TimeWindow window = draft.Build().Document.Conditions!.TimeWindow!;
        Assert.Equal(["mon", "wed", "sun"], window.Days!);
    }

    [Fact]
    public void A_window_without_days_has_no_days_member_and_no_window_without_the_switch()
    {
        var draft = new CommandDraft { Name = "Day shift", WindowEnabled = true, WindowFrom = "08:00", WindowTo = "17:30" };

        TimeWindow? window = draft.Build().Document.Conditions?.TimeWindow;

        Assert.NotNull(window);
        Assert.Null(window.Days);
        Assert.Equal("17:30", window.To);

        draft.WindowEnabled = false;
        Assert.Null(draft.Build().Document.Conditions);
    }

    [Fact]
    public void An_unknown_session_value_is_kept_so_that_the_agent_can_report_it()
    {
        CommandDocument document = Samples.Command("odd") with { Conditions = new CommandConditions("sleeping") };

        CommandDocument rebuilt = CommandDraft.From(document).Build().Document;

        Assert.Equal("sleeping", rebuilt.Conditions?.Session);
    }

    [Fact]
    public void Secrets_of_a_powershell_or_process_command_round_trip()
    {
        CommandAction[] actions =
        [
            new PowerShellAction(PowerShellSources.Inline, Code: "Write-Output $env:QA_SECRET_API_KEY"),
            new ProcessAction("backup.exe", ["--quiet"]),
        ];

        foreach (CommandAction action in actions)
        {
            CommandDocument document = Samples.Command("uses_secrets", action: action) with { Secrets = ["api_key", "db_password"] };

            CommandDraft draft = CommandDraft.From(document);

            Assert.True(draft.SupportsSecrets);
            Assert.Equal(["api_key", "db_password"], draft.Secrets);
            Assert.Equal(Serialize(document), Serialize(draft.Build().Document));
        }
    }

    [Fact]
    public void Secrets_are_left_out_of_an_action_that_cannot_use_them()
    {
        var draft = new CommandDraft { Name = "Lock", ActionType = ActionTypes.Builtin, BuiltinName = "session.lock" };
        draft.Secrets.Add("api_key");

        Assert.False(draft.SupportsSecrets);
        Assert.Null(draft.Build().Document.Secrets);

        draft.ActionType = ActionTypes.Process;
        Assert.Equal(["api_key"], draft.Build().Document.Secrets!);
    }

    [Fact]
    public void A_new_draft_has_the_command_schema_version()
    {
        Assert.Equal(DocumentSchema.CommandVersion, new CommandDraft().Build().Document.SchemaVersion);
    }

    private static string Serialize(CommandDocument document)
    {
        return JsonSerializer.Serialize(document, ApiDocumentJsonContext.Default.CommandDocument);
    }
}
