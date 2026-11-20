using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Contracts.Tests.Hub;

/// <summary>ECDSA P-256 signing and verification of the desired state (PLAN.md S20, D-027, T901).</summary>
public sealed class DesiredStateSigningTests : IDisposable
{
    private const string AgentId = "5b0c6c1e-8e0a-4f6e-9a57-3d2f3a1c9b77";

    private readonly ECDsa _hubKey = HubSigningKey.Generate();

    public static TheoryData<int> ItemIndexes => [0, 1, 2];

    public void Dispose()
    {
        _hubKey.Dispose();
    }

    [Fact]
    public void A_signed_desired_state_verifies()
    {
        DesiredState state = CreateState();

        string signature = DesiredStateSigner.Sign(state, _hubKey);

        Assert.True(DesiredStateSigner.Verify(state, signature, _hubKey));
        Assert.Equal(HubProtocol.SignatureBytes, Convert.FromBase64String(signature).Length);
    }

    [Fact]
    public void A_state_with_no_items_can_be_signed_and_verified()
    {
        DesiredState state = CreateState() with { Items = [] };

        string signature = DesiredStateSigner.Sign(state, _hubKey);

        Assert.True(DesiredStateSigner.Verify(state, signature, _hubKey));
    }

    [Fact]
    public void Verification_works_with_the_public_key_exported_for_the_enrollment_string()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        string exported = HubSigningKey.ExportPublicKey(_hubKey);
        using ECDsa imported = HubSigningKey.ImportPublicKey(exported);

