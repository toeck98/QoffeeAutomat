using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Agent.Link.Outbound;
using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Agent.Tests.Link.Outbound;

/// <summary>
/// The enrollment string of the reverse link, <c>qalr1.&lt;base64url(JSON)&gt;</c> (ADR 0003), and the rules for outbound URLs and pinned
/// fingerprints that it relies on. The payload of the valid case is the fixture <c>protocol/fixtures/reverse/enrollment.json</c>, which the
/// Python tests of the integration use as well, so both implementations agree on the format.
/// </summary>
[Trait("Category", "OutboundLink")]
public sealed class ReverseLinkEnrollmentTests
{
    private const string Token = "qalr_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string Url = "wss://ha.example.org/api/qoffeeautomat/reverse/v1";

    private static readonly string Fingerprint = string.Concat(Enumerable.Repeat("A1B2C3D4E5F60718293A4B5C6D7E8F90", 2));

    [Fact]
    public void The_fixture_payload_is_an_enrollment_that_the_agent_accepts()
    {
        string payload = ReverseFixtures.ReadReverse("enrollment.json");

        bool parsed = ReverseLinkEnrollment.TryParse(Encode(payload), out ReverseLinkEnrollment? enrollment, out string? error);

        Assert.True(parsed, error);
        Assert.Equal(new Uri(Url), enrollment!.Url);
        Assert.Equal(Token, enrollment.Token);
        Assert.Equal(Fingerprint, enrollment.Fingerprint);
    }

