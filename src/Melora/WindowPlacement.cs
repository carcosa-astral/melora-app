using System.Globalization;
using System.Windows;

namespace Melora;

/// <summary>Where the main window was: its normal bounds and whether it was maximized.</summary>
public readonly record struct WindowPlacement(Rect Bounds, bool Maximized)
{
    // At least this much of the title bar has to land on a screen, or the saved position is ignored.
    const double MinVisibleWidth = 120;
    const double MinVisibleHeight = 40;

    public string Save() => string.Create(CultureInfo.InvariantCulture,
        $"{Bounds.Left:0},{Bounds.Top:0},{Bounds.Width:0},{Bounds.Height:0},{(Maximized ? 1 : 0)}");

    public static WindowPlacement? Load(string? saved)
    {
        var parts = saved?.Split(',');
        if (parts is not { Length: 5 }) return null;
        var n = new double[5];
        for (var i = 0; i < 5; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return null;
        if (n[2] <= 0 || n[3] <= 0) return null;
        return new WindowPlacement(new Rect(n[0], n[1], n[2], n[3]), n[4] != 0);
    }

    /// <summary>
    /// Whether the top of the window would be on some screen, e.g. not on a monitor that's since been unplugged.
    /// </summary>
    public bool IsVisibleOn(Rect virtualScreen)
    {
        var titleBar = new Rect(Bounds.Left, Bounds.Top, Bounds.Width, MinVisibleHeight);
        titleBar.Intersect(virtualScreen);
        return !titleBar.IsEmpty && titleBar.Width >= MinVisibleWidth && titleBar.Height >= MinVisibleHeight;
    }
}
