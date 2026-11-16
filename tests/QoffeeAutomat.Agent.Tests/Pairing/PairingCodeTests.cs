using QoffeeAutomat.Agent.Link.Pairing;
using QoffeeAutomat.Platform.Simulated;

namespace QoffeeAutomat.Agent.Tests.Pairing;

/// <summary>Pairing code rules of T211: six digits, five minutes, single use, five attempts, one active code.</summary>
[Trait("Category", "Pairing")]
public sealed class PairingCodeTests
{
    private readonly SimulatedClock _clock = new();

    [Fact]
    public void A_code_has_six_digits_and_lives_five_minutes()
    {
        var codes = new PairingCodeManager(_clock);

        PairingCode code = codes.Start();

        Assert.Equal(6, code.Code.Length);
        Assert.All(code.Code, character => Assert.True(char.IsAsciiDigit(character)));
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMinutes(5), code.ExpiresAt);
        Assert.Equal(code, codes.GetActive());
    }

    [Fact]
    public void A_code_works_exactly_once()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();

        Assert.Equal(PairingAttempt.Accepted, codes.TryRedeem(code.Code));
        Assert.Equal(PairingAttempt.NoActiveCode, codes.TryRedeem(code.Code));
        Assert.Null(codes.GetActive());
    }

    [Fact]
    public void A_code_works_until_shortly_before_expiry()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();

        _clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        Assert.Equal(PairingAttempt.Accepted, codes.TryRedeem(code.Code));
    }

    [Fact]
    public void A_code_does_not_work_after_expiry()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();

        _clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Null(codes.GetActive());
        Assert.Equal(PairingAttempt.NoActiveCode, codes.TryRedeem(code.Code));
    }

    [Fact]
    public void Five_wrong_attempts_invalidate_the_active_code()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();
        string wrong = WrongCodeFor(code);

        for (int attempt = 1; attempt < PairingCodeManager.MaxFailedAttempts; attempt++)
        {
            Assert.Equal(PairingAttempt.WrongCode, codes.TryRedeem(wrong));
        }

        Assert.Equal(PairingAttempt.Invalidated, codes.TryRedeem(wrong));

        // The right code is useless now.
        Assert.Equal(PairingAttempt.NoActiveCode, codes.TryRedeem(code.Code));
        Assert.Null(codes.GetActive());
    }

    [Fact]
    public void Four_wrong_attempts_leave_the_code_valid()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();
        string wrong = WrongCodeFor(code);

        for (int attempt = 1; attempt < PairingCodeManager.MaxFailedAttempts; attempt++)
        {
            codes.TryRedeem(wrong);
        }

        Assert.Equal(PairingAttempt.Accepted, codes.TryRedeem(code.Code));
    }

    [Fact]
    public void Only_one_code_is_active_at_a_time()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode first = codes.Start();
        PairingCode second = codes.Start();

        // The first code stops working as soon as a new one exists (the codes differ unless the dice repeat).
        if (!string.Equals(first.Code, second.Code, StringComparison.Ordinal))
        {
            Assert.Equal(PairingAttempt.WrongCode, codes.TryRedeem(first.Code));
        }

        Assert.Equal(second, codes.GetActive());
        Assert.Equal(PairingAttempt.Accepted, codes.TryRedeem(second.Code));
    }

    [Fact]
    public void A_new_code_starts_with_a_fresh_attempt_counter()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode old = codes.Start();
        string wrong = WrongCodeFor(old);
        for (int attempt = 1; attempt < PairingCodeManager.MaxFailedAttempts; attempt++)
        {
            codes.TryRedeem(wrong);
        }

        PairingCode fresh = codes.Start();

        Assert.Equal(PairingAttempt.WrongCode, codes.TryRedeem(WrongCodeFor(fresh)));
        Assert.Equal(PairingAttempt.Accepted, codes.TryRedeem(fresh.Code));
    }

    [Fact]
    public void Cancel_discards_the_code()
    {
        var codes = new PairingCodeManager(_clock);
        PairingCode code = codes.Start();

        Assert.True(codes.Cancel());
        Assert.False(codes.Cancel());
        Assert.Equal(PairingAttempt.NoActiveCode, codes.TryRedeem(code.Code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    public void Malformed_codes_are_wrong_attempts(string? candidate)
    {
        var codes = new PairingCodeManager(_clock);
        codes.Start();

        Assert.Equal(PairingAttempt.WrongCode, codes.TryRedeem(candidate));
    }

    private static string WrongCodeFor(PairingCode code)
    {
        return string.Equals(code.Code, "000000", StringComparison.Ordinal) ? "000001" : "000000";
    }
}
