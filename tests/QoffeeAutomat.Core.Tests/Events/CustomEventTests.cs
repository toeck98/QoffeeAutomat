using System.Text.Json;
using QoffeeAutomat.Core.Events;

namespace QoffeeAutomat.Core.Tests.Events;

/// <summary>Validation and shape of the custom events that <c>emit</c> raises.</summary>
[Trait("Category", "Events")]
public sealed class CustomEventTests
{
    [Theory]
    [InlineData("backup_done")]
    [InlineData("a")]
    [InlineData("Build.finished-ok_2")]
    public void Plain_names_are_accepted(string name)
    {
        Assert.True(CustomEvents.IsValidName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("new\nline")]
    [InlineData("slash/name")]
    [InlineData("ümlaut")]
    public void Other_names_are_rejected(string? name)
    {
        Assert.False(CustomEvents.IsValidName(name));
        Assert.False(CustomEvents.Create(name, null).IsValid);
    }

    [Fact]
    public void A_name_can_have_at_most_64_characters()
    {
        Assert.True(CustomEvents.IsValidName(new string('a', 64)));
        Assert.False(CustomEvents.IsValidName(new string('a', 65)));
    }

    [Fact]
    public void The_payload_carries_the_name_and_the_data()
    {
        CustomEventResult result = CustomEvents.Create("backup_done", EventTestSupport.Json("""{"files":12,"ok":true}"""));

        Assert.True(result.IsValid);
        JsonElement payload = result.Payload!.Value;
        Assert.Equal("backup_done", payload.GetProperty("name").GetString());
        Assert.Equal(12, payload.GetProperty("data").GetProperty("files").GetInt32());
        Assert.True(payload.GetProperty("data").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Without_data_the_payload_has_only_the_name()
    {
        CustomEventResult result = CustomEvents.Create("ping", null);

        Assert.True(result.IsValid);
        Assert.False(result.Payload!.Value.TryGetProperty("data", out _));
    }

    [Fact]
    public void A_json_null_counts_as_no_data()
    {
        CustomEventResult result = CustomEvents.Create("ping", EventTestSupport.Json("null"));

        Assert.True(result.IsValid);
        Assert.False(result.Payload!.Value.TryGetProperty("data", out _));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void The_data_must_be_an_object(string json)
    {
        CustomEventResult result = CustomEvents.Create("ping", EventTestSupport.Json(json));

        Assert.False(result.IsValid);
        Assert.Null(result.Payload);
        Assert.Contains("object", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_data_is_limited_in_size()
    {
        string big = "{\"text\":\"" + new string('x', CustomEvents.MaxDataBytes) + "\"}";

        CustomEventResult result = CustomEvents.Create("ping", EventTestSupport.Json(big));

        Assert.False(result.IsValid);
        Assert.Contains("bytes", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Quotes_and_control_characters_in_the_data_stay_inside_their_string()
    {
        CustomEventResult result = CustomEvents.Create(
            "ping",
            EventTestSupport.Json("""{"text":"a\"b\\c\nd"}"""));

        Assert.True(result.IsValid);
        Assert.Equal("a\"b\\c\nd", result.Payload!.Value.GetProperty("data").GetProperty("text").GetString());
    }

    [Fact]
    public void Event_data_builder_omits_null_members_and_escapes_text()
    {
        JsonElement data = EventData.Create(("a", "x\"y"), ("b", null), ("c", 5L), ("d", 1.5), ("e", true));

        Assert.Equal("x\"y", data.GetProperty("a").GetString());
        Assert.False(data.TryGetProperty("b", out _));
        Assert.Equal(5L, data.GetProperty("c").GetInt64());
        Assert.Equal(1.5, data.GetProperty("d").GetDouble());
        Assert.True(data.GetProperty("e").GetBoolean());
    }
}
