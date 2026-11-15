using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Tests;

public class LinkCodecTests
{
    [Fact]
    public void Serialized_frames_start_with_the_message_type()
    {
        string json = LinkCodec.Serialize(new GetManifestMessage(1));

        Assert.StartsWith("{\"t\":\"get_manifest\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Member_names_are_snake_case()
    {
        var hello = new HelloMessage(
            new ProtocolRange(1, 1),
            new AgentInfo("id", "Name", "1.0.0", "linux", "x64", "host", []),
            "143ffa0cd221",
            "boot");

        string json = LinkCodec.Serialize(hello);

        Assert.Contains("\"manifest_rev\"", json, StringComparison.Ordinal);
        Assert.Contains("\"boot_id\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"features\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ManifestRev", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Frames_parse_from_utf8_bytes()
    {
        byte[] frame = Encoding.UTF8.GetBytes("""{"t":"bye","reason":"sleep"}""");

        ByeMessage bye = Assert.IsType<ByeMessage>(LinkCodec.Parse(frame));

        Assert.Equal(ByeReasons.Sleep, bye.Reason);
        Assert.Equal(frame, LinkCodec.SerializeToUtf8(bye));
    }

    [Fact]
    public void A_frame_without_t_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"id":1}"""));
    }

    [Fact]
    public void A_frame_whose_t_is_not_a_string_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"t":5}"""));
    }

    [Fact]
    public void A_frame_that_is_not_an_object_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""["hello"]"""));
    }

    [Fact]
    public void Text_that_is_not_json_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("{"));
    }

    [Fact]
    public void A_mandatory_member_that_is_missing_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"t":"manifest_changed"}"""));
    }

    [Fact]
    public void A_mandatory_member_that_is_null_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"t":"manifest_changed","rev":null}"""));
    }

    [Fact]
    public void A_member_of_the_wrong_type_is_rejected()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"t":"get_manifest","id":"one"}"""));
    }

    [Fact]
    public void Subscribe_accepts_a_list_and_the_wildcard()
    {
        SubscribeMessage list = Assert.IsType<SubscribeMessage>(LinkCodec.Parse("""{"t":"subscribe","id":1,"metrics":["a.b","c.d"]}"""));
        SubscribeMessage all = Assert.IsType<SubscribeMessage>(LinkCodec.Parse("""{"t":"subscribe","id":2,"metrics":"*"}"""));

        string[] expectedIds = ["a.b", "c.d"];
        Assert.False(list.Metrics.All);
        Assert.Equal(expectedIds, list.Metrics.Ids);
        Assert.True(all.Metrics.All);
        Assert.Empty(all.Metrics.Ids);
    }

    [Fact]
    public void Subscribe_rejects_a_plain_string_other_than_the_wildcard()
    {
        Assert.ThrowsAny<JsonException>(() => LinkCodec.Parse("""{"t":"subscribe","id":1,"metrics":"cpu.usage"}"""));
    }

    [Fact]
    public void Subscribe_writes_the_wildcard_as_a_string_and_a_list_as_an_array()
    {
        string all = LinkCodec.Serialize(new SubscribeMessage(1, MetricSelection.Everything));
        string list = LinkCodec.Serialize(new SubscribeMessage(2, MetricSelection.From(["a.b"]), new Dictionary<string, double> { ["a.b"] = 2.5 }));

        Assert.Contains("\"metrics\":\"*\"", all, StringComparison.Ordinal);
        Assert.Contains("\"metrics\":[\"a.b\"]", list, StringComparison.Ordinal);
        Assert.Contains("\"intervals\":{\"a.b\":2.5}", list, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_waits_by_default()
    {
        RunMessage minimal = Assert.IsType<RunMessage>(LinkCodec.Parse("""{"t":"run","id":1,"command":"lock_session"}"""));
        RunMessage nowait = Assert.IsType<RunMessage>(LinkCodec.Parse("""{"t":"run","id":2,"command":"lock_session","wait":false}"""));

        Assert.Null(minimal.Wait);
        Assert.True(minimal.ShouldWait);
        Assert.False(nowait.ShouldWait);
        Assert.Null(minimal.Vars);
    }

    [Fact]
    public void State_keeps_null_values_as_unavailable()
    {
        StateMessage state = Assert.IsType<StateMessage>(LinkCodec.Parse("""{"t":"state","ts":1,"v":{"a.b":null,"c.d":3}}"""));

        Assert.Equal(JsonValueKind.Null, state.V["a.b"].ValueKind);
        Assert.Equal(3, state.V["c.d"].GetInt32());
        Assert.Contains("\"a.b\":null", LinkCodec.Serialize(state), StringComparison.Ordinal);
    }

    [Fact]
    public void Result_factories_create_the_documented_shapes()
    {
        using JsonDocument data = JsonDocument.Parse("{}");

        string success = LinkCodec.Serialize(ResultMessage.Success(7, data.RootElement));
        string failure = LinkCodec.Serialize(ResultMessage.Failure(8, ErrorCodes.UnknownCommand, "nope"));

        Assert.Equal("""{"t":"result","id":7,"ok":true,"data":{}}""", success);
        Assert.Equal("""{"t":"result","id":8,"ok":false,"error":{"code":"unknown_command","message":"nope"}}""", failure);
    }

    [Fact]
    public void Run_result_data_converts_to_and_from_json()
    {
        var data = new RunResultData("run-1", 0, "out", string.Empty, 12, false);

        RunResultData roundTripped = LinkCodec.ParseRunResult(LinkCodec.ToElement(data));

        Assert.Equal(data, roundTripped);
    }

    [Fact]
    public void The_manifest_converts_to_and_from_a_json_element()
    {
        Manifest manifest = LinkCodec.ParseManifest(RepositoryPaths.ReadFixture("manifest.json"));

        Manifest again = LinkCodec.ParseManifest(LinkCodec.ToElement(manifest));

        Assert.Equal(manifest.Rev, again.Rev);
        Assert.Equal(manifest.Commands.Count, again.Commands.Count);
    }

    [Fact]
    public void Error_codes_match_the_documentation()
    {
        string readme = File.ReadAllText(Path.Combine(RepositoryPaths.ProtocolDirectory, "README.md"));

        Assert.Equal(14, ErrorCodes.All.Count);
        foreach (string code in ErrorCodes.All)
        {
            Assert.Contains($"| `{code}` |", readme, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Protocol_constants_have_their_documented_values()
    {
        Assert.Equal(1, ProtocolInfo.Version);
        Assert.Equal("qoffeelink.v1", ProtocolInfo.SubProtocol);
        Assert.Equal(4406, CloseCodes.UnsupportedVersion);
        Assert.Equal(4400, CloseCodes.BadRequest);
        Assert.Equal(4401, CloseCodes.Unauthorized);
        Assert.Equal(1_048_576, ProtocolInfo.MaxFrameBytes);
    }
}
