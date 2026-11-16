using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QoffeeAutomat.SessionHost.Tray;

/// <summary>
/// The four tray icons, all derived from <c>assets/brand/app.ico</c> (PLAN.md S05, T310): the brand icon with a small status
/// badge in the lower right corner, green for connected, amber for not paired, red with a pause bar for paused and grey for
/// the agent being away. The icons are made once when the set is created; the handles are released with the set.
/// </summary>
public sealed class TrayIconSet : IDisposable
{
    /// <summary>The name of the embedded resource that holds <c>app.ico</c>.</summary>
    public const string ResourceName = "QoffeeAutomat.SessionHost.app.ico";

    private readonly Dictionary<TrayIconState, Icon> _icons = [];
    private readonly List<nint> _handles = [];

    /// <summary>Creates the icons for the size of the notification area on this computer.</summary>
    /// <exception cref="InvalidOperationException">The icon resource is missing.</exception>
    public TrayIconSet()
    {
        using Stream stream = typeof(TrayIconSet).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"The resource {ResourceName} is missing.");
        using var brand = new Icon(stream, SystemInformation.SmallIconSize);
        using Bitmap baseImage = brand.ToBitmap();

        foreach (TrayIconState state in Enum.GetValues<TrayIconState>())
        {
            _icons[state] = Render(baseImage, state);
        }
    }

    /// <summary>Returns the icon of a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>The icon; it belongs to the set.</returns>
    public Icon Get(TrayIconState state)
    {
        return _icons[state];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }

        _icons.Clear();
        foreach (nint handle in _handles)
        {
            _ = NativeMethods.DestroyIcon(handle);
        }

        _handles.Clear();
    }

    private static Color BadgeColor(TrayIconState state)
    {
        return state switch
        {
            TrayIconState.Connected => Color.FromArgb(0x2E, 0x9E, 0x4F),
            TrayIconState.NotPaired => Color.FromArgb(0xE0, 0xA1, 0x00),
            TrayIconState.Paused => Color.FromArgb(0xD0, 0x32, 0x32),
            _ => Color.FromArgb(0x8A, 0x8A, 0x8A),
        };
    }

    private Icon Render(Bitmap baseImage, TrayIconState state)
    {
        using var image = new Bitmap(baseImage.Width, baseImage.Height);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.DrawImage(baseImage, 0, 0, image.Width, image.Height);

            int diameter = Math.Max(6, image.Width * 5 / 11);
            var badge = new Rectangle(image.Width - diameter, image.Height - diameter, diameter - 1, diameter - 1);
            using var fill = new SolidBrush(BadgeColor(state));
            using var outline = new Pen(Color.White, Math.Max(1f, image.Width / 16f));
            graphics.FillEllipse(fill, badge);
            graphics.DrawEllipse(outline, badge);

            if (state == TrayIconState.Paused)
            {
                // Two white bars, the usual pause symbol.
                int bar = Math.Max(1, diameter / 6);
                int top = badge.Top + (diameter / 4);
                int height = Math.Max(2, diameter / 2);
                int left = badge.Left + (diameter / 2) - (bar * 3 / 2) - 1;
                graphics.FillRectangle(Brushes.White, left, top, bar, height);
                graphics.FillRectangle(Brushes.White, left + (bar * 2), top, bar, height);
            }
        }

        nint handle = image.GetHicon();
        _handles.Add(handle);

        // The icon does not own the handle; the set destroys it.
        return Icon.FromHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(nint handle);
    }
}
