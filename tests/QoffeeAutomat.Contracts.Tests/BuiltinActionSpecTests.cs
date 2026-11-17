using System.Text.RegularExpressions;
using QoffeeAutomat.Contracts.Config;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Contracts.Session;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>
/// The shared definitions of the built-in actions <c>notify.toast</c> and <c>media.control</c> and of the media metrics
/// (PLAN.md S06, S11, T702, T716). Three places use them and cannot reference each other, so the definitions live in Contracts
/// and are tested here, on every system.
/// </summary>
public class BuiltinActionSpecTests
{
    [Fact]
    public void Every_parameter_name_is_a_valid_variable_name()
    {
        // The rule of PLAN.md S06 and of the manifest schema: a letter, then up to 31 letters, digits and underscores.
        foreach (ParameterDefinition parameter in NotifyToastSpec.Parameters.Concat(MediaControlSpec.Parameters))
        {
            Assert.Matches("^[A-Za-z][A-Za-z0-9_]{0,31}$", parameter.Name);
        }
    }

    [Fact]
    public void The_parameter_names_of_a_command_are_unique_ignoring_case()
    {
        string[] names = [.. NotifyToastSpec.Parameters.Select(parameter => parameter.Name.ToUpperInvariant())];

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_notification_has_a_required_message_and_pairs_of_button_parameters_that_are_optional()
    {
        Assert.Equal(["Message"], NotifyToastSpec.Parameters.Where(parameter => parameter.Required).Select(parameter => parameter.Name));
        Assert.Equal(NotifyToastSpec.MaxButtons, NotifyToastSpec.Parameters.Count(parameter => parameter.Name.StartsWith("Action", StringComparison.Ordinal) && parameter.Name.EndsWith("Id", StringComparison.Ordinal)));
        Assert.Equal("Action2Id", NotifyToastSpec.ButtonId(2));
        Assert.Equal("Action3Title", NotifyToastSpec.ButtonTitle(3));
        Assert.All(NotifyToastSpec.Parameters.Where(parameter => parameter.Type == ParameterTypes.Text), parameter => Assert.NotNull(parameter.MaxLength));
    }

    [Fact]
    public void The_picture_is_not_a_parameter_because_only_the_agent_may_add_it()
    {
        Assert.DoesNotContain(NotifyToastSpec.Parameters, parameter => parameter.Name == NotifyToastSpec.ImageData);
    }

    [Fact]
    public void The_media_action_has_one_required_choice_with_the_six_actions_of_the_plan()
    {
        ParameterDefinition action = Assert.Single(MediaControlSpec.Parameters);

        Assert.Equal("Action", action.Name);
        Assert.Equal(ParameterTypes.Choice, action.Type);
        Assert.True(action.Required);
        Assert.Equal(["play", "pause", "play_pause", "next", "previous", "stop"], action.Options);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00 }, ".gif")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x00 }, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".bmp")]
    public void A_picture_is_recognised_by_its_first_bytes(byte[] data, string extension)
    {
        Assert.Equal(extension, NotifyToastSpec.DetectImageExtension(data));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x38, 0x61, 0x00 })]
    [InlineData(new byte[] { 0x42, 0x4D })]
    public void Anything_else_is_not_a_picture(byte[] data)
    {
        Assert.Null(NotifyToastSpec.DetectImageExtension(data));
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("a:b-c.d_e", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    public void An_id_is_made_of_letters_digits_and_a_few_signs(string? id, bool valid)
    {
        Assert.Equal(valid, NotifyToastSpec.IsValidId(id));
    }

    [Fact]
    public void The_id_pattern_and_the_check_agree()
    {
        var pattern = new Regex(NotifyToastSpec.IdPattern, RegexOptions.CultureInvariant);

        foreach (string candidate in new[] { "open", "a:b-c.d_e", "has space", "semi;colon", "ümlaut", new string('a', 64), new string('a', 65) })
        {
            Assert.Equal(pattern.IsMatch(candidate), NotifyToastSpec.IsValidId(candidate));
        }
    }

    [Fact]
    public void The_media_metrics_are_off_by_default_and_the_state_is_an_enum()
    {
        MetricDescriptorView[] media =
        [
            .. SessionMetricCatalog.SessionHostSide
                .Where(descriptor => descriptor.Id.StartsWith("media.", StringComparison.Ordinal))
                .Select(descriptor => new MetricDescriptorView(descriptor.Id, descriptor.EnabledDefault, descriptor.ValueType, descriptor.Context)),
        ];

        Assert.Equal(
            ["media.state", "media.title", "media.artist", "media.album", "media.app", "media.duration", "media.position", "media.position_updated_at"],
            media.Select(entry => entry.Id));
        Assert.All(media, entry => Assert.False(entry.EnabledDefault));
        Assert.All(media, entry => Assert.Equal("user", entry.Context));
        Assert.Equal("enum", media.Single(entry => entry.Id == "media.state").ValueType);
        Assert.Equal(["playing", "paused", "idle"], SessionMetricCatalog.SessionHostSide.Single(descriptor => descriptor.Id == "media.state").Options);
        Assert.Equal(["playing", "paused", "idle"], MediaStates.All);
    }

    [Fact]
    public void The_duration_and_the_position_are_numbers_of_seconds()
    {
        foreach (string id in new[] { SessionMetricIds.MediaDuration, SessionMetricIds.MediaPosition })
        {
            MetricDescriptor descriptor = SessionMetricCatalog.SessionHostSide.Single(candidate => candidate.Id == id);

            Assert.Equal("number", descriptor.ValueType);
            Assert.Equal("s", descriptor.Unit);
            Assert.Equal("duration", descriptor.DeviceClass);
        }
    }

    [Fact]
    public void The_position_comes_with_a_timestamp_sensor_that_tells_when_it_was_valid()
    {
        MetricDescriptor descriptor = SessionMetricCatalog.SessionHostSide.Single(candidate => candidate.Id == SessionMetricIds.MediaPositionUpdatedAt);

        Assert.Equal("timestamp", descriptor.ValueType);
        Assert.Equal("timestamp", descriptor.DeviceClass);
        Assert.Equal("sensor", descriptor.Kind);
        Assert.False(descriptor.EnabledDefault);
    }

    private sealed record MetricDescriptorView(string Id, bool? EnabledDefault, string ValueType, string? Context);
}
