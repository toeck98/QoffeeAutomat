using QoffeeAutomat.Core.Commands.Conditions;

namespace QoffeeAutomat.Core.Config;

/// <summary>The forward migrations that ship with the agent. A store applies them in addition to <see cref="ConfigStoreOptions.Migrations"/>.</summary>
internal static class BuiltInMigrations
{
    /// <summary>All built-in steps.</summary>
    public static IReadOnlyList<IDocumentMigration> All { get; } = [new CommandSchemaV2Migration()];
}
