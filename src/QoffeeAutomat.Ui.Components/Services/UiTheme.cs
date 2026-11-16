using MudBlazor;

namespace QoffeeAutomat.Ui.Components.Services;

/// <summary>
/// The MudBlazor theme: the colours of the brand (<c>assets/brand/README.md</c>) in a light and a dark palette. The app
/// follows the system setting (<c>prefers-color-scheme</c>); there is no switch of its own. No web font is requested:
/// the fonts of the system are used, because the PC may be offline and nothing may leave it (PLAN.md D-015).
/// </summary>
public static class UiTheme
{
    /// <summary>The background colour of the brand, used as the primary colour of the light theme.</summary>
    public const string BrandBackground = "#17424F";

    /// <summary>The accent colour of the brand on light backgrounds.</summary>
    public const string BrandAccentOnLight = "#C9772B";

    /// <summary>The accent colour of the brand on dark backgrounds.</summary>
    public const string BrandAccent = "#F2A65A";

    /// <summary>Creates the theme.</summary>
    /// <returns>The theme with both palettes.</returns>
    public static MudTheme Create()
    {
        return new MudTheme
        {
            PaletteLight = new PaletteLight
            {
                Primary = BrandBackground,
                Secondary = BrandAccentOnLight,
                AppbarBackground = BrandBackground,
            },
            PaletteDark = new PaletteDark
            {
                Primary = BrandAccent,
                Secondary = BrandAccent,
                AppbarBackground = BrandBackground,
            },
        };
    }
}
