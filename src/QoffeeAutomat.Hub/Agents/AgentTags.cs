namespace QoffeeAutomat.Hub.Agents;

/// <summary>
/// The rules for tags (PLAN.md S20, D-030): a tag is the part after <c>tag:</c> in an assignment target, so it is short, lower case and
/// free of separators that would make a target ambiguous.
/// </summary>
public static class AgentTags
{
    /// <summary>The most tags an agent or a token may have.</summary>
    public const int MaxCount = 16;

    /// <summary>The longest tag.</summary>
    public const int MaxLength = 32;

    /// <summary>Tells whether a text is a valid tag: 1 to 32 characters, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c>, <c>-</c>, starting with a letter or a digit.</summary>
    /// <param name="tag">The text.</param>
    /// <returns>True when valid.</returns>
    public static bool IsValid(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > MaxLength)
        {
            return false;
        }

        if (!(char.IsAsciiLetterLower(tag[0]) || char.IsAsciiDigit(tag[0])))
        {
            return false;
        }

        foreach (char character in tag)
        {
            if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Validates and normalises a list of tags: trims, lower-cases, removes duplicates, sorts ordinally.</summary>
    /// <param name="tags">The tags as the caller sent them; null counts as none.</param>
    /// <param name="normalized">The normalised tags when the list is valid.</param>
    /// <param name="error">What is wrong when the list is invalid.</param>
    /// <returns>True when the list is valid.</returns>
    public static bool TryNormalize(IEnumerable<string?>? tags, out IReadOnlyList<string> normalized, out string? error)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        if (tags is not null)
        {
            foreach (string? raw in tags)
            {
                string tag = raw?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!IsValid(tag))
                {
                    normalized = [];
                    error = "A tag has 1 to 32 characters (a-z, 0-9, '.', '_', '-') and starts with a letter or a digit.";
                    return false;
                }

                set.Add(tag);
            }
        }

        if (set.Count > MaxCount)
        {
            normalized = [];
            error = "At most 16 tags are allowed.";
            return false;
        }

        normalized = [.. set];
        error = null;
        return true;
    }

    /// <summary>Merges two lists of valid tags.</summary>
    /// <param name="first">The first list.</param>
    /// <param name="second">The second list.</param>
    /// <returns>The sorted union, cut at <see cref="MaxCount"/>.</returns>
    public static IReadOnlyList<string> Union(IEnumerable<string> first, IEnumerable<string> second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        return [.. new SortedSet<string>(first.Concat(second), StringComparer.Ordinal).Take(MaxCount)];
    }
}
