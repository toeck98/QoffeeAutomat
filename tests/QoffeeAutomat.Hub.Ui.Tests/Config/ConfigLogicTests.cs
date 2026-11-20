using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Hub.Config;
using QoffeeAutomat.Hub.Metrics;
using QoffeeAutomat.Hub.Ui.Pages.Library;
using QoffeeAutomat.Hub.Ui.Pages.Metrics;
using QoffeeAutomat.Hub.Ui.Pages.Profiles;
using QoffeeAutomat.Hub.Ui.Tests.Support;

namespace QoffeeAutomat.Hub.Ui.Tests.Config;

/// <summary>The logic behind the library, profile, assignment and metric pages (PLAN.md S20, T916), tested without a page.</summary>
[Trait("Category", "Config")]
public sealed class ConfigLogicTests
{
    // Library kinds ----------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DocumentKinds.Command, false)]
    [InlineData(DocumentKinds.Sensor, false)]
    [InlineData(DocumentKinds.Script, true)]
    [InlineData(DocumentKinds.Mode, true)]
    [InlineData(DocumentKinds.MetricOverrides, true)]
    [InlineData(DocumentKinds.Variables, true)]
    public void Only_commands_and_sensors_are_edited_with_the_forms_of_the_component_library(string kind, bool textEditor)
    {
        Assert.Equal(textEditor, LibraryKinds.UsesTextEditor(kind));
    }

