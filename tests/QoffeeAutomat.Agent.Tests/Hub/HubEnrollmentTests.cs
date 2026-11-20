using System.Security.Cryptography;
using QoffeeAutomat.Agent.Hosting;
using QoffeeAutomat.Agent.Hub;
using QoffeeAutomat.Contracts.Hub;

namespace QoffeeAutomat.Agent.Tests.Hub;

/// <summary>The enrollment string of the hub as the agent reads it (PLAN.md S20, D-026): what is accepted, what is refused, and that the token never shows in a message.</summary>
[Trait("Category", "HubLink")]
public sealed class HubEnrollmentTests
{
    private static readonly string Token = FakeHub.NewSecret();

    private static string Key()
    {
        using ECDsa key = HubSigningKey.Generate();
        return HubSigningKey.ExportPublicKey(key);
    }

    private static string Encode(string url, string token, string? key = null, string? fingerprint = null)
    {
        return EnrollmentString.Encode(new EnrollmentPayload(new Uri(url), token, key ?? Key(), fingerprint));
    }

    [Fact]
    public void A_valid_string_is_read_and_the_address_is_normalised()
    {
        string fingerprint = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        string key = Key();

        bool valid = HubEnrollment.TryParse("  " + Encode("https://hub.example.org:8490/", Token, key, fingerprint) + " \n", out HubEnrollment? enrollment, out string? error);

        Assert.True(valid, error);
        Assert.Null(error);
        Assert.Equal("https://hub.example.org:8490", enrollment!.Url.AbsoluteUri.TrimEnd('/'));
        Assert.Equal(Token, enrollment.Token);
        Assert.Equal(key, enrollment.PublicKey);
        Assert.Equal(fingerprint, enrollment.Fingerprint);
        Assert.Equal(new Uri("https://hub.example.org:8490/hub/v1/enroll"), enrollment.EnrollUri);
        Assert.Equal(new Uri("wss://hub.example.org:8490/hub/v1/agent"), enrollment.LinkUri);
    }

    [Fact]
    public void A_string_without_a_fingerprint_means_normal_certificate_validation()
    {
        Assert.True(HubEnrollment.TryParse(Encode("https://hub.example.org", Token), out HubEnrollment? enrollment, out _));

        Assert.Null(enrollment!.Fingerprint);
    }

    [Fact]
    public void An_address_below_a_path_keeps_the_path_for_a_hub_behind_a_reverse_proxy()
    {
        Assert.True(HubEnrollment.TryParse(Encode("https://home.example.org/qoffeeautomat/", Token), out HubEnrollment? enrollment, out _));

        Assert.Equal(new Uri("wss://home.example.org/qoffeeautomat/hub/v1/agent"), enrollment!.LinkUri);
    }

