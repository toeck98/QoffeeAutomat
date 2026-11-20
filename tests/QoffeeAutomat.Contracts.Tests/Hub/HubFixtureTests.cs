using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QoffeeAutomat.Contracts.Hub;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Tests.Hub;

/// <summary>
/// Round trips and schema checks of every fixture in protocol/fixtures/hub (PLAN.md S14, S20, T901): each hub and
/// fleet message is read and written with the source-generated <see cref="HubJsonContext"/> and must equal the fixture.
/// </summary>
public class HubFixtureTests
{
    private const string Folder = "hub";

    private static readonly Dictionary<string, FrameCodec> Frames = new(StringComparer.Ordinal)
    {
        ["config_desired"] = FrameCodec.For(HubJsonContext.Default.ConfigDesiredMessage),
        ["config_documents"] = FrameCodec.For(HubJsonContext.Default.ConfigDocumentsMessage),
        ["config_status"] = FrameCodec.For(HubJsonContext.Default.ConfigStatusMessage),
        ["mgmt_request"] = FrameCodec.For(HubJsonContext.Default.MgmtRequestMessage),
        ["mgmt_stream_open"] = FrameCodec.For(HubJsonContext.Default.MgmtStreamOpenMessage),
        ["mgmt_stream_data"] = FrameCodec.For(HubJsonContext.Default.MgmtStreamDataMessage),
        ["mgmt_stream_close"] = FrameCodec.For(HubJsonContext.Default.MgmtStreamCloseMessage),
        ["credential_rotate"] = FrameCodec.For(HubJsonContext.Default.CredentialRotateMessage),
        ["unenroll"] = FrameCodec.For(HubJsonContext.Default.UnenrollMessage),
        ["fleet_hello"] = FrameCodec.For(HubJsonContext.Default.FleetHelloMessage),
        ["get_agents"] = FrameCodec.For(HubJsonContext.Default.GetAgentsMessage),
        ["agent_added"] = FrameCodec.For(HubJsonContext.Default.AgentAddedMessage),
        ["agent_removed"] = FrameCodec.For(HubJsonContext.Default.AgentRemovedMessage),
        ["agent_status"] = FrameCodec.For(HubJsonContext.Default.AgentStatusMessage),
        ["fleet_get_manifest"] = FrameCodec.For(HubJsonContext.Default.FleetGetManifestMessage),
        ["fleet_subscribe"] = FrameCodec.For(HubJsonContext.Default.FleetSubscribeMessage),
        ["fleet_run"] = FrameCodec.For(HubJsonContext.Default.FleetRunMessage),
        ["fleet_manifest_changed"] = FrameCodec.For(HubJsonContext.Default.FleetManifestChangedMessage),
        ["fleet_state"] = FrameCodec.For(HubJsonContext.Default.FleetStateMessage),
        ["fleet_event"] = FrameCodec.For(HubJsonContext.Default.FleetEventMessage),
    };

    private static readonly Dictionary<string, Func<JsonElement, JsonElement>> ResultData = new(StringComparer.Ordinal)
    {
        ["result_config_desired"] = data => RoundTripElement(data, HubJsonContext.Default.ConfigDesiredResultData),
        ["result_get_agents"] = data => RoundTripElement(data, HubJsonContext.Default.GetAgentsResultData),
        ["result_mgmt_request"] = data => RoundTripElement(data, HubJsonContext.Default.MgmtRequestResultData),
        ["result_mgmt_stream_open"] = data => RoundTripElement(data, HubJsonContext.Default.MgmtStreamOpenResultData),
    };

    public static TheoryData<string> FrameFixtures => ToData(ValidFixtures().Where(IsFrame));

    public static TheoryData<string> ResultFixtures => ToData(ValidFixtures().Where(IsResult));

    public static TheoryData<string> DocumentFixtures => ToData(
        ValidFixtures().Where(static f => !IsFrame(f) && !IsResult(f) && !IsEnrollmentString(f)));

    public static TheoryData<string> EnrollmentStringFixtures => ToData(ValidFixtures().Where(IsEnrollmentString));

    public static TheoryData<string> ValidFixtureData => ToData(ValidFixtures());

    public static TheoryData<string> ForwardCompatibleFixtures => ToData(RepositoryPaths.FixtureFiles(Folder + "/forward_compat"));

    public static TheoryData<string> InvalidFixtures => ToData(RepositoryPaths.FixtureFiles(Folder + "/invalid"));

    [Fact]
    public void The_hub_schemas_load()
    {
        Assert.Contains(RepositoryPaths.SchemaId("hub/message.schema.json"), RepositoryPaths.Schemas.Ids);
        Assert.Contains(RepositoryPaths.SchemaId("hub/defs.schema.json"), RepositoryPaths.Schemas.Ids);
    }

