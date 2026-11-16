namespace QoffeeAutomat.Ui.Tests.Support;

/// <summary>Facts about the globalization mode of the test run.</summary>
internal static class Globalization
{
    /// <summary>True in invariant mode (no ICU): only the invariant culture exists, so German texts cannot be selected.</summary>
    public static bool IsInvariant => AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant) && invariant;
}
