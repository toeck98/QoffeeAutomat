using System.Text.Json;
using QoffeeAutomat.Contracts.Link;

namespace QoffeeAutomat.Contracts.Tests;

/// <summary>Round trips and schema checks of every fixture in protocol/fixtures (PLAN.md S14, T102).</summary>
public class LinkFixtureTests
{
    private static readonly Dictionary<string, Type> MessageTypesByName = new(StringComparer.Ordinal)
    {
        [MessageTypes.Hello] = typeof(HelloMessage),
        [MessageTypes.ClientHello] = typeof(ClientHelloMessage),
        [MessageTypes.GetManifest] = typeof(GetManifestMessage),
        [MessageTypes.ManifestChanged] = typeof(ManifestChangedMessage),
        [MessageTypes.Subscribe] = typeof(SubscribeMessage),
        [MessageTypes.State] = typeof(StateMessage),
        [MessageTypes.Run] = typeof(RunMessage),
        [MessageTypes.Event] = typeof(EventMessage),
        [MessageTypes.Bye] = typeof(ByeMessage),
        [MessageTypes.Result] = typeof(ResultMessage),
    };

    public static TheoryData<string> ValidFixtures => ToData(RepositoryPaths.FixtureFiles(string.Empty));

    public static TheoryData<string> ValidMessageFixtures =>
        ToData(RepositoryPaths.FixtureFiles(string.Empty).Where(IsMessage));

    public static TheoryData<string> ForwardCompatibleFixtures => ToData(RepositoryPaths.FixtureFiles("forward_compat"));

    public static TheoryData<string> InvalidFixtures => ToData(RepositoryPaths.FixtureFiles("invalid"));

    [Fact]
    public void Every_message_type_has_a_valid_fixture()
    {
        HashSet<string> present = RepositoryPaths.FixtureFiles(string.Empty)
            .Select(RepositoryPaths.FixtureType)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string type in MessageTypesByName.Keys)
        {
            Assert.Contains(type, present);
        }

        Assert.Contains("manifest", present);
        Assert.Contains("pair_request", present);
        Assert.Contains("pair_response", present);
    }

    [Fact]
    public void The_schemas_load_and_use_only_supported_keywords()
    {
        Assert.Contains(RepositoryPaths.SchemaId("message.schema.json"), RepositoryPaths.Schemas.Ids);
        Assert.Contains(RepositoryPaths.SchemaId("manifest.schema.json"), RepositoryPaths.Schemas.Ids);
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixture_matches_its_schemas(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(instance, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Valid_fixture_round_trips_losslessly(string fixture)
    {
        JsonElement original = RepositoryPaths.ReadFixtureElement(fixture);
        JsonElement reserialized = Reserialize(fixture);

        Assert.Null(JsonCompare.Difference(original, reserialized));
    }

    [Theory]
    [MemberData(nameof(ValidFixtures))]
    public void Serialized_output_matches_the_schemas(string fixture)
    {
        JsonElement reserialized = Reserialize(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(reserialized, schemaId));
        }
    }

    [Theory]
    [MemberData(nameof(ValidMessageFixtures))]
    public void Message_fixture_deserializes_to_the_expected_type(string fixture)
    {
        LinkMessage message = LinkCodec.Parse(RepositoryPaths.ReadFixture(fixture));

        Type expected = MessageTypesByName[RepositoryPaths.FixtureType(fixture)];
        Assert.IsType(expected, message);
        Assert.Equal(RepositoryPaths.FixtureType(fixture), message.T);
    }

    [Theory]
    [MemberData(nameof(ForwardCompatibleFixtures))]
    public void Forward_compatible_fixture_validates_and_parses(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.Empty(RepositoryPaths.Schemas.Validate(instance, schemaId));
        }

        string type = RepositoryPaths.FixtureType(fixture);
        if (type == "manifest")
        {
            Assert.NotNull(LinkCodec.ParseManifest(RepositoryPaths.ReadFixture(fixture)));
        }
        else
        {
            Assert.NotNull(LinkCodec.Parse(RepositoryPaths.ReadFixture(fixture)));
        }
    }

    [Fact]
    public void Unknown_message_type_becomes_an_UnknownMessage_and_round_trips()
    {
        const string fixture = "forward_compat/message.unknown_type.json";
        LinkMessage message = LinkCodec.Parse(RepositoryPaths.ReadFixture(fixture));

        UnknownMessage unknown = Assert.IsType<UnknownMessage>(message);
        Assert.Equal("some_future_message", unknown.T);

        using JsonDocument serialized = JsonDocument.Parse(LinkCodec.Serialize(message));
        Assert.Null(JsonCompare.Difference(RepositoryPaths.ReadFixtureElement(fixture), serialized.RootElement));
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_known_ones_are_read()
    {
        LinkMessage message = LinkCodec.Parse(RepositoryPaths.ReadFixture("forward_compat/hello.future_fields.json"));

        HelloMessage hello = Assert.IsType<HelloMessage>(message);
        Assert.Equal(3, hello.Protocol.Max);
        Assert.Equal("143ffa0cd221", hello.ManifestRev);
        Assert.NotNull(hello.Features);
        Assert.Contains("some_future_feature", hello.Features);
    }

    [Theory]
    [MemberData(nameof(InvalidFixtures))]
    public void Invalid_fixture_is_rejected_by_the_schemas(string fixture)
    {
        JsonElement instance = RepositoryPaths.ReadFixtureElement(fixture);

        foreach (string schemaId in SchemasFor(fixture))
        {
            Assert.NotEmpty(RepositoryPaths.Schemas.Validate(instance, schemaId));
        }
    }

    private static TheoryData<string> ToData(IEnumerable<string> items)
    {
        var data = new TheoryData<string>();
        foreach (string item in items)
        {
            data.Add(item);
        }

        return data;
    }

    private static bool IsMessage(string fixture)
    {
        return MessageTypesByName.ContainsKey(RepositoryPaths.FixtureType(fixture));
    }

    private static List<string> SchemasFor(string fixture)
    {
        string type = RepositoryPaths.FixtureType(fixture);
        if (MessageTypesByName.ContainsKey(type))
        {
            return [RepositoryPaths.SchemaId(type + ".schema.json"), RepositoryPaths.SchemaId("message.schema.json")];
        }

        if (type == "message")
        {
            return [RepositoryPaths.SchemaId("message.schema.json")];
        }

        return [RepositoryPaths.SchemaId(type + ".schema.json")];
    }

    private static JsonElement Reserialize(string fixture)
    {
        string json = RepositoryPaths.ReadFixture(fixture);
        string type = RepositoryPaths.FixtureType(fixture);

        string output = type switch
        {
            "manifest" => JsonSerializer.Serialize(LinkCodec.ParseManifest(json), LinkJsonContext.Default.Manifest),
            "pair_request" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, LinkJsonContext.Default.PairRequest)!,
                LinkJsonContext.Default.PairRequest),
            "pair_response" => JsonSerializer.Serialize(
                JsonSerializer.Deserialize(json, LinkJsonContext.Default.PairResponse)!,
                LinkJsonContext.Default.PairResponse),
            _ => LinkCodec.Serialize(LinkCodec.Parse(json)),
        };

        using JsonDocument document = JsonDocument.Parse(output);
        return document.RootElement.Clone();
    }
}
