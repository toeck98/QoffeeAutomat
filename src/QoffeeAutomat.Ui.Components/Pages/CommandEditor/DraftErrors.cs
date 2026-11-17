using QoffeeAutomat.Contracts.Api;

namespace QoffeeAutomat.Ui.Components.Pages.CommandEditor;

/// <summary>
/// The problems of a draft by JSON pointer, so that every field can show its own: the problems that the editor finds by
/// itself (a number that is not a number) and the ones that the agent reports through <c>/commands/validate</c> or when a
/// save is refused. A problem found here hides the agent's problems at the same path, which would say the same.
/// </summary>
public sealed class DraftErrors
{
    /// <summary>No problems.</summary>
    public static readonly DraftErrors Empty = new([]);

    private readonly Dictionary<string, List<ApiValidationError>> _byPath = new(StringComparer.Ordinal);

    private DraftErrors(IReadOnlyList<ApiValidationError> all)
    {
        All = all;
        foreach (ApiValidationError error in all)
        {
            if (!_byPath.TryGetValue(error.Path, out List<ApiValidationError>? list))
            {
                list = [];
                _byPath[error.Path] = list;
            }

            list.Add(error);
        }
    }

    /// <summary>Every problem, the editor's own first.</summary>
    public IReadOnlyList<ApiValidationError> All { get; }

    /// <summary>The number of problems.</summary>
    public int Count => All.Count;

    /// <summary>Combines the problems of the editor and of the agent.</summary>
    /// <param name="local">The problems the editor found.</param>
    /// <param name="server">The problems the agent reported.</param>
    /// <returns>The combined problems.</returns>
    public static DraftErrors From(IEnumerable<ApiValidationError> local, IEnumerable<ApiValidationError> server)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(server);

        List<ApiValidationError> own = [.. local];
        var covered = new HashSet<string>(own.Select(error => error.Path), StringComparer.Ordinal);
        return new DraftErrors([.. own, .. server.Where(error => !covered.Contains(error.Path))]);
    }

    /// <summary>The problems at a path.</summary>
    /// <param name="path">A JSON pointer such as <c>/parameters/0/min</c>.</param>
    /// <returns>The problems; empty when there are none.</returns>
    public IReadOnlyList<ApiValidationError> For(string path)
    {
        return _byPath.TryGetValue(path, out List<ApiValidationError>? list) ? list : [];
    }

    /// <summary>Tells whether a path has a problem.</summary>
    /// <param name="path">A JSON pointer.</param>
    /// <returns>True when there is at least one problem.</returns>
    public bool Has(string path) => _byPath.ContainsKey(path);
}