    [Fact]
    public void Every_hub_and_fleet_message_has_a_valid_fixture()
    {
        HashSet<string> present = ValidFixtures()
            .Select(RepositoryPaths.FixtureType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string type in Frames.Keys.Concat(ResultData.Keys))
        {
            Assert.Contains(type, present);
        }

        Assert.Contains("result_hub_error", present);
        Assert.Contains("enrollment_payload", present);
        Assert.Contains("enrollment_string", present);
        Assert.Contains("enroll_request", present);
        Assert.Contains("enroll_response", present);
        Assert.Contains("pair_response", present);
    }

    [Theory]
    [MemberData(nameof(ValidFixtureData))]
    public void Valid_fixture_matches_its_schemas(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(instance, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(FrameFixtures))]
    public void Frame_fixture_deserializes_to_the_expected_type_and_round_trips(string fixture)
    {
        FrameCodec codec = Frames[RepositoryPaths.FixtureType(fixture)];
        JsonElement original = RepositoryPaths.ReadFixtureElement(fixture);

        LinkMessage message = codec.Parse(RepositoryPaths.ReadFixture(fixture));

        Assert.IsType(codec.Type, message);
        Assert.Equal(original.GetProperty("t").GetString(), message.T);

        using JsonDocument reserialized = JsonDocument.Parse(codec.Serialize(message));
        Assert.Null(JsonCompare.Difference(original, reserialized.RootElement));

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(reserialized.RootElement, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(ResultFixtures))]
    public void Result_fixture_round_trips_through_the_link_codec_and_the_hub_data_types(string fixture)
    {
        JsonElement original = RepositoryPaths.ReadFixtureElement(fixture);
        string type = RepositoryPaths.FixtureType(fixture);

        ResultMessage result = Assert.IsType<ResultMessage>(LinkCodec.Parse(RepositoryPaths.ReadFixture(fixture)));
        using JsonDocument reserialized = JsonDocument.Parse(LinkCodec.Serialize(result));
        Assert.Null(JsonCompare.Difference(original, reserialized.RootElement));

        if (result.Ok)
        {
            Assert.NotNull(result.Data);
            JsonElement data = result.Data.Value;
            Assert.Null(JsonCompare.Difference(data, ResultData[type](data)));
        }
        else
        {
            Assert.NotNull(result.Error);
            Assert.True(HubErrorCodes.All.Contains(result.Error.Code), result.Error.Code);
        }

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(reserialized.RootElement, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(DocumentFixtures))]
    public void Enrollment_document_fixture_round_trips(string fixture)
    {
        JsonElement original = RepositoryPaths.ReadFixtureElement(fixture);
        string json = RepositoryPaths.ReadFixture(fixture);

        JsonElement reserialized = RepositoryPaths.FixtureType(fixture) switch
        {
            "enrollment_payload" => RoundTripElement(original, HubJsonContext.Default.EnrollmentPayload),
            "enroll_request" => RoundTripElement(original, HubJsonContext.Default.EnrollRequest),
            "enroll_response" => RoundTripElement(original, HubJsonContext.Default.EnrollResponse),
            "pair_response" => RoundTripElement(original, HubJsonContext.Default.FleetPairResponse),
            _ => throw new InvalidOperationException("No codec for fixture " + fixture + ": " + json),
        };

        Assert.Null(JsonCompare.Difference(original, reserialized));
        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(reserialized, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(EnrollmentStringFixtures))]
    public void Enrollment_string_decodes_to_the_payload_fixture_and_survives_a_round_trip(string fixture)
    {
        string text = RepositoryPaths.ReadFixtureElement(fixture).GetString()!;
        string payloadFixture = fixture.Replace("enrollment_string", "enrollment_payload", StringComparison.Ordinal);
        EnrollmentPayload expected = JsonSerializer.Deserialize(
            RepositoryPaths.ReadFixture(payloadFixture),
            HubJsonContext.Default.EnrollmentPayload)!;

        EnrollmentPayload decoded = EnrollmentString.Decode(text);

        Assert.Equal(expected, decoded);
        Assert.Equal(expected, EnrollmentString.Decode(EnrollmentString.Encode(decoded)));
        Assert.True(EnrollmentString.TryDecode(text, out EnrollmentPayload? viaTry));
        Assert.Equal(expected, viaTry);
    }

    [Fact]
    public void Enrollment_string_with_the_wrong_prefix_is_rejected()
    {
        Assert.False(EnrollmentString.TryDecode("qahub2.e30", out _));
        Assert.False(EnrollmentString.TryDecode("e30", out _));
        Assert.False(EnrollmentString.TryDecode(null, out _));
    }

