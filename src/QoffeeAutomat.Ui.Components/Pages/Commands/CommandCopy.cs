using QoffeeAutomat.Contracts.Config;

namespace QoffeeAutomat.Ui.Components.Pages.Commands;

/// <summary>Makes the copy of a command for "duplicate" (T403).</summary>
public static class CommandCopy
{
    /// <summary>The longest id that the agent accepts (<c>^[a-z][a-z0-9_]{0,62}$</c>, PLAN.md S06).</summary>
    public const int MaxIdLength = 63;

    private const string Suffix = "_copy";

    /// <summary>Finds an id for the copy: <c>id_copy</c>, then <c>id_copy2</c>, <c>id_copy3</c> and so on.</summary>
    /// <param name="id">The id of the command that is copied.</param>
    /// <param name="taken">The ids that exist; the comparison is exact.</param>
    /// <returns>An id that is not in <paramref name="taken"/> and that the agent accepts.</returns>
    public static string NextId(string id, IReadOnlySet<string> taken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(taken);

        for (int number = 1; ; number++)
        {
            string tail = Suffix + (number == 1 ? string.Empty : number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            string stem = id.Length + tail.Length > MaxIdLength ? id[..(MaxIdLength - tail.Length)] : id;
            string candidate = stem + tail;
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Makes the copy. It is switched off and not shown to Home Assistant, so that a second entity with the same function
    /// does not appear there before the user has looked at it; everything else is the same. The agent sets the revision,
    /// the time and the origin when it stores the copy.
    /// </summary>
    /// <param name="source">The command that is copied.</param>
    /// <param name="id">The id of the copy, see <see cref="NextId"/>.</param>
    /// <param name="name">The name of the copy.</param>
    /// <returns>The new document.</returns>
    public static CommandDocument Create(CommandDocument source, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return source with
        {
            Id = id,
            Name = name,
            Enabled = false,
            ExposeToHa = false,
            Revision = null,
            UpdatedAt = null,
            Origin = null,
        };
    }
}
