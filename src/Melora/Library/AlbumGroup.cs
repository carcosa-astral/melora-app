using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Melora.Library;

/// <summary>An album as shown in the list: header text plus lazily loaded cover art.</summary>
/// <remarks>
/// The decoded art is only held weakly. While its row is on screen the list keeps it alive; once
/// scrolled away it can be collected, and is read from disk again if the row comes back.
/// </remarks>
public sealed class AlbumGroup : ObservableObject
{
    static readonly string[] CoverNames = ["cover", "folder", "front", "album"];
    static readonly string[] CoverExtensions = [".jpg", ".jpeg", ".png"];

    /// <summary>Album headers in Music are 168 px.</summary>
    public const int AlbumArtSize = 168;

    /// <summary>DJ pictures on Mixes headings are 44 px.</summary>
    public const int DjArtSize = 44;

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public int Year { get; init; }

    /// <summary>Any track of the album; used to find embedded art and the album folder.</summary>
    public required string SampleTrackPath { get; init; }

    public string Subtitle => Year > 0 ? $"{Artist}  ·  {Year}" : Artist;

    /// <summary>The size the art is shown at, in device-independent pixels.</summary>
    public int ArtSize { get; init; } = AlbumArtSize;

    readonly WeakReference<ImageSource?> _art = new(null);
    bool _loading;
    bool _noArt;

    public ImageSource? Art
    {
        get
        {
            if (_art.TryGetTarget(out var art)) return art;
            if (!_loading && !_noArt)
            {
                _loading = true;
                Task.Run(LoadArt);
            }
            return null;
        }
    }

    void LoadArt()
    {
        try
        {
            var bytes = ReadArtBytes();
            if (bytes is null)
            {
                _noArt = true;
                return;
            }

            _art.SetTarget(Decode(bytes, ArtSize));
            OnPropertyChanged(nameof(Art));
        }
        catch
        {
            // Unreadable art just leaves the placeholder.
            _noArt = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Decodes an image for showing at <paramref name="size"/> pixels wide. Usable from any thread.</summary>
    public static ImageSource Decode(byte[] bytes, int size)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        // Twice the shown size stays sharp up to 200% display scaling.
        image.DecodePixelWidth = size * 2;
        image.StreamSource = new MemoryStream(bytes);
        image.EndInit();

        // A BitmapImage keeps its source stream (the whole original file) for as long as it
        // lives; a copy of just the decoded pixels lets that go.
        var pixels = new CachedBitmap(image, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        pixels.Freeze();
        return pixels;
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

        // For a track in CD1\ and the like, the album's cover is often in the folder above.
        var dirs = LibraryService.DiscFolderNumber(System.IO.Path.GetFileName(dir)) > 0 && System.IO.Path.GetDirectoryName(dir) is { } parent
            ? new[] { dir, parent }
            : [dir];
        foreach (var folder in dirs)
            foreach (var name in CoverNames)
                foreach (var ext in CoverExtensions)
                {
                    var candidate = System.IO.Path.Combine(folder, name + ext);
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
