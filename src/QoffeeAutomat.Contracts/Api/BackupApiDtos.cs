namespace QoffeeAutomat.Contracts.Api;

/// <summary>One document of a configuration archive in an import preview.</summary>
/// <param name="Kind">The kind of document: <c>command</c>, <c>sensor</c>, <c>script</c>, <c>metrics</c> or <c>settings</c>.</param>
/// <param name="Id">The id of the document (the file name of a script).</param>
/// <param name="Change">What the import would do: <c>new</c>, <c>changed</c> or <c>unchanged</c>.</param>
public sealed record ImportPreviewItem(string Kind, string Id, string Change);

/// <summary>
/// Response of <c>POST /api/v1/config/import/preview</c>: what an import of the archive would do, without doing it. When any
/// document is invalid, <see cref="Valid"/> is false, <see cref="Errors"/> names the problems and the import applies nothing.
/// </summary>
/// <param name="Valid">True when every document of the archive is valid.</param>
/// <param name="Items">The documents of the archive and what would happen to each; empty when the archive is not readable.</param>
/// <param name="Errors">The validation errors; the path of an error starts with the entry in the archive.</param>
public sealed record ConfigImportPreview(bool Valid, IReadOnlyList<ImportPreviewItem> Items, IReadOnlyList<ApiValidationError> Errors);

/// <summary>A kept revision of a document.</summary>
/// <param name="Revision">The revision number.</param>
/// <param name="UpdatedAt">When the revision was saved; absent when unknown.</param>
public sealed record RevisionEntry(int Revision, DateTimeOffset? UpdatedAt = null);

/// <summary>Response of <c>GET /api/v1/config/revisions/{kind}/{id}</c>: the kept revisions of one document, newest first.</summary>
/// <param name="Kind">The kind of document: <c>command</c>, <c>sensor</c> or <c>settings</c>.</param>
/// <param name="Id">The id of the document.</param>
/// <param name="Current">The revision that is stored now; absent when the document does not exist (it was deleted).</param>
/// <param name="Revisions">The kept revisions.</param>
public sealed record RevisionList(string Kind, string Id, int? Current, IReadOnlyList<RevisionEntry> Revisions);
