namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// Turns the address of a page of this library (<c>/commands/new</c>, <c>/sensors/disk-free</c>) into the address under which the host serves
/// it. The agent UI serves every page at the address it declares, so <see cref="RootAppLinks"/> returns the path unchanged; the hub UI embeds
/// the pages below an agent (<c>agents/{id}/commands/new</c>) and, behind a path prefix such as Home Assistant ingress, needs addresses that
/// are relative to the base address instead of the root of the site (PLAN.md S20, D-033, T915).
/// </summary>
public interface IAppLinks
{
    /// <summary>Resolves a page address of the library.</summary>
    /// <param name="path">The path as the library declares it; it starts with a slash.</param>
    /// <returns>The address to use as <c>href</c> or as the target of a navigation.</returns>
    string Resolve(string path);
}

/// <summary>The links of the agent UI: every page is served at its declared path.</summary>
public sealed class RootAppLinks : IAppLinks
{
    /// <inheritdoc />
    public string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path;
    }
}
