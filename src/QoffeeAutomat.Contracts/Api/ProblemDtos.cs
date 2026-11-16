namespace QoffeeAutomat.Contracts.Api;

/// <summary>
/// One problem with a request body, as found by the validator of the configuration documents or the variable
/// binder (PLAN.md S06, S08).
/// </summary>
/// <param name="Path">A JSON pointer (RFC 6901) to the offending member of the request body, for example <c>/parameters/0/min</c>.</param>
/// <param name="Code">A stable machine-readable code such as <c>required</c>, <c>invalid</c> or <c>duplicate</c>.</param>
/// <param name="Message">A human-readable explanation in English; it never contains the value of a variable.</param>
public sealed record ApiValidationError(string Path, string Code, string Message);

/// <summary>
/// An error response of the management API: RFC 9457 problem details (<c>application/problem+json</c>) with
/// the machine-readable member <c>code</c> (PLAN.md S08).
/// </summary>
/// <param name="Type">A URI that identifies the problem type; the RFC 9110 section of the status code.</param>
/// <param name="Title">The reason phrase of the status.</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Detail">A human-readable explanation; never contains secrets.</param>
/// <param name="Code">A stable machine-readable code such as <c>validation_failed</c>, <c>not_found</c> or <c>revision_mismatch</c>.</param>
/// <param name="Errors">The validation errors of a 422 response, with JSON pointers into the request body.</param>
/// <param name="CurrentRevision">The revision that is stored, set when a write was refused because the expected revision is stale.</param>
/// <param name="RunId">The id of a run that was recorded although the request was rejected (for example <c>invalid_vars</c>).</param>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string? Detail = null,
    string? Code = null,
    IReadOnlyList<ApiValidationError>? Errors = null,
    int? CurrentRevision = null,
    string? RunId = null);

/// <summary>The result of <c>POST /api/v1/commands/validate</c>: a draft is checked, nothing is saved.</summary>
/// <param name="Valid">True when the draft has no errors.</param>
/// <param name="Errors">The errors with JSON pointers into the draft; empty when valid.</param>
public sealed record ValidationReport(bool Valid, IReadOnlyList<ApiValidationError> Errors);
