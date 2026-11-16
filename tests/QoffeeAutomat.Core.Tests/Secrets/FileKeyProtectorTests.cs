using System.Security.Cryptography;
using System.Text;
using QoffeeAutomat.Core.Secrets;

namespace QoffeeAutomat.Core.Tests.Secrets;

[Trait("Category", "Secrets")]
public sealed class FileKeyProtectorTests
{
    private static readonly byte[] Context = Encoding.UTF8.GetBytes("auth.password_hash");

    [Fact]
    public void A_secret_survives_a_round_trip()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets", "secrets.key"));
        byte[] secret = Encoding.UTF8.GetBytes("correct horse battery staple");

        byte[] blob = protector.Protect(secret, Context);
        byte[] plain = protector.Unprotect(blob, Context);

        Assert.Equal(secret, plain);
        Assert.Equal(FileKeyProtector.SchemeName, protector.Scheme);
    }

    [Fact]
    public void The_blob_does_not_contain_the_plaintext_and_differs_for_every_call()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        byte[] secret = Encoding.UTF8.GetBytes("super-secret-value-0123456789");

        byte[] first = protector.Protect(secret, Context);
        byte[] second = protector.Protect(secret, Context);

        Assert.NotEqual(first, second);
        Assert.False(first.AsSpan().IndexOf(secret) >= 0);
        Assert.Equal(secret.Length + 1 + 12 + 16, first.Length);
    }

    [Fact]
    public void Empty_secrets_are_supported()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));

        byte[] blob = protector.Protect([], Context);

        Assert.Empty(protector.Unprotect(blob, Context));
    }

    [Fact]
    public void A_blob_is_bound_to_its_context_and_detects_tampering()
    {
        using var temp = new TempDirectory();
        using var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        byte[] blob = protector.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        byte[] otherContext = Encoding.UTF8.GetBytes("link.token_hashes");

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, otherContext));

        byte[] flipped = (byte[])blob.Clone();
        flipped[^1] ^= 0x01;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(flipped, Context));

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob.AsSpan(0, 10), Context));

        byte[] unknownVersion = (byte[])blob.Clone();
        unknownVersion[0] = 9;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(unknownVersion, Context));
    }

    [Fact]
    public void The_key_is_created_once_and_reused_by_the_next_instance()
    {
        using var temp = new TempDirectory();
        string keyFile = temp.Combine("secrets", "secrets.key");
        byte[] blob;
        using (var first = new FileKeyProtector(keyFile))
        {
            blob = first.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        }

        byte[] keyBytes = File.ReadAllBytes(keyFile);
        using var second = new FileKeyProtector(keyFile);

        Assert.Equal(32, keyBytes.Length);
        Assert.Equal("secret", Encoding.UTF8.GetString(second.Unprotect(blob, Context)));
        Assert.Equal(keyBytes, File.ReadAllBytes(keyFile));
        Assert.Empty(Directory.GetFiles(temp.Combine("secrets"), "*.tmp"));
    }

    [Fact]
    public void Different_installations_get_different_keys()
    {
        using var temp = new TempDirectory();
        string first = temp.Combine("one", "secrets.key");
        string second = temp.Combine("two", "secrets.key");
        using var a = new FileKeyProtector(first);
        using var b = new FileKeyProtector(second);

        Assert.NotEqual(File.ReadAllBytes(first), File.ReadAllBytes(second));
        byte[] blob = a.Protect(Encoding.UTF8.GetBytes("secret"), Context);
        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(blob, Context));
    }

    [Fact]
    public void On_Unix_the_key_file_is_created_with_mode_0600_in_a_private_directory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        string keyFile = temp.Combine("secrets", "secrets.key");

        using var protector = new FileKeyProtector(keyFile);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(temp.Combine("secrets")));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.GroupWrite)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.OtherExecute)]
    public void On_Unix_the_store_refuses_to_load_a_key_file_with_wider_permissions(UnixFileMode extra)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        string keyFile = temp.Combine("secrets", "secrets.key");
        using (new FileKeyProtector(keyFile))
        {
        }

        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);

        var exception = Assert.Throws<SecretStoreException>(() => new FileKeyProtector(keyFile));

        Assert.Equal(SecretStoreFailure.InsecureKeyFile, exception.Reason);
        Assert.Contains("chmod 600", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stricter_key_file_is_accepted()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var temp = new TempDirectory();
        string keyFile = temp.Combine("secrets.key");
        using (new FileKeyProtector(keyFile))
        {
        }

        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead);

        using var protector = new FileKeyProtector(keyFile);

        Assert.NotNull(protector);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(64)]
    public void A_key_file_of_the_wrong_size_is_refused(int size)
    {
        using var temp = new TempDirectory();
        string keyFile = temp.Combine("secrets.key");
        File.WriteAllBytes(keyFile, new byte[size]);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var exception = Assert.Throws<SecretStoreException>(() => new FileKeyProtector(keyFile));

        Assert.Equal(SecretStoreFailure.InvalidKeyFile, exception.Reason);
    }

    [Fact]
    public void A_disposed_protector_refuses_to_work()
    {
        using var temp = new TempDirectory();
        var protector = new FileKeyProtector(temp.Combine("secrets.key"));
        protector.Dispose();

        Assert.Throws<ObjectDisposedException>(() => protector.Protect(new byte[1], Context));
        Assert.Throws<ObjectDisposedException>(() => protector.Unprotect(new byte[64], Context));
    }
}
