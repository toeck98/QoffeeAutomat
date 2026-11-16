using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Core.Secrets;
using QoffeeAutomat.Platform.Windows.Security;

namespace QoffeeAutomat.Platform.Windows.Tests.Security;

/// <summary>Secrets at rest use DPAPI machine scope on Windows (PLAN.md S09 item 6).</summary>
[Trait("Category", "Service")]
public sealed class DpapiSecretProtectorTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("correct horse battery staple");
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("auth.password_hash");

    [Fact]
    public void The_scheme_is_distinct_from_the_file_key_scheme()
    {
        var protector = new DpapiSecretProtector();

        Assert.Equal("dpapi-machine", protector.Scheme);
        Assert.NotEqual(FileKeyProtector.SchemeName, protector.Scheme);
    }

    [WindowsFact]
    public void A_secret_survives_a_round_trip_and_the_blob_does_not_contain_it()
    {
        var protector = new DpapiSecretProtector();

        byte[] blob = protector.Protect(Secret, Context);

        Assert.Equal(Secret, protector.Unprotect(blob, Context));
        Assert.False(blob.AsSpan().IndexOf(Secret) >= 0, "The protected blob contains the plain text.");
    }

    [WindowsFact]
    public void A_blob_cannot_be_used_for_another_name()
    {
        var protector = new DpapiSecretProtector();
        byte[] blob = protector.Protect(Secret, Context);

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, Encoding.UTF8.GetBytes("link.token_hashes")));
    }

    [WindowsFact]
    public void A_damaged_blob_is_rejected()
    {
        var protector = new DpapiSecretProtector();
        byte[] blob = protector.Protect(Secret, Context);
        blob[^1] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, Context));
    }

    [WindowsFact]
    public async Task The_secret_store_works_on_top_of_it()
    {
        using var temp = new TempDirectory();
        Directory.CreateDirectory(temp.Path);
        string file = Path.Combine(temp.Path, "secrets.json");
        var store = new SecretStore(file, new DpapiSecretProtector());

        await store.SetStringAsync("script.token", "s3cret-value", CancellationToken.None);

        Assert.Equal("s3cret-value", await new SecretStore(file, new DpapiSecretProtector()).GetStringAsync("script.token", CancellationToken.None));
        Assert.DoesNotContain("s3cret-value", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
    }
}
