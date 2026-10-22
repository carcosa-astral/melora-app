using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using Melora.Audio;
using Melora.Library;
using Melora.Playback;

namespace Melora;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    const int MaxSkipsOnError = 20;
    const double RestartThresholdSeconds = 3;

    readonly LibraryDb _db;
    readonly AudioEngine _engine = new();
    readonly PlayQueue _queue = new();
    readonly DispatcherTimer _timer;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    List<Track> _tracks = [];

    public LibraryService Music { get; }

    public MainViewModel()
    {
        _db = new LibraryDb(AppPaths.Database);
        Music = new LibraryService(_db, LibraryKind.Music);
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
    }

    /// <summary>Starts audio, shows the library from the database, then rescans in the background.</summary>
    public async Task StartAsync()
    {
        _engine.Initialize();
        _engine.Ended += () => _dispatcher.BeginInvoke(() => PlayTrack(_queue.Next()));

        _engine.Volume = double.TryParse(_db.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 1.0;
        _queue.SetShuffle(_db.GetSetting("shuffle") == "1");
        _queue.Repeat = Enum.TryParse<RepeatMode>(_db.GetSetting("repeat"), out var r) ? r : RepeatMode.All;
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Shuffle));
        OnRepeatChanged();

        Music.Changed += () => _dispatcher.BeginInvoke(Reload);
        Reload();
        Music.Rewatch();
        _timer.Start();
        await RescanAsync();
    }

    public async Task RescanAsync()
    {
        IsScanning = true;
        try
        {
            if (await Music.ScanAsync()) Reload();
        }
        finally
        {
            IsScanning = false;
        }
    }

    void Reload()
    {
        _tracks = Music.Load();
        var view = new ListCollectionView(_tracks);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Track.Group)));
        TracksView = view;
        HighlightPlaying();

        var total = TimeSpan.FromTicks(_tracks.Sum(t => t.Duration.Ticks));
        Summary = _tracks.Count == 0 ? "" : $"{_tracks.Count:N0} tracks  ·  {FormatTotal(total)}";
        OnPropertyChanged(nameof(IsEmpty));
    }

    static string FormatTotal(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalHours}h {t.Minutes}m";

    // ---- Bindable state ----

    ListCollectionView? _tracksView;
    public ListCollectionView? TracksView { get => _tracksView; private set => Set(ref _tracksView, value); }

    public bool IsEmpty => _tracks.Count == 0;

    bool _isScanning;
    public bool IsScanning { get => _isScanning; private set => Set(ref _isScanning, value); }

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    Track? _current;
    public Track? Current
    {
        get => _current;
        private set
        {
            if (!Set(ref _current, value)) return;
            HighlightPlaying();
            OnPropertyChanged(nameof(NowPlaying));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    public string NowPlaying => _current is null ? "Stopped" : $"{_current.Artist}  –  {_current.Title}";

    public string WindowTitle => _current is null ? "Melora" : $"{_current.Artist} – {_current.Title}  ·  Melora";

    bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    /// <summary>Set while the user drags the seek bar, so the timer doesn't fight them.</summary>
    public bool IsSeeking { get; set; }

    double _position;
    public double Position
    {
        get => _position;
        set { if (Set(ref _position, value)) OnPropertyChanged(nameof(TimeText)); }
    }

    const double NoLength = 1;

    double _length = NoLength;
    /// <summary>Track length in seconds; never zero, so an empty seek bar draws empty rather than full.</summary>
    public double Length { get => _length; private set => Set(ref _length, value > 0 ? value : NoLength); }

    public string TimeText => _current is null
        ? "0:00 / 0:00"
        : $"{Track.FormatTime(TimeSpan.FromSeconds(_position))} / {Track.FormatTime(TimeSpan.FromSeconds(_length))}";

    string _formatText = "";
    public string FormatText { get => _formatText; private set => Set(ref _formatText, value); }

    public double Volume
    {
        get => _engine.Volume * 100;
        set
        {
            _engine.Volume = value / 100;
            OnPropertyChanged();
            _db.SetSetting("volume", _engine.Volume.ToString(CultureInfo.InvariantCulture));
        }
    }

    public bool Shuffle
    {
        get => _queue.Shuffle;
        set
        {
            _queue.SetShuffle(value);
            OnPropertyChanged();
            _db.SetSetting("shuffle", value ? "1" : "0");
        }
    }

    public RepeatMode Repeat => _queue.Repeat;

    public bool RepeatActive => _queue.Repeat != RepeatMode.Off;

    public string RepeatGlyph => _queue.Repeat == RepeatMode.One ? "" : "";

    public string RepeatTip => _queue.Repeat switch
    {
        RepeatMode.Off => "Repeat: off",
        RepeatMode.All => "Repeat: all",
        _ => "Repeat: one",
    };

    public void CycleRepeat()
    {
        _queue.Repeat = _queue.Repeat switch
        {
            RepeatMode.All => RepeatMode.One,
            RepeatMode.One => RepeatMode.Off,
            _ => RepeatMode.All,
        };
        _db.SetSetting("repeat", _queue.Repeat.ToString());
        OnRepeatChanged();
    }

    void OnRepeatChanged()
    {
        OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(RepeatActive));
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatTip));
    }

    // ---- Transport ----

    List<Track> VisibleTracks() => TracksView?.Cast<Track>().ToList() ?? [];

    /// <summary>Double-click: play the current view starting at <paramref name="track"/>.</summary>
    public void PlayFrom(Track track) => PlayTrack(_queue.Start(VisibleTracks(), track));

    /// <summary>Play/pause. With nothing loaded, starts the view (shuffled if shuffle is on).</summary>
    public void PlayPause(Track? selected)
    {
        if (_engine.IsLoaded)
        {
            if (_engine.IsPlaying) _engine.Pause(); else _engine.Resume();
            IsPlaying = _engine.IsPlaying;
            return;
        }
        PlayTrack(_queue.Start(VisibleTracks(), Shuffle ? null : selected));
    }

    public void Next()
    {
        if (_engine.IsLoaded) PlayTrack(_queue.Next(manual: true));
        else PlayPause(null);
    }

    public void Previous()
    {
        if (_engine.IsLoaded && _engine.Position > RestartThresholdSeconds)
            _engine.Position = 0;
        else if (_engine.IsLoaded)
            PlayTrack(_queue.Previous());
    }

    public void SeekTo(double seconds)
    {
        _engine.Position = seconds;
        Position = seconds;
    }

    void PlayTrack(Track? track)
    {
        // Skip files BASS can't open, but don't spin forever on a folder of broken files.
        for (var attempt = 0; track is not null && attempt < MaxSkipsOnError; attempt++)
        {
            if (_engine.Play(track.Path))
            {
                Current = track;
                Length = _engine.Length;
                Position = 0;
                IsPlaying = true;
                FormatText = DescribeFormat(track);
                return;
            }
            track = _queue.Next(manual: true);
        }

        _engine.Stop();
        Current = null;
        IsPlaying = false;
        Position = 0;
        Length = NoLength;
        FormatText = "";
    }

    string DescribeFormat(Track track)
    {
        if (_engine.Format is not { } format) return track.Codec;
        var (rate, channels, kbps) = format;
        var layout = channels switch { 1 => "mono", 2 => "stereo", _ => $"{channels} ch" };
        return $"{track.Codec}  ·  {rate / 1000.0:0.#} kHz  ·  {kbps:N0} kbps  ·  {layout}";
    }

    void HighlightPlaying()
    {
        var path = _current?.Path;
        foreach (var t in _tracks)
            t.IsPlaying = path is not null && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    void Tick()
    {
        IsPlaying = _engine.IsPlaying;
        if (!IsSeeking && _engine.IsLoaded) Position = _engine.Position;
    }

    public void Dispose()
    {
        _timer.Stop();
        Music.Dispose();
        _engine.Dispose();
    }
}
