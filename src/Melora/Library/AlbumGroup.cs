using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Melora.Library;

/// <summary>An album as shown in the list: header text plus lazily loaded cover art.</summary>
public sealed class AlbumGroup : ObservableObject
{
    static readonly string[] CoverNames = ["cover", "folder", "front", "album"];
    static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png"];
    const int ArtDecodeWidth = 360;

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public int Year { get; init; }

    /// <summary>Any track of the album; used to find embedded art and the album folder.</summary>
    public required string SampleTrackPath { get; init; }

    public string Subtitle => Year > 0 ? $"{Artist}  ·  {Year}" : Artist;

    ImageSource? _art;
    bool _artRequested;

    public ImageSource? Art
    {
        get
        {
            if (!_artRequested)
            {
                _artRequested = true;
                Task.Run(LoadArt);
            }
            return _art;
        }
    }

    void LoadArt()
    {
        try
        {
            var bytes = ReadArtBytes();
            if (bytes is null) return;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = ArtDecodeWidth;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            _art = image;
            OnPropertyChanged(nameof(Art));
        }
        catch
        {
            // Unreadable art just leaves the placeholder.
        }
    }

    Task<Color?>? _accent;

    /// <summary>The accent colour picked from this album's art (null for none or grey art). Worked out once.</summary>
    public Task<Color?> GetAccentAsync() => _accent ??= Task.Run(() =>
    {
        try
        {
            return ReadArtBytes() is { } bytes ? Theme.Accent.FromImage(bytes) : null;
        }
        catch
        {
            return (Color?)null;
        }
    });

    /// <summary>The raw cover image (folder image first, then embedded art), or null. Reads from disk.</summary>
    public byte[]? ReadArtBytes()
    {
        try
        {
            return FindFolderCover() ?? ReadEmbeddedCover();
        }
        catch
        {
            return null;
        }
    }

    byte[]? FindFolderCover()
    {
        var dir = System.IO.Path.GetDirectoryName(SampleTrackPath);
        if (dir is null) return null;
        foreach (var name in CoverNames)
            foreach (var ext in CoverExtensions)
            {
                var candidate = System.IO.Path.Combine(dir, name + ext);
                if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            }
        return null;
    }

    byte[]? ReadEmbeddedCover()
    {
        using var file = TagLib.File.Create(SampleTrackPath);
        var pictures = file.Tag.Pictures;
        if (pictures.Length == 0) return null;
        var front = pictures.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
        return front.Data.Data;
    }
}