    [Fact]
    public void An_enrollment_survives_the_round_trip_through_its_string()
    {
        var original = new ReverseLinkEnrollment(new Uri(Url), Token, Fingerprint);

        string text = original.ToEnrollmentString();

        Assert.StartsWith("qalr1.", text, StringComparison.Ordinal);
        Assert.True(ReverseLinkEnrollment.TryParse(text, out ReverseLinkEnrollment? parsed, out string? error), error);
        Assert.Equal(original, parsed);
        using JsonDocument written = JsonDocument.Parse(Base64Url.DecodeFromChars(text.AsSpan("qalr1.".Length)));
        Assert.Equal(Url, written.RootElement.GetProperty("url").GetString());
        Assert.Equal(Token, written.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void The_fingerprint_is_optional_and_white_space_around_the_string_is_ignored()
    {
        string text = "  " + Encode($"{{\"url\":\"{Url}\",\"token\":\"{Token}\"}}") + "\r\n";

        Assert.True(ReverseLinkEnrollment.TryParse(text, out ReverseLinkEnrollment? enrollment, out string? error), error);
        Assert.Null(enrollment!.Fingerprint);
    }

    [Fact]
    public void A_fingerprint_in_any_notation_is_normalised()
    {
        string lower = string.Join(':', Enumerable.Range(0, 32).Select(index => Fingerprint.Substring(index * 2, 2).ToLowerInvariant()));
        string text = Encode($"{{\"url\":\"{Url}\",\"token\":\"{Token}\",\"fp\":\"{lower}\"}}");

        Assert.True(ReverseLinkEnrollment.TryParse(text, out ReverseLinkEnrollment? enrollment, out string? error), error);
        Assert.Equal(Fingerprint, enrollment!.Fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qalr2.e30")]
    [InlineData("qahub1.e30")]
    [InlineData("qalr1.***")]
    [InlineData("qalr1.bm90IGpzb24")]
    [InlineData("qalr1.e30")]
    public void A_string_that_is_not_an_enrollment_is_refused_with_a_reason(string? text)
    {
        Assert.False(ReverseLinkEnrollment.TryParse(text, out ReverseLinkEnrollment? enrollment, out string? error));
        Assert.Null(enrollment);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("{\"url\":\"https://ha.example.org/x\",\"token\":\"" + Token + "\"}")]
    [InlineData("{\"url\":\"ws://ha.example.org/x\",\"token\":\"" + Token + "\"}")]
    [InlineData("{\"url\":\"wss://user:secret@ha.example.org/x\",\"token\":\"" + Token + "\"}")]
    [InlineData("{\"url\":\"wss://ha.example.org/x\",\"token\":\"qalr_short\"}")]
    [InlineData("{\"url\":\"wss://ha.example.org/x\",\"token\":\"qapm_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\"}")]
    [InlineData("{\"url\":\"wss://ha.example.org/x\",\"token\":\"qalr_AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxw!Hh8\"}")]
    [InlineData("{\"url\":\"wss://ha.example.org/x\",\"token\":\"" + Token + "\",\"fp\":\"12:34\"}")]
    [InlineData("{\"url\":\"wss://ha.example.org/x\"}")]
    [InlineData("[]")]
    public void A_payload_that_breaks_the_rules_is_refused(string json)
    {
        Assert.False(ReverseLinkEnrollment.TryParse(Encode(json), out ReverseLinkEnrollment? enrollment, out string? error));
        Assert.Null(enrollment);
        Assert.DoesNotContain("AAECAwQ", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ws_url_is_fine_for_a_loopback_host_only()
    {
        Assert.True(ReverseLinkEnrollment.TryParse(Encode($"{{\"url\":\"ws://localhost:8123/x\",\"token\":\"{Token}\"}}"), out _, out _));
        Assert.True(ReverseLinkEnrollment.TryParse(Encode($"{{\"url\":\"ws://127.0.0.1:8123/x\",\"token\":\"{Token}\"}}"), out _, out _));
        Assert.False(ReverseLinkEnrollment.TryParse(Encode($"{{\"url\":\"ws://192.168.1.10:8123/x\",\"token\":\"{Token}\"}}"), out _, out _));
    }

    [Fact]
    public void The_description_of_an_enrollment_does_not_show_the_token()
    {
        var enrollment = new ReverseLinkEnrollment(new Uri(Url + "?secret=1"), Token, Fingerprint);

        string description = enrollment.ToString();

        Assert.DoesNotContain("AAECAwQ", description, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", description, StringComparison.Ordinal);
        Assert.Contains("ha.example.org", description, StringComparison.Ordinal);
    }

    [Fact]
    public void The_description_of_an_endpoint_does_not_show_the_credential()
    {
        var endpoint = new OutboundEndpoint(new Uri(Url + "?x=1"), Token, Fingerprint);

        Assert.DoesNotContain("AAECAwQ", endpoint.ToString(), StringComparison.Ordinal);
        Assert.Equal(Url, endpoint.DisplayUrl);
    }

    [Fact]
    public void A_token_is_the_prefix_and_32_random_bytes_in_base64url()
    {
        string token = ReverseLinkEnrollment.CreateToken(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray());

        Assert.Equal(Token, token);
        Assert.Throws<ArgumentException>(() => ReverseLinkEnrollment.CreateToken(new byte[31]));
    }

    [Theory]
    [InlineData("wss://ha.example.org/api/qoffeeautomat/reverse/v1", true)]
    [InlineData("WSS://HA.EXAMPLE.ORG:8123/x", true)]
    [InlineData("ws://localhost/x", true)]
    [InlineData("ws://[::1]:8123/x", true)]
    [InlineData("ws://ha.example.org/x", false)]
    [InlineData("https://ha.example.org/x", false)]
    [InlineData("http://localhost/x", false)]
    [InlineData("/api/qoffeeautomat/reverse/v1", false)]
    [InlineData("wss://user@ha.example.org/x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_url_rules_allow_wss_and_ws_only_for_loopback(string? url, bool allowed)
    {
        Assert.Equal(allowed, OutboundLinkRules.CheckUrl(url) is null);
    }

    [Theory]
    [InlineData("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90", true)]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", true)]
    [InlineData("a1-b2-c3-d4-e5-f6-07-18-29-3a-4b-5c-6d-7e-8f-90-a1-b2-c3-d4-e5-f6-07-18-29-3a-4b-5c-6d-7e-8f-90", true)]
    [InlineData("A1:B2", false)]
    [InlineData("zz1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", false)]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90aa", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_fingerprint_has_64_hexadecimal_digits_in_any_notation(string? text, bool valid)
    {
        bool result = OutboundLinkRules.TryNormalizeFingerprint(text, out string normalized);

        Assert.Equal(valid, result);
        if (valid)
        {
            Assert.Equal(OutboundLinkRules.FingerprintDigits, normalized.Length);
            Assert.Equal(normalized.ToUpperInvariant(), normalized);
        }
    }

    private static string Encode(string json) => ReverseLinkEnrollment.Prefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