    [Fact]
    public void Every_kind_of_the_protocol_is_in_the_library_pages()
    {
        Assert.Equal(DocumentKinds.All.Order(StringComparer.Ordinal), LibraryKinds.All.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(DocumentKinds.Command, "lock_screen", "library/command/lock_screen")]
    [InlineData(DocumentKinds.Sensor, "up time", "library/sensor/up%20time")]
    [InlineData(DocumentKinds.Script, "backup.ps1", "library/document/script/backup.ps1")]
    [InlineData(DocumentKinds.MetricOverrides, "overrides", "library/document/metric_overrides/overrides")]
    public void The_editor_of_a_document_has_an_address_below_the_base_address_of_the_app(string kind, string id, string expected)
    {
        Assert.Equal(expected, LibraryKinds.EditHref(kind, id));
    }

    [Theory]
    [InlineData(DocumentKinds.Command, "library/command/new")]
    [InlineData(DocumentKinds.Sensor, "library/sensor/new")]
    [InlineData(DocumentKinds.Variables, "library/document/variables/new")]
    public void A_new_document_has_its_own_address(string kind, string expected)
    {
        Assert.Equal(expected, LibraryKinds.NewHref(kind));
    }

    [Fact]
    public void The_text_of_a_script_is_the_json_string_that_the_hub_stores()
    {
        Assert.True(LibraryKinds.TryParse(DocumentKinds.Script, "line 1\nline \"2\"", out JsonElement body, out string? error));

        Assert.Null(error);
        Assert.Equal(JsonValueKind.String, body.ValueKind);
        Assert.Equal("line 1\nline \"2\"", body.GetString());
        Assert.Equal("line 1\nline \"2\"", LibraryKinds.ToText(DocumentKinds.Script, body));
    }

    [Fact]
    public void The_text_of_a_json_document_is_parsed_and_shown_indented()
    {
        Assert.True(LibraryKinds.TryParse(DocumentKinds.Variables, "{\"SITE\":\"berlin\"}", out JsonElement body, out _));

        string text = LibraryKinds.ToText(DocumentKinds.Variables, body);

        Assert.Equal("berlin", body.GetProperty("SITE").GetString());
        Assert.Contains("\n", text, StringComparison.Ordinal);
        Assert.Contains("\"SITE\": \"berlin\"", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void Text_that_is_not_a_json_object_is_refused_before_anything_is_sent(string text)
    {
        Assert.False(LibraryKinds.TryParse(DocumentKinds.Mode, text, out _, out string? error));

        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(DocumentKinds.Script, "")]
    [InlineData(DocumentKinds.Variables, "{}")]
    [InlineData(DocumentKinds.Mode, "{}")]
    public void A_new_document_starts_with_an_empty_text_for_a_script_and_an_empty_object_for_the_rest(string kind, string expected)
    {
        Assert.Equal(expected, LibraryKinds.NewText(kind));
    }

    [Theory]
    [InlineData("lock_screen", true)]
    [InlineData("a", true)]
    [InlineData("a1_b2", true)]
    [InlineData("", false)]
    [InlineData("1abc", false)]
    [InlineData("Upper", false)]
    [InlineData("with-dash", false)]
    [InlineData("with space", false)]
    public void An_id_follows_the_rule_of_the_hub_for_documents_and_profiles(string id, bool valid)
    {
        Assert.Equal(valid, LibraryKinds.IsValidDocumentId(id));
    }

    [Fact]
    public void An_id_of_64_characters_is_too_long()
    {
        Assert.True(LibraryKinds.IsValidDocumentId("a" + new string('b', 62)));
        Assert.False(LibraryKinds.IsValidDocumentId("a" + new string('b', 63)));
    }

    // Profile draft ----------------------------------------------------------------------------------------------------------------------

    private static ProfileDraft Draft()
    {
        ProfileInfo profile = Samples.Profile("office", "Office", 5, 2, new ProfileItemRef(DocumentKinds.Command, "lock_screen", 2), new ProfileItemRef(DocumentKinds.Script, "backup.ps1", 1));
        ProfileContent content = profile.Draft with { Variables = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" }, OsFilter = "windows" };
        return ProfileDraft.From(profile.Id, content);
    }

    [Fact]
    public void A_draft_takes_what_the_hub_returned_and_gives_it_back_as_the_request_that_replaces_the_draft()
    {
        ProfileDraft draft = Draft();

        ProfileDraftRequest request = draft.ToRequest();

        Assert.Null(request.Id);
        Assert.Equal("Office", request.Name);
        Assert.Equal(5, request.Priority);
        Assert.Equal("windows", request.OsFilter);
        Assert.Equal(["command:lock_screen@2", "script:backup.ps1@1"], request.Items!.Select(item => item.Kind + ":" + item.Id + "@" + item.Revision).ToArray());
        Assert.Equal("1", request.Variables!["A"]);
        Assert.Equal("2", request.Variables["B"]);
        Assert.Equal("inherit", request.Policy!.LocalConfig);
        Assert.Equal("inherit", request.Policy.DirectLink);
    }

    [Fact]
    public void An_empty_os_filter_is_sent_as_none()
    {
        ProfileDraft draft = Draft();
        draft.OsFilter = string.Empty;

        Assert.Null(draft.ToRequest().OsFilter);
    }

    [Fact]
    public void The_variables_of_a_draft_are_sorted_by_name_when_they_are_loaded()
    {
        ProfileDraft draft = Draft();

        Assert.Equal(["A", "B"], draft.Variables.Select(row => row.Name).ToArray());
    }

    [Fact]
    public void A_draft_that_was_not_changed_has_the_snapshot_it_was_loaded_with_and_any_change_alters_it()
    {
        ProfileDraft draft = Draft();
        string loaded = draft.Snapshot();

        Assert.Equal(loaded, Draft().Snapshot());

        draft.Name = "Office 2";
        Assert.NotEqual(loaded, draft.Snapshot());
        draft.Name = "Office";
        Assert.Equal(loaded, draft.Snapshot());

        draft.Items.Add(new ProfileItemRow(DocumentKinds.Sensor, "explorer", null));
        Assert.NotEqual(loaded, draft.Snapshot());
        draft.Items.RemoveAt(draft.Items.Count - 1);
        draft.Variables[0].Value = "changed";
        Assert.NotEqual(loaded, draft.Snapshot());
        draft.Variables[0].Value = "1";
        draft.LocalConfig = "locked";
        Assert.NotEqual(loaded, draft.Snapshot());
        draft.LocalConfig = "inherit";
        draft.Priority = 6;
        Assert.NotEqual(loaded, draft.Snapshot());
        draft.Priority = 5;
        Assert.Equal(loaded, draft.Snapshot());
    }

    [Fact]
    public void A_draft_without_problems_has_none_and_the_problems_are_named_by_text_keys()
    {
        ProfileDraft draft = Draft();
        Assert.Empty(draft.Problems());

        draft.Name = "  ";
        draft.Priority = 20_000;
        draft.Variables.Add(new VariableRow { Name = "1bad", Value = "x" });
        draft.Variables.Add(new VariableRow { Name = "A", Value = "again" });
        draft.Items.Add(new ProfileItemRow(DocumentKinds.MetricOverrides, "one", null));
        draft.Items.Add(new ProfileItemRow(DocumentKinds.MetricOverrides, "two", null));

        IReadOnlyList<string> problems = draft.Problems();

        Assert.Contains("Profile.Problem.Name", problems);
        Assert.Contains("Profile.Problem.Priority", problems);
        Assert.Contains("Profile.Problem.VariableName", problems);
        Assert.Contains("Profile.Problem.VariableDuplicate", problems);
        Assert.Contains("Profile.Problem.MetricOverrides", problems);
    }

    [Fact]
    public void A_new_profile_starts_with_a_name_and_the_defaults()
    {
        ProfileDraftRequest request = ProfileDraft.ToCreateRequest("office", "  Office ");

        Assert.Equal("office", request.Id);
        Assert.Equal("Office", request.Name);
        Assert.Equal(0, request.Priority);
        Assert.Null(request.OsFilter);
        Assert.Empty(request.Items!);
        Assert.Empty(request.Variables!);
        Assert.Equal("inherit", request.Policy!.LocalConfig);
        Assert.Equal("inherit", request.Policy.DirectLink);
    }

    [Fact]
    public void The_documents_of_a_profile_are_found_by_kind_and_id()
    {
        ProfileDraft draft = Draft();

        Assert.True(draft.Contains(DocumentKinds.Command, "lock_screen"));
        Assert.False(draft.Contains(DocumentKinds.Sensor, "lock_screen"));
    }

    [Fact]
    public void The_policy_choices_are_the_ones_the_hub_accepts_and_inherit_comes_first()
    {
        Assert.Equal(["inherit", "allow", "locked"], ProfileDraft.LocalConfigChoices.ToArray());
        Assert.Equal(["inherit", "allow", "deny"], ProfileDraft.DirectLinkChoices.ToArray());
        Assert.Equal([string.Empty, "windows", "linux"], ProfileDraft.OsFilters.ToArray());
    }

    // Assignment targets -----------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("all", null, "all")]
    [InlineData("all", "ignored", "all")]
    [InlineData("tag", " Lab ", "tag:lab")]
    [InlineData("agent", "pc-1", "agent:pc-1")]
    public void The_target_text_is_built_the_way_the_hub_reads_it(string kind, string? value, string expected)
    {
        Assert.Equal(expected, AssignmentTargets.Build(kind, value));
    }

    [Theory]
    [InlineData("tag", null)]
    [InlineData("tag", " ")]
    [InlineData("agent", "")]
    [InlineData("other", "x")]
    public void A_target_that_needs_a_value_has_no_text_without_one(string kind, string? value)
    {
        Assert.Null(AssignmentTargets.Build(kind, value));
    }

    [Theory]
    [InlineData("all", "all", "")]
    [InlineData("tag:lab", "tag", "lab")]
    [InlineData("agent:pc-1", "agent", "pc-1")]
    public void A_target_text_is_split_into_kind_and_value(string target, string kind, string value)
    {
        Assert.Equal((kind, value), AssignmentTargets.Split(target));
    }

    [Fact]
    public void Targets_are_ordered_from_the_least_to_the_most_specific()
    {
        string[] ordered = ["agent:pc-1", "tag:lab", "all"];

        Assert.Equal(["all", "tag:lab", "agent:pc-1"], ordered.OrderBy(AssignmentTargets.Specificity).ToArray());
    }

    // Metric chart -----------------------------------------------------------------------------------------------------------------------

    private static MetricQueryResult Result(params MetricSeriesData[] series)
    {
        return new MetricQueryResult("raw", Samples.Now.AddHours(-6), Samples.Now, series);
    }

    [Fact]
    public void Two_agents_share_one_value_axis_and_one_time_axis()
    {
        MetricQueryResult result = Result(
            Samples.Series("pc-1", "cpu.total", "%", (300, 10), (60, 30)),
            Samples.Series("pc-2", "cpu.total", "%", (300, 50), (60, 90)));

        MetricChartModel chart = MetricChartModel.Build(result, "cpu.total", [("pc-1", "PC 1"), ("pc-2", "PC 2")]);

        Assert.Equal(10, chart.Minimum);
        Assert.Equal(90, chart.Maximum);
        Assert.Equal("%", chart.Unit);
        Assert.Equal("raw", chart.Resolution);
        Assert.True(chart.HasLines);
        Assert.Equal(["PC 1", "PC 2"], chart.Series.Select(line => line.AgentName).ToArray());
        Assert.Equal([0, 1], chart.Series.Select(line => line.Slot).ToArray());

        // The lowest value of all lines is at the bottom and the highest at the top: y runs downwards in SVG, so 38 (bottom) and 2 (top).
        double[] firstY = [.. chart.Series[0].Points.Split(' ').Select(point => double.Parse(point.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture))];
        double[] secondY = [.. chart.Series[1].Points.Split(' ').Select(point => double.Parse(point.Split(',')[1], System.Globalization.CultureInfo.InvariantCulture))];
        Assert.Equal(38, firstY[0]);
        Assert.Equal(2, secondY[1]);
        Assert.True(secondY[0] < firstY[1], "50 is above 30 on the shared axis");
    }

    [Fact]
    public void A_point_is_placed_on_the_time_axis_by_when_it_was_taken()
    {
        MetricQueryResult result = Result(Samples.Series("pc-1", "cpu.total", "%", (360, 1), (180, 2), (0, 3)));

        MetricChartModel chart = MetricChartModel.Build(result, "cpu.total", [("pc-1", "PC 1")]);

        string[] xs = [.. chart.Series[0].Points.Split(' ').Select(point => point.Split(',')[0])];
        Assert.Equal(["0", "50", "100"], xs);
    }

    [Fact]
    public void The_table_numbers_of_a_line_are_its_latest_minimum_average_and_maximum()
    {
        MetricQueryResult result = Result(Samples.Series("pc-1", "cpu.total", "%", (300, 10), (200, 20), (100, 60)));

        ChartSeries line = MetricChartModel.Build(result, "cpu.total", [("pc-1", "PC 1")]).Series[0];

        Assert.Equal(60, line.Latest);
        Assert.Equal(10, line.Minimum);
        Assert.Equal(60, line.Maximum);
        Assert.Equal(30, line.Average);
        Assert.Equal(3, line.PointCount);
    }

    [Fact]
    public void Rolled_up_points_are_drawn_by_their_average_and_weighted_by_their_count()
    {
        var series = new MetricSeriesData(
            "pc-1",
            "cpu.total",
            "number",
            "%",
            "5m",
            [
                new MetricPoint(Samples.Now.AddHours(-2), null, 0, 10, 20, 1, null),
                new MetricPoint(Samples.Now.AddHours(-1), null, 20, 40, 80, 3, null),
            ],
            false);

        ChartSeries line = MetricChartModel.Build(new MetricQueryResult("5m", Samples.Now.AddHours(-6), Samples.Now, [series]), "cpu.total", [("pc-1", "PC 1")]).Series[0];

        Assert.Equal(40, line.Latest);
        Assert.Equal(0, line.Minimum);
        Assert.Equal(80, line.Maximum);
        Assert.Equal(32.5, line.Average);
    }

    [Fact]
    public void An_agent_without_values_still_has_an_empty_line_for_the_legend_and_the_table()
    {
        MetricQueryResult result = Result(Samples.Series("pc-1", "cpu.total", "%", (300, 10), (60, 30)));

        MetricChartModel chart = MetricChartModel.Build(result, "cpu.total", [("pc-1", "PC 1"), ("pc-2", "PC 2")]);

        ChartSeries empty = chart.Series[1];
        Assert.Equal("PC 2", empty.AgentName);
        Assert.Equal(string.Empty, empty.Points);
        Assert.Null(empty.Latest);
        Assert.Equal(0, empty.PointCount);
    }

    [Fact]
    public void A_line_with_one_point_has_numbers_but_nothing_to_draw()
    {
        MetricChartModel chart = MetricChartModel.Build(Result(Samples.Series("pc-1", "cpu.total", "%", (60, 30))), "cpu.total", [("pc-1", "PC 1")]);

        Assert.False(chart.HasLines);
        Assert.Equal(30, chart.Series[0].Latest);
    }

    [Fact]
    public void A_result_without_any_value_gives_an_empty_chart_and_does_not_fail()
    {
        MetricChartModel chart = MetricChartModel.Build(Result(), "cpu.total", [("pc-1", "PC 1")]);

        Assert.False(chart.HasLines);
        Assert.Equal(0, chart.Minimum);
        Assert.Equal(0, chart.Maximum);
    }

    [Fact]
    public void Equal_values_are_drawn_in_the_middle_of_the_chart()
    {
        MetricChartModel chart = MetricChartModel.Build(Result(Samples.Series("pc-1", "cpu.total", "%", (120, 5), (60, 5))), "cpu.total", [("pc-1", "PC 1")]);

        Assert.All(chart.Series[0].Points.Split(' '), point => Assert.EndsWith(",20", point, StringComparison.Ordinal));
    }

    [Fact]
    public void A_truncated_series_is_marked()
    {
        MetricSeriesData cut = Samples.Series("pc-1", "cpu.total", "%", (120, 5), (60, 6)) with { Truncated = true };

        MetricChartModel chart = MetricChartModel.Build(Result(cut), "cpu.total", [("pc-1", "PC 1")]);

        Assert.True(chart.Series[0].Truncated);
    }

    [Fact]
    public void Only_the_series_of_the_asked_metric_is_drawn()
    {
        MetricQueryResult result = Result(
            Samples.Series("pc-1", "cpu.total", "%", (120, 5), (60, 6)),
            Samples.Series("pc-1", "memory.used", "MB", (120, 500), (60, 600)));

        MetricChartModel chart = MetricChartModel.Build(result, "memory.used", [("pc-1", "PC 1")]);

        Assert.Equal("MB", chart.Unit);
        Assert.Equal(500, chart.Minimum);
        Assert.Equal(600, chart.Maximum);
    }

    [Theory]
    [InlineData("1h", 1)]
    [InlineData("6h", 6)]
    [InlineData("24h", 24)]
    [InlineData("7d", 168)]
    [InlineData("30d", 720)]
    [InlineData("unknown", 6)]
    public void The_ranges_have_the_lengths_they_say_and_an_unknown_one_is_the_default(string key, double hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), MetricRanges.LengthOf(key));
    }
}
