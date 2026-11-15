namespace QoffeeAutomat.Contracts.Config;

/// <summary>The common header of every configuration document (PLAN.md S06).</summary>
public interface IConfigDocument
{
    /// <summary>Version of the document schema.</summary>
    int SchemaVersion { get; }

    /// <summary>Incremented by the store on every save; null for a document that was not stored yet.</summary>
    int? Revision { get; }

    /// <summary>Time of the last save.</summary>
    DateTimeOffset? UpdatedAt { get; }

    /// <summary>Where the document was authored, see <see cref="DocumentOrigins"/>; null means local.</summary>
    string? Origin { get; }
}