        Assert.True(DesiredStateSigner.Verify(state, signature, imported));
        Assert.True(HubSigningKey.TryImportPublicKey(exported, out ECDsa? viaTry));
        using (viaTry)
        {
            Assert.True(DesiredStateSigner.Verify(state, signature, viaTry!));
        }
    }

    [Fact]
    public void A_frame_created_by_the_hub_verifies_for_the_agent_it_is_meant_for()
    {
        ConfigDesiredMessage message = DesiredStateSigner.CreateMessage(11, CreateState(), _hubKey);

        Assert.Equal(7, message.Generation);
        Assert.True(DesiredStateSigner.Verify(AgentId, message, _hubKey));
    }

    [Fact]
    public void A_frame_does_not_verify_for_another_agent()
    {
        ConfigDesiredMessage message = DesiredStateSigner.CreateMessage(11, CreateState(), _hubKey);

        Assert.False(DesiredStateSigner.Verify("7d3f9a10-2b6c-4e8d-9c11-5a0e4b7f8d21", message, _hubKey));
    }

    [Fact]
    public void A_frame_survives_serialization_and_still_verifies()
    {
        ConfigDesiredMessage message = DesiredStateSigner.CreateMessage(11, CreateState(), _hubKey);

        string json = JsonSerializer.Serialize(message, HubJsonContext.Default.ConfigDesiredMessage);
        ConfigDesiredMessage parsed = JsonSerializer.Deserialize(json, HubJsonContext.Default.ConfigDesiredMessage)!;

        Assert.True(DesiredStateSigner.Verify(AgentId, parsed, _hubKey));
    }

    [Fact]
    public void Changing_the_agent_id_fails_verification()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        Assert.False(DesiredStateSigner.Verify(state with { AgentId = "other-agent" }, signature, _hubKey));
        Assert.False(DesiredStateSigner.Verify(state with { AgentId = AgentId.ToUpperInvariant() }, signature, _hubKey));
    }

    [Fact]
    public void Changing_the_generation_fails_verification()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        Assert.False(DesiredStateSigner.Verify(state with { Generation = state.Generation + 1 }, signature, _hubKey));
        Assert.False(DesiredStateSigner.Verify(state with { Generation = state.Generation - 1 }, signature, _hubKey));
    }

    [Fact]
    public void Changing_the_local_config_policy_fails_verification()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        PolicyInfo changed = state.Policy with { LocalConfig = LocalConfigPolicies.Allow };
        Assert.False(DesiredStateSigner.Verify(state with { Policy = changed }, signature, _hubKey));
    }

    [Fact]
    public void Changing_the_direct_link_policy_fails_verification()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        PolicyInfo changed = state.Policy with { DirectLink = DirectLinkPolicies.Allow };
        Assert.False(DesiredStateSigner.Verify(state with { Policy = changed }, signature, _hubKey));
    }

    [Theory]
    [MemberData(nameof(ItemIndexes))]
    public void Changing_the_hash_of_any_item_fails_verification(int index)
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        DesiredItem[] items = [.. state.Items];
        items[index] = items[index] with { Hash = FlipFirstHexDigit(items[index].Hash) };

        Assert.False(DesiredStateSigner.Verify(state with { Items = items }, signature, _hubKey));
    }

    [Theory]
    [MemberData(nameof(ItemIndexes))]
    public void Changing_the_kind_or_id_of_any_item_fails_verification(int index)
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        DesiredItem[] otherId = [.. state.Items];
        otherId[index] = otherId[index] with { Id = otherId[index].Id + "_x" };
        DesiredItem[] otherKind = [.. state.Items];
        otherKind[index] = otherKind[index] with
        {
            Kind = otherKind[index].Kind == DocumentKinds.Variables ? DocumentKinds.Command : DocumentKinds.Variables,
        };

        Assert.False(DesiredStateSigner.Verify(state with { Items = otherId }, signature, _hubKey));
        Assert.False(DesiredStateSigner.Verify(state with { Items = otherKind }, signature, _hubKey));
    }

    [Fact]
    public void Adding_removing_or_reordering_items_fails_verification()
    {
        DesiredState state = CreateState();
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        DesiredItem extra = DocumentHasher.ToItem(
            DocumentHasher.CreateDocument(DocumentKinds.Variables, "extra", ParseObject("""{"values":{}}""")));

        Assert.False(DesiredStateSigner.Verify(state with { Items = [.. state.Items, extra] }, signature, _hubKey));
        Assert.False(DesiredStateSigner.Verify(state with { Items = [.. state.Items.Skip(1)] }, signature, _hubKey));
        Assert.False(DesiredStateSigner.Verify(state with { Items = [.. state.Items.Reverse()] }, signature, _hubKey));
    }

    [Fact]
    public void A_changed_document_body_changes_the_hash_and_fails_verification()
    {
        HubDocument original = DocumentHasher.CreateDocument(
            DocumentKinds.Variables,
            "site",
            ParseObject("""{"values":{"room":"office"}}"""));
        HubDocument tampered = DocumentHasher.CreateDocument(
            DocumentKinds.Variables,
            "site",
            ParseObject("""{"values":{"room":"attic"}}"""));
        DesiredState state = new(AgentId, 1, DefaultPolicy(), [DocumentHasher.ToItem(original)]);
        string signature = DesiredStateSigner.Sign(state, _hubKey);

        DesiredState tamperedState = state with { Items = [DocumentHasher.ToItem(tampered)] };

        Assert.NotEqual(original.Hash, tampered.Hash);
        Assert.False(DesiredStateSigner.Verify(tamperedState, signature, _hubKey));
    }

    [Fact]
    public void A_signature_of_another_key_fails_verification()
    {
        DesiredState state = CreateState();
        using ECDsa otherKey = HubSigningKey.Generate();

        string signature = DesiredStateSigner.Sign(state, otherKey);

        Assert.False(DesiredStateSigner.Verify(state, signature, _hubKey));
    }

    [Fact]
    public void A_modified_signature_fails_verification()
    {
        DesiredState state = CreateState();
        byte[] signature = Convert.FromBase64String(DesiredStateSigner.Sign(state, _hubKey));
        signature[10] ^= 0x01;

        Assert.False(DesiredStateSigner.Verify(state, Convert.ToBase64String(signature), _hubKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AQID")]
    public void A_malformed_signature_fails_without_throwing(string signature)
    {
        Assert.False(DesiredStateSigner.Verify(CreateState(), signature, _hubKey));
    }

    [Fact]
    public void The_signed_content_is_the_canonical_json_with_sorted_members()
    {
        DesiredState state = new(
            AgentId,
            7,
            new PolicyInfo(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny),
            [new DesiredItem(DocumentKinds.Command, "run", new string('a', 64))]);

        string canonical = Encoding.UTF8.GetString(DesiredStateSigner.CanonicalPayload(state));

        Assert.Equal(
            "{\"agent_id\":\"" + AgentId + "\",\"generation\":7,"
            + "\"items\":[{\"hash\":\"" + new string('a', 64) + "\",\"id\":\"run\",\"kind\":\"command\"}],"
            + "\"policy\":{\"direct_link\":\"deny\",\"local_config\":\"locked\"}}",
            canonical);
    }

    [Fact]
    public void The_dummy_signature_of_the_config_desired_fixture_does_not_verify()
    {
        ConfigDesiredMessage fixture = JsonSerializer.Deserialize(
            RepositoryPaths.ReadFixture("hub/config_desired.json"),
            HubJsonContext.Default.ConfigDesiredMessage)!;

        Assert.False(DesiredStateSigner.Verify(AgentId, fixture, _hubKey));
    }

    [Fact]
    public void A_non_p256_public_key_is_not_imported()
    {
        using ECDsa p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        string exported = Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo());

        Assert.False(HubSigningKey.TryImportPublicKey(exported, out _));
        Assert.False(HubSigningKey.TryImportPublicKey("not base64!", out _));
        Assert.False(HubSigningKey.TryImportPublicKey(null, out _));
    }

    private static DesiredState CreateState()
    {
        IReadOnlyList<DesiredItem> items = DesiredState.SortItems(
        [
            DocumentHasher.ToItem(DocumentHasher.CreateDocument(
                DocumentKinds.Command,
                "set_display_profile",
                ParseObject("""{"id":"set_display_profile","name":"Display profile"}"""))),
            DocumentHasher.ToItem(DocumentHasher.CreateDocument(
                DocumentKinds.Script,
                "set_display_profile.ps1",
                JsonSerializer.SerializeToElement("Write-Output 'hello'"))),
            DocumentHasher.ToItem(DocumentHasher.CreateDocument(
                DocumentKinds.Variables,
                "site",
                ParseObject("""{"values":{"room":"office"}}"""))),
        ]);

        return new DesiredState(AgentId, 7, DefaultPolicy(), items);
    }

    private static PolicyInfo DefaultPolicy() => new(LocalConfigPolicies.Locked, DirectLinkPolicies.Deny);

    private static JsonElement ParseObject(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string FlipFirstHexDigit(string hash)
    {
        char first = hash[0] == '0' ? '1' : '0';
        return first + hash[1..];
    }
}
