namespace QoffeeAutomat.SessionHost.Media;

/// <summary>
/// Turns the application user model id of a media session into a name that a person recognises (PLAN.md S11, T716): Windows
/// reports <c>Spotify.exe</c> for a classic program and <c>Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic</c> for a
/// packaged one.
/// </summary>
public static class MediaAppNames
{
    private const int MaxLength = 64;

    /// <summary>Derives the display name.</summary>
    /// <param name="appUserModelId">The id as Windows reports it.</param>
    /// <returns>The name, for example <c>Spotify</c> or <c>ZuneMusic</c>; null when the id is empty.</returns>
    public static string? From(string? appUserModelId)
    {
        string? id = appUserModelId?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        string name;
        int bang = id.LastIndexOf('!');
        if (bang >= 0)
        {
            // A packaged app: package family name, an exclamation mark and the application id inside the package.
            string application = id[(bang + 1)..];
            name = application.Length == 0 || string.Equals(application, "App", StringComparison.OrdinalIgnoreCase)
                ? PackageName(id[..bang])
                : application;
            name = LastSegment(name);
        }
        else
        {
            int separator = id.LastIndexOfAny(['\\', '/']);
            name = separator >= 0 ? id[(separator + 1)..] : id;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            return null;
        }

        return name.Length > MaxLength ? name[..MaxLength] : name;
    }

    private static string PackageName(string familyName)
    {
        // "Microsoft.ZuneMusic_8wekyb3d8bbwe": the part before the underscore is the package name.
        int underscore = familyName.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? familyName[..underscore] : familyName;
    }

    private static string LastSegment(string dotted)
    {
        int dot = dotted.LastIndexOf('.');
        return dot >= 0 && dot < dotted.Length - 1 ? dotted[(dot + 1)..] : dotted;
    }
}
