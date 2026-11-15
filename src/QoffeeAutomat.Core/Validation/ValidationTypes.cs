using System.Globalization;

namespace QoffeeAutomat.Core.Validation;

/// <summary>One problem found by <see cref="DocumentValidator"/> or <see cref="VariableBinder"/>.</summary>
/// <param name="Path">A JSON pointer (RFC 6901) to the offending member, for example <c>/parameters/0/min</c>.</param>
/// <param name="Code">A stable machine-readable code, see <see cref="ValidationCodes"/>.</param>
/// <param name="Message">A human-readable explanation in English. It never contains the value of a variable.</param>
public sealed record ValidationError(string Path, string Code, string Message);

/// <summary>The outcome of a validation: a structured list of errors, empty when valid.</summary>
/// <param name="Errors">The errors in document order.</param>
public sealed record ValidationResult(IReadOnlyList<ValidationError> Errors)
{
    /// <summary>True when there are no errors.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>The codes of <see cref="ValidationError.Code"/>.</summary>
public static class ValidationCodes
{
    /// <summary>A mandatory member is missing or empty.</summary>
    public const string Required = "required";

    /// <summary>The value is malformed or not allowed.</summary>
    public const string Invalid = "invalid";

    /// <summary>The value is well formed but not supported by this version.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The value is not unique.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>The member does not apply to the type of its parent.</summary>
    public const string NotApplicable = "not_applicable";

    /// <summary>Two members exclude each other.</summary>
    public const string Conflict = "conflict";

    /// <summary>Sequences call each other in a cycle.</summary>
    public const string Cycle = "cycle";

    /// <summary>A reference names something that does not exist.</summary>
    public const string UnknownReference = "unknown_reference";

    /// <summary>A variable is not declared by the command.</summary>
    public const string UnknownVariable = "unknown_variable";

    /// <summary>A value has the wrong JSON type.</summary>
    public const string TypeMismatch = "type_mismatch";

    /// <summary>A number is outside min and max.</summary>
    public const string Range = "range";

    /// <summary>A number does not lie on the step grid.</summary>
    public const string Step = "step";

    /// <summary>A value is not one of the options.</summary>
    public const string Options = "options";

    /// <summary>A string is longer than maxLength.</summary>
    public const string MaxLength = "max_length";

    /// <summary>A string does not match the pattern.</summary>
    public const string Pattern = "pattern";

    /// <summary>Matching the pattern exceeded the 100 ms timeout.</summary>
    public const string PatternTimeout = "pattern_timeout";

    /// <summary>The pattern is not a valid regular expression.</summary>
    public const string InvalidPattern = "invalid_pattern";
}

/// <summary>Helpers for JSON pointers (RFC 6901).</summary>
public static class JsonPointer
{
    /// <summary>Escapes a reference token: <c>~</c> becomes <c>~0</c> and <c>/</c> becomes <c>~1</c>.</summary>
    /// <param name="token">The raw token.</param>
    /// <returns>The escaped token.</returns>
    public static string Escape(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    /// <summary>Appends an object member to a pointer.</summary>
    /// <param name="path">The parent pointer.</param>
    /// <param name="member">The raw member name.</param>
    /// <returns>The pointer to the member.</returns>
    public static string Member(string path, string member)
    {
        return path + "/" + Escape(member);
    }

    /// <summary>Appends an array index to a pointer.</summary>
    /// <param name="path">The parent pointer.</param>
    /// <param name="index">The zero-based index.</param>
    /// <returns>The pointer to the element.</returns>
    public static string Index(string path, int index)
    {
        return path + "/" + index.ToString(CultureInfo.InvariantCulture);
    }
}
