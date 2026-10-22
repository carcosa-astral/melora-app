namespace Melora.Library;

public enum LibraryKind { Music, Mixes }

public sealed class Track : ObservableObject
{
    public required string Path { get; init; }
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required AlbumGroup Group { get; init; }
    public int TrackNo { get; init; }
    public int DiscNo { get; init; }
    public TimeSpan Duration { get; init; }

    public string Codec => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    public string TrackNoText => TrackNo > 0 ? TrackNo.ToString("00") : "";

    public string LengthText => FormatTime(Duration);

    bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}
