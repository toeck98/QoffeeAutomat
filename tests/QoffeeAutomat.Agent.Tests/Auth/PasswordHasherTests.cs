using System.Globalization;
using QoffeeAutomat.Agent.Security.Auth;

namespace QoffeeAutomat.Agent.Tests.Auth;

/// <summary>PBKDF2-SHA256 password hashes (T212, PLAN.md S09 item 1).</summary>
[Trait("Category", "Auth")]
public sealed class PasswordHasherTests
{
    [Fact]
    public void A_hash_names_its_scheme_and_uses_at_least_600000_iterations()
    {
        string hash = PasswordHasher.Hash("correct horse battery staple");

        string[] parts = hash.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1], CultureInfo.InvariantCulture) >= 600_000);
        Assert.DoesNotContain("correct horse", hash, StringComparison.Ordinal);
    }

    [Fact]
    public void The_right_password_verifies_and_a_wrong_one_does_not()
    {
        string hash = PasswordHasher.Hash("correct horse battery staple");

        Assert.True(PasswordHasher.Verify("correct horse battery staple", hash));
        Assert.False(PasswordHasher.Verify("correct horse battery stapl", hash));
        Assert.False(PasswordHasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Every_hash_has_its_own_salt()
    {
        string first = PasswordHasher.Hash("same password, long enough");
        string second = PasswordHasher.Hash("same password, long enough");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHasher.Verify("same password, long enough", first));
        Assert.True(PasswordHasher.Verify("same password, long enough", second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain text")]
    [InlineData("pbkdf2-sha256$600000$AAAA")]
    [InlineData("pbkdf2-sha256$abc$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$600000$not base64!$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$600000$AAAAAAAAAAAAAAAAAAAAAA==$AAAA")]
    [InlineData("other$600000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void A_malformed_hash_never_verifies(string stored)
    {
        Assert.False(PasswordHasher.Verify("anything at all", stored));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    [InlineData(599_999)]
    [InlineData(10_000_001)]
    public void A_hash_with_too_few_or_too_many_iterations_is_refused(int iterations)
    {
        // Well-formed apart from the iteration count, which is outside the accepted range.
        string stored = "pbkdf2-sha256$" + iterations.ToString(CultureInfo.InvariantCulture)
            + "$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

        Assert.False(PasswordHasher.Verify("password", stored));
    }
}
