using System.Globalization;
using System.Windows;

namespace Melora;

/// <summary>
/// Widths of a track list's resizable columns. The title column takes whatever is left,
/// so only Artist and Length are stored. Shared by the header and every row.
/// </summary>
public sealed class ColumnLayout : ObservableObject
{
    const double MinArtist = 80, MaxArtist = 640;
    const double MinLength = 48, MaxLength = 160;

    double _artist = 220;
    double _length = 64;

    public double Artist
    {
        get => _artist;
        set { if (Set(ref _artist, Math.Clamp(value, MinArtist, MaxArtist))) OnPropertyChanged(nameof(ArtistWidth)); }
    }

    public double Length
    {
        get => _length;
        set { if (Set(ref _length, Math.Clamp(value, MinLength, MaxLength))) OnPropertyChanged(nameof(LengthWidth)); }
    }

    public GridLength ArtistWidth => new(_artist);
    public GridLength LengthWidth => new(_length);

    /// <summary>Dragging the Title | Artist divider: moving right shrinks Artist.</summary>
    public void DragTitleArtist(double dx) => Artist -= dx;

    /// <summary>Dragging the Artist | Length divider: width moves between the two.</summary>
    public void DragArtistLength(double dx)
    {
        dx = Math.Clamp(dx, MinArtist - _artist, _length - MinLength);
        dx = Math.Clamp(dx, _length - MaxLength, MaxArtist - _artist);
        Artist += dx;
        Length -= dx;
    }

    public string Save() => string.Create(CultureInfo.InvariantCulture, $"{_artist:0},{_length:0}");

    public void Load(string? saved)
    {
        var parts = saved?.Split(',');
        if (parts is not { Length: 2 }) return;
        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var artist)) Artist = artist;
        if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var length)) Length = length;
    }
}
