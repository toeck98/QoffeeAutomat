using System.Text.Json.Nodes;
using QoffeeAutomat.Core.Config;

namespace QoffeeAutomat.Core.Commands.Conditions;

/// <summary>
/// The migration of command documents from schema version 1 to 2 (T705, T706). Version 2 only adds the optional members
/// <c>conditions</c> and <c>secrets</c>, so a version 1 document is already a valid version 2 document; the step exists so
/// that the stores rewrite the file with the new version number (keeping the old file as a backup) and so that the chain
/// of migrations has no gap. Nothing in a version 1 document is changed.
/// </summary>
public sealed class CommandSchemaV2Migration : IDocumentMigration
{
    /// <inheritdoc />
    public ConfigKind Kind => ConfigKind.Command;

    /// <inheritdoc />
    public int FromVersion => 1;

    /// <inheritdoc />
    public JsonObject Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return document;
    }
}