    [Fact]
    public void The_documents_of_config_documents_fixtures_carry_correct_hashes()
    {
        foreach (string fixture in new[] { "hub/config_documents.json", "hub/config_documents.partial.json" })
        {
            ConfigDocumentsMessage message = JsonSerializer.Deserialize(
                RepositoryPaths.ReadFixture(fixture),
                HubJsonContext.Default.ConfigDocumentsMessage)!;

            Assert.NotEmpty(message.Documents);
            foreach (HubDocument document in message.Documents)
            {
                Assert.True(DocumentHasher.Matches(document), fixture + ": " + document.Kind + "/" + document.Id);
            }
        }
    }

    [Fact]
    public void The_item_hashes_of_config_desired_match_the_documents_fixture()
    {
        ConfigDesiredMessage desired = JsonSerializer.Deserialize(
            RepositoryPaths.ReadFixture("hub/config_desired.json"),
            HubJsonContext.Default.ConfigDesiredMessage)!;
        ConfigDocumentsMessage documents = JsonSerializer.Deserialize(
            RepositoryPaths.ReadFixture("hub/config_documents.json"),
            HubJsonContext.Default.ConfigDocumentsMessage)!;

        foreach (HubDocument document in documents.Documents)
        {
            DesiredItem item = Assert.Single(desired.Items, i => i.Kind == document.Kind && i.Id == document.Id);
            Assert.Equal(DocumentHasher.Hash(document), item.Hash);
        }
    }

    [Fact]
    public void The_items_of_config_desired_are_sorted_in_ordinal_order()
    {
        ConfigDesiredMessage desired = JsonSerializer.Deserialize(
            RepositoryPaths.ReadFixture("hub/config_desired.json"),
            HubJsonContext.Default.ConfigDesiredMessage)!;

        Assert.Equal(DesiredState.SortItems(desired.Items), desired.Items);
    }

    [Theory]
    [MemberData(nameof(ForwardCompatibleFixtures))]
    public void Forward_compatible_fixture_validates_and_is_read(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(instance, schemaId));
        }

        string type = RepositoryPaths.FixtureType(fixture);
        if (Frames.TryGetValue(type, out FrameCodec? codec))
        {
            Assert.IsType(codec.Type, codec.Parse(RepositoryPaths.ReadFixture(fixture)));
        }
        else
        {
            Assert.IsType<UnknownMessage>(LinkCodec.Parse(RepositoryPaths.ReadFixture(fixture)));
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_fixture_is_rejected_by_the_schemas(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        Assert.Contains(SchemasFor(fixture), schemaId => RepositoryPaths.Schemas.Validate(instance, schemaId).Count > 0);
    }

    [Fact]
    public void The_hub_error_codes_are_the_ones_of_the_plan()
    {
        string[] expected = ["agent_offline", "forbidden", "revoked", "unknown_agent"];
        Assert.Equal(expected, HubErrorCodes.All.Order(StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<string> ValidFixtures() => RepositoryPaths.FixtureFiles(Folder);

    private static bool IsFrame(string fixture) => Frames.ContainsKey(RepositoryPaths.FixtureType(fixture));

    private static bool IsResult(string fixture) =>
        RepositoryPaths.FixtureType(fixture).StartsWith("result_", StringComparison.Ordinal);

    private static bool IsEnrollmentString(string fixture) =>
        RepositoryPaths.FixtureType(fixture) == "enrollment_string";

    private static TheoryData<string> ToData(IEnumerable<string> items)
    {
        var data = new TheoryData<string>();
        foreach (string item in items)
        {
            data.Add(item);
        }

        return data;
    }

    private static List<string> SchemasFor(string fixture)
    {
        string type = RepositoryPaths.FixtureType(fixture);
        var schemas = new List<string> { RepositoryPaths.SchemaId("hub/" + type + ".schema.json") };
        if (Frames.ContainsKey(type))
        {
            schemas.Add(RepositoryPaths.SchemaId("hub/message.schema.json"));
        }

        return schemas;
    }

    private static JsonElement RoundTripElement<T>(JsonElement element, JsonTypeInfo<T> typeInfo)
    {
        T value = JsonSerializer.Deserialize(element, typeInfo)!;
        return JsonSerializer.SerializeToElement(value, typeInfo);
    }

    private sealed record FrameCodec(Type Type, Func<string, LinkMessage> Parse, Func<LinkMessage, string> Serialize)
    {
        public static FrameCodec For<T>(JsonTypeInfo<T> typeInfo)
            where T : LinkMessage
        {
            return new FrameCodec(
                typeof(T),
                json => JsonSerializer.Deserialize(json, typeInfo)!,
                message => JsonSerializer.Serialize((T)message, typeInfo));
        }
    }
}
