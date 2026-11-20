using QoffeeAutomat.Core.Validation;

namespace QoffeeAutomat.Hub.Config;

/// <summary>The outcome class of an operation of the configuration service; the endpoints map it to an HTTP status.</summary>
public enum ConfigStatus
{
    /// <summary>The operation succeeded.</summary>
    Ok,

    /// <summary>The operation created something.</summary>
    Created,

    /// <summary>The profile, document, revision or assignment does not exist.</summary>
    NotFound,

    /// <summary>The content is not acceptable; <see cref="ConfigResult{T}.Errors"/> says why.</summary>
    Invalid,

    /// <summary>The operation conflicts with the state: a duplicate, or a document that is still in use.</summary>
    Conflict,

    /// <summary>The caller's role is not sufficient.</summary>
    Forbidden,
}

/// <summary>The result of an operation of the configuration service.</summary>
/// <typeparam name="T">The type of the value.</typeparam>
/// <param name="Status">The outcome class.</param>
/// <param name="Value">The value when the operation succeeded.</param>
/// <param name="Errors">The validation errors when <paramref name="Status"/> is <see cref="ConfigStatus.Invalid"/>.</param>
/// <param name="Code">A stable machine-readable code for the other failures.</param>
/// <param name="Message">A human-readable explanation for the other failures.</param>
public sealed record ConfigResult<T>(
    ConfigStatus Status,
    T? Value,
    IReadOnlyList<ValidationError> Errors,
    string? Code,
    string? Message)
{
    /// <summary>True when the operation succeeded.</summary>
    public bool IsSuccess => Status is ConfigStatus.Ok or ConfigStatus.Created;
}

/// <summary>Creates <see cref="ConfigResult{T}"/> values.</summary>
public static class ConfigResults
{
    /// <summary>A success.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Ok<T>(T value) => new(ConfigStatus.Ok, value, [], null, null);

    /// <summary>A success that created something.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Created<T>(T value) => new(ConfigStatus.Created, value, [], null, null);

    /// <summary>The thing does not exist.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="code">The code.</param>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> NotFound<T>(string code, string message) => new(ConfigStatus.NotFound, default, [], code, message);

    /// <summary>The content is not acceptable.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="errors">The errors with JSON pointers.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Invalid<T>(IReadOnlyList<ValidationError> errors) => new(ConfigStatus.Invalid, default, errors, null, null);

    /// <summary>The content is not acceptable: a single error.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="path">The JSON pointer.</param>
    /// <param name="code">The validation code.</param>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Invalid<T>(string path, string code, string message) => Invalid<T>([new ValidationError(path, code, message)]);

    /// <summary>The operation conflicts with the state.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="code">The code.</param>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Conflict<T>(string code, string message) => new(ConfigStatus.Conflict, default, [], code, message);

    /// <summary>The role is not sufficient.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="code">The code.</param>
    /// <param name="message">The explanation.</param>
    /// <returns>The result.</returns>
    public static ConfigResult<T> Forbidden<T>(string code, string message) => new(ConfigStatus.Forbidden, default, [], code, message);

    /// <summary>Carries a failure over to a result of another type.</summary>
    /// <typeparam name="TTo">The value type of the new result.</typeparam>
    /// <typeparam name="TFrom">The value type of the failed result.</typeparam>
    /// <param name="failed">The failed result.</param>
    /// <returns>The same failure with the new type.</returns>
    public static ConfigResult<TTo> Fail<TTo, TFrom>(ConfigResult<TFrom> failed)
    {
        ArgumentNullException.ThrowIfNull(failed);

        return new ConfigResult<TTo>(failed.Status, default, failed.Errors, failed.Code, failed.Message);
    }
}
