using System.Text.Json.Nodes;

namespace QoffeeAutomat.Core.Config;

/// <summary>
/// One step of the forward migration of documents (PLAN.md S13: config documents are migrated
/// forward automatically). A step turns a document of schema version <see cref="FromVersion"/>
/// into the shape of version <c>FromVersion + 1</c>; the store sets <c>schemaVersion</c> itself and
/// chains the steps until the current version is reached. Documents are migrated on the JSON tree,
/// before they are read into the typed records.
/// </summary>
public interface IDocumentMigration
{
    /// <summary>The kind of document the step applies to.</summary>
    ConfigKind Kind { get; }

    /// <summary>The schema version that the step upgrades.</summary>
    int FromVersion { get; }

    /// <summary>Upgrades a document by one schema version.</summary>
    /// <param name="document">The document in the old shape.</param>
    /// <returns>The document in the new shape (it may be the same instance, changed in place).</returns>
    JsonObject Migrate(JsonObject document);
}
