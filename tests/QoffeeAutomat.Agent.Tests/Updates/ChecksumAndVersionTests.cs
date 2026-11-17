using QoffeeAutomat.Agent.Updates;

namespace QoffeeAutomat.Agent.Tests.Updates;

/// <summary>The checksum file of a release and the comparison of versions (PLAN.md S13, T704).</summary>
[Trait("Category", "Updates")]
public sealed class ChecksumAndVersionTests
{
    private const string Digest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_checksum_of_a_file_is_found_by_its_exact_name()
    {
        string other = new('a', 64);
        string sums = Digest + "  QoffeeAutomat-Setup-1.2.0-x64.exe\r\n" + other + " *other.zip\n\nnot a checksum line\n";

        Assert.True(ChecksumFile.TryFind(sums, "QoffeeAutomat-Setup-1.2.0-x64.exe", out string? text));
        Assert.Equal(Digest, text);
        Assert.True(ChecksumFile.TryFind(sums, "other.zip", out string? binary));
        Assert.Equal(other, binary);
        Assert.False(ChecksumFile.TryFind(sums, "QoffeeAutomat-Setup-1.2.0-arm64.exe", out _));
        Assert.False(ChecksumFile.TryFind(sums, "zip", out _));
        Assert.False(ChecksumFile.TryFind(string.Empty, "other.zip", out _));
    }

    [Fact]
    public void An_upper_case_checksum_is_read_as_lower_case()
    {
        string sums = Digest.ToUpperInvariant() + "  setup.exe\n";

        Assert.True(ChecksumFile.TryFind(sums, "setup.exe", out string? digest));

        Assert.Equal(Digest, digest);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefa")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    public void A_line_whose_checksum_is_not_64_hexadecimal_digits_is_ignored(string bad)
    {
        Assert.False(ChecksumFile.TryFind(bad + "  setup.exe\n", "setup.exe", out _));
    }

    [Fact]
    public void Only_equal_checksums_match_whatever_their_case()
    {
        string wrong = new string('0', 63) + "1";

        Assert.True(ChecksumFile.Matches(Digest, Digest));
        Assert.True(ChecksumFile.Matches(Digest, Digest.ToUpperInvariant()));
        Assert.False(ChecksumFile.Matches(Digest, wrong));
        Assert.False(ChecksumFile.Matches(Digest, Digest[..63]));
        Assert.False(ChecksumFile.Matches(Digest, string.Empty));
        Assert.False(ChecksumFile.Matches(Digest, new string('z', 64)));
    }

    [Fact]
    public async Task The_checksum_of_a_file_is_computed_over_its_content()
    {
        using var data = new TempDataDirectory();
        string path = Path.Combine(data.Path, "file.bin");
        await File.WriteAllBytesAsync(path, UpdateSamples.InstallerBytes);

        string actual = await ChecksumFile.ComputeAsync(path, CancellationToken.None);

        Assert.Equal(UpdateSamples.Sha256(UpdateSamples.InstallerBytes), actual);
        Assert.True(ChecksumFile.Matches(UpdateSamples.Sha256(UpdateSamples.InstallerBytes), actual));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData(" 1.2.3 ", "1.2.3")]
    [InlineData("1.2.3+build.5", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    [InlineData("1.2.3-rc.1+abc", "1.2.3-rc.1")]
    [InlineData("10.20.30", "10.20.30")]
    public void A_version_is_read_with_or_without_the_v_of_a_tag(string text, string expected)
    {
        Assert.True(SemanticVersion.TryParse(text, out SemanticVersion? version));

        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("a.b.c")]
    [InlineData("1.2.-3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-rc..1")]
    [InlineData("latest")]
    public void Text_that_is_no_version_is_refused(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out SemanticVersion? version));
        Assert.Null(version);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.9", 1)]
    [InlineData("1.10.0", "1.9.0", 1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("1.2.0", "1.2.0-rc.1", 1)]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.10", -1)]
    [InlineData("1.2.0-alpha", "1.2.0-1", 1)]
    [InlineData("1.2.0-rc", "1.2.0-rc.1", -1)]
    [InlineData("1.2.0", "v1.2.0+build", 0)]
    [InlineData("1.1.0", "1.2.0", -1)]
    public void Versions_are_compared_by_the_rules_of_semver(string left, string right, int expected)
    {
        Assert.True(SemanticVersion.TryParse(left, out SemanticVersion? first));
        Assert.True(SemanticVersion.TryParse(right, out SemanticVersion? second));

        Assert.Equal(expected, Math.Sign(SemanticVersion.Compare(first, second)));
        Assert.Equal(expected > 0, first.IsNewerThan(second));
    }
}
