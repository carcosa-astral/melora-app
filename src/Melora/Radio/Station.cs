namespace Melora.Radio;

/// <summary>A radio stream the user pasted in and named.</summary>
public sealed class Station : ObservableObject
{
    public required long Id { get; init; }

    string _name = "";
    public required string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    string _url = "";
    public required string Url
    {
        get => _url;
        set
        {
            if (!Set(ref _url, value)) return;
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;

    bool _isPlaying;
    /// <summary>True while this is the current station, connecting, playing or stopped.</summary>
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }

    string? _nowPlaying;
    /// <summary>What the station says is playing, or a status like "Connecting…".</summary>
    public string? NowPlaying
    {
        get => _nowPlaying;
        set
        {
            if (Set(ref _nowPlaying, value)) OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string Subtitle => NowPlaying ?? Host;
}