    [Fact]
    public void A_loopback_hub_may_use_plain_http_but_no_other_host_may()
    {
        Assert.True(HubEnrollment.TryParse(Encode("http://127.0.0.1:8490", Token), out HubEnrollment? local, out _));
        Assert.Equal(new Uri("ws://127.0.0.1:8490/hub/v1/agent"), local!.LinkUri);

        Assert.False(HubEnrollment.TryParse(Encode("http://hub.example.org", Token), out HubEnrollment? remote, out string? error));
        Assert.Null(remote);
        Assert.Contains("https", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("qahub2.abcdef")]
    [InlineData("hello")]
    [InlineData("qahub1.")]
    [InlineData("qahub1.!!!not-base64!!!")]
    public void Text_that_is_not_an_enrollment_string_is_refused(string? text)
    {
        Assert.False(HubEnrollment.TryParse(text, out HubEnrollment? enrollment, out string? error));

        Assert.Null(enrollment);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_token_or_key_or_fingerprint_that_is_not_valid_is_refused_without_echoing_the_token()
    {
        string tooShort = "abc";
        Assert.False(HubEnrollment.TryParse(Encode("https://hub.example.org", tooShort), out _, out string? shortError));
        Assert.DoesNotContain(tooShort, shortError, StringComparison.Ordinal);

        Assert.False(HubEnrollment.TryParse(Encode("https://hub.example.org", Token, key: "AAAA"), out _, out string? keyError));
        Assert.DoesNotContain(Token, keyError, StringComparison.Ordinal);

        Assert.False(HubEnrollment.TryParse(Encode("https://hub.example.org", Token, fingerprint: "1234"), out _, out string? fingerprintError));
        Assert.DoesNotContain(Token, fingerprintError, StringComparison.Ordinal);
    }

    [Fact]
    public void An_address_with_credentials_a_query_or_a_fragment_is_refused()
    {
        Assert.False(HubEnrollment.TryParse(Encode("https://user:supersecret123@hub.example.org", Token), out _, out string? userError));
        Assert.DoesNotContain("supersecret123", userError, StringComparison.Ordinal);
        Assert.False(HubEnrollment.TryParse(Encode("https://hub.example.org/?a=b", Token), out _, out _));
        Assert.False(HubEnrollment.TryParse(Encode("https://hub.example.org/#top", Token), out _, out _));
    }

    [Fact]
    public void A_string_that_is_too_long_is_refused()
    {
        string text = HubProtocolPrefix() + new string('A', HubEnrollment.MaxStringLength);

        Assert.False(HubEnrollment.TryParse(text, out _, out string? error));
        Assert.Contains("too long", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_does_not_show_in_the_text_of_the_enrollment()
    {
        Assert.True(HubEnrollment.TryParse(Encode("https://hub.example.org", Token), out HubEnrollment? enrollment, out _));

        Assert.DoesNotContain(Token, enrollment!.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://hub.example.org", true)]
    [InlineData("https://hub.example.org:8490/base", true)]
    [InlineData("http://localhost:8490", true)]
    [InlineData("http://hub.example.org", false)]
    [InlineData("ftp://hub.example.org", false)]
    [InlineData("https://hub.example.org/?x=1", false)]
    public void The_address_rules_of_the_hub(string url, bool allowed)
    {
        Assert.Equal(allowed, HubUrls.CheckBaseUrl(new Uri(url)) is null);
    }

    [Fact]
    public void A_credential_is_43_characters_of_base64url()
    {
        Assert.True(HubCredentialRules.IsValid(FakeHub.NewSecret()));
        Assert.False(HubCredentialRules.IsValid(null));
        Assert.False(HubCredentialRules.IsValid("short"));
        Assert.False(HubCredentialRules.IsValid(new string('+', HubCredentialRules.Length)));
        Assert.False(HubCredentialRules.IsValid(new string('a', HubCredentialRules.Length + 1)));
    }

    private static string HubProtocolPrefix() => HubProtocol.EnrollmentStringPrefix;
}

/// <summary>The verbs <c>hub join</c>, <c>hub leave</c> and <c>hub status</c> on the command line (PLAN.md S20, T905).</summary>
[Trait("Category", "HubLink")]
public sealed class HubCommandLineTests
{
    [Fact]
    public void Hub_join_takes_the_enrollment_string()
    {
        AgentOptions options = CommandLine.Parse(["hub", "join", "qahub1.abc"]);

        Assert.Null(options.Error);
        Assert.Equal(AgentVerb.Hub, options.Verb);
        Assert.Equal(HubAction.Join, options.HubAction);
        Assert.Equal("qahub1.abc", options.HubEnrollment);
    }

    [Fact]
    public void A_dash_means_that_the_string_is_read_from_standard_input()
    {
        AgentOptions options = CommandLine.Parse(["hub", "join", "-"]);

        Assert.Null(options.Error);
        Assert.Equal("-", options.HubEnrollment);
    }

    [Fact]
    public void Hub_leave_may_name_what_becomes_of_the_documents()
    {
        AgentOptions convert = CommandLine.Parse(["hub", "leave", "--documents", "convert"]);
        AgentOptions remove = CommandLine.Parse(["hub", "leave", "--documents=remove"]);
        AgentOptions none = CommandLine.Parse(["hub", "leave"]);

        Assert.Equal(HubAction.Leave, convert.HubAction);
        Assert.Equal("convert", convert.HubDocuments);
        Assert.Equal("remove", remove.HubDocuments);
        Assert.Null(none.HubDocuments);
        Assert.Null(none.Error);
    }

    [Fact]
    public void Hub_status_takes_nothing()
    {
        AgentOptions options = CommandLine.Parse(["hub", "status"]);

        Assert.Null(options.Error);
        Assert.Equal(HubAction.Status, options.HubAction);
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("hub join")]
    [InlineData("hub frobnicate")]
    [InlineData("hub status extra")]
    [InlineData("hub status --documents convert")]
    [InlineData("hub leave --documents keep")]
    [InlineData("hub leave --documents")]
    public void Anything_else_is_a_usage_error(string line)
    {
        Assert.NotNull(CommandLine.Parse(line.Split(' ')).Error);
    }

    [Fact]
    public void An_extra_argument_is_refused_without_echoing_it_because_it_may_be_a_token()
    {
        AgentOptions options = CommandLine.Parse(["hub", "join", "qahub1.first", "qahub1.secret-second"]);

        Assert.NotNull(options.Error);
        Assert.DoesNotContain("secret-second", options.Error, StringComparison.Ordinal);
    }
}
