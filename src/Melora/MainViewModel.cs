using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Threading;
using Melora.Audio;
using Melora.Library;
using Melora.Playback;
using Melora.Radio;
using Melora.Scrobbling;

namespace Melora;

public enum LibraryTab { Music, Mixes, Radio }

public sealed class MainViewModel : ObservableObject, IDisposable
{
    const int MaxSkipsOnError = 20;
    const double RestartThresholdSeconds = 3;
    const double NoLength = 1;

    readonly LibraryDb _db;
    readonly AudioEngine _engine = new();
    readonly PlayQueue _queue = new();
    readonly DispatcherTimer _timer;
    readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    public TrackLibrary Music { get; }
    public TrackLibrary Mixes { get; }
    public Scrobbler Scrobbler { get; }
    public ObservableCollection<Station> Stations { get; } = [];

    public MainViewModel()
    {
        _db = new LibraryDb(AppPaths.Database);
        // Each library skips folders that belong to the other (e.g. Music\Mixes).
        LibraryService mixes = null!;
        var music = new LibraryService(_db, LibraryKind.Music, () => mixes.Folders);
        mixes = new LibraryService(_db, LibraryKind.Mixes, () => music.Folders);
        Music = new TrackLibrary(music, groupBy: nameof(Track.Group));
        Mixes = new TrackLibrary(mixes, groupBy: nameof(Track.Collection));
        Scrobbler = new Scrobbler(_db);

        foreach (var library in new[] { Music, Mixes })
        {
            library.PropertyChanged += OnLibraryChanged;
            library.Reloaded += HighlightPlaying;
        }
        Stations.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _dispatcher);
    }

    void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TrackLibrary.Summary)) OnPropertyChanged(nameof(Summary));
        if (e.PropertyName is nameof(TrackLibrary.IsScanning)) OnPropertyChanged(nameof(IsScanning));
    }

    /// <summary>Starts audio, shows the libraries from the database, then rescans in the background.</summary>
    public async Task StartAsync()
    {
        _engine.Initialize();
        _engine.Ended += generation => _dispatcher.BeginInvoke(() =>
        {
            if (generation == _engine.Generation) OnEnded();
        });
        _engine.MetadataChanged += generation => _dispatcher.BeginInvoke(() =>
        {
            if (generation == _engine.Generation) UpdateStreamTitle();
        });

        _engine.Volume = double.TryParse(_db.GetSetting("volume"), CultureInfo.InvariantCulture, out var v) ? v : 1.0;
        _queue.SetShuffle(_db.GetSetting("shuffle") == "1");
        _queue.Repeat = Enum.TryParse<RepeatMode>(_db.GetSetting("repeat"), out var r) ? r : RepeatMode.All;
        _tab = Enum.TryParse<LibraryTab>(_db.GetSetting("tab"), out var t) ? t : LibraryTab.Music;
        _minimizeToTray = _db.GetSetting("minimizeToTray") != "0";
        OnPropertyChanged(nameof(Volume));
        OnPropertyChanged(nameof(Shuffle));
        OnRepeatChanged();
        OnTabChanged();

        foreach (var (id, name, url) in _db.GetStations().OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Stations.Add(new Station { Id = id, Name = name, Url = url });

        foreach (var library in new[] { Music, Mixes })
        {
            var lib = library;
            lib.Service.Changed += () => _dispatcher.BeginInvoke(lib.Reload);
            lib.Reload();
            lib.Service.Rewatch();
        }

        _timer.Start();
        _ = Scrobbler.FlushAsync(); // anything left over from last time
        await RescanAsync();
    }

    public Task RescanAsync() => Task.WhenAll(Music.RescanAsync(), Mixes.RescanAsync());

    // ---- Tabs ----

    LibraryTab _tab;
    public LibraryTab Tab
    {
        get => _tab;
        set
        {
            if (!Set(ref _tab, value)) return;
            _db.SetSetting("tab", value.ToString());
            OnTabChanged();
        }
    }

    void OnTabChanged()
    {
        OnPropertyChanged(nameof(Tab));
        OnPropertyChanged(nameof(ShowMusic));
        OnPropertyChanged(nameof(ShowMixes));
        OnPropertyChanged(nameof(ShowRadio));
        OnPropertyChanged(nameof(Summary));
    }

    public bool ShowMusic => _tab == LibraryTab.Music;
    public bool ShowMixes => _tab == LibraryTab.Mixes;
    public bool ShowRadio => _tab == LibraryTab.Radio;

    public string Summary => _tab switch
    {
        LibraryTab.Music => Music.Summary,
        LibraryTab.Mixes => Mixes.Summary,
        _ => Stations.Count switch { 0 => "", 1 => "1 station", var n => $"{n} stations" },
    };

    public bool IsScanning => Music.IsScanning || Mixes.IsScanning;

    TrackLibrary LibraryFor(LibraryKind kind) => kind == LibraryKind.Mixes ? Mixes : Music;

    // ---- Window behaviour ----

    bool _minimizeToTray = true;
    /// <summary>Minimizing hides Melora to the tray instead of the taskbar.</summary>
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { if (Set(ref _minimizeToTray, value)) _db.SetSetting("minimizeToTray", value ? "1" : "0"); }
    }

    /// <summary>Tint the accent colour from the playing album's art.</summary>
    public bool AccentFromArt
    {
        get => _db.GetSetting("accentFromArt") != "0";
        set
        {
            _db.SetSetting("accentFromArt", value ? "1" : "0");
            OnPropertyChanged();
        }
    }

    /// <summary>Small bits of window state the view keeps between runs (placement, column widths).</summary>
    public string? GetViewSetting(string key) => _db.GetSetting("view." + key);

    public void SetViewSetting(string key, string value) => _db.SetSetting("view." + key, value);

    /// <summary>Whether the "still running in the tray" hint has been shown once already.</summary>
    public bool TrayHintShown
    {
        get => _db.GetSetting("trayHintShown") == "1";
        set => _db.SetSetting("trayHintShown", value ? "1" : "0");
    }

    // ---- Now playing ----

    Track? _currentTrack;
    Station? _currentStation;
    string? _streamTitle;
    string? _streamStatus;

    /// <summary>The station being played (or last played, while stopped).</summary>
    public Station? CurrentStation => _currentStation;

    public Track? CurrentTrack => _currentTrack;

    /// <summary>What the current station says is playing, if anything.</summary>
    public string? StreamTitle => _streamTitle;

    /// <summary>A track is loaded but paused.</summary>
    public bool IsPaused => _currentTrack is not null && !_isPlaying && _engine.IsLoaded;

    public string PlayPauseLabel => _isPlaying ? (IsLive ? "Stop" : "Pause") : "Play";

    public bool IsLive => _currentStation is not null;

    public bool CanSeek => _currentTrack is not null;

    public string NowPlaying
    {
        get
        {
            if (_currentTrack is { } t) return $"{t.Artist}  –  {t.Title}";
            if (_currentStation is { } s) return $"{s.Name}  ·  {_streamStatus ?? _streamTitle ?? "Live"}";
            return "Stopped";
        }
    }

    public string WindowTitle
    {
        get
        {
            if (_currentTrack is { } t) return $"{t.Artist} – {t.Title}  ·  Melora";
            if (_currentStation is { } s) return (_streamTitle is { } title ? $"{title}  ·  {s.Name}" : s.Name) + "  ·  Melora";
            return "Melora";
        }
    }

    void OnNowPlayingChanged()
    {
        OnPropertyChanged(nameof(NowPlaying));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(CanSeek));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(CurrentTrack));
        OnPropertyChanged(nameof(StreamTitle));
        OnPropertyChanged(nameof(PlayPauseLabel));
        if (_currentStation is { } s) s.NowPlaying = _streamStatus ?? _streamTitle;
    }

    bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(PlayPauseLabel));
            OnPropertyChanged(nameof(IsPaused));
        }
    }

    /// <summary>Set while the user drags the seek bar, so the timer doesn't fight them.</summary>
    public bool IsSeeking { get; set; }

    double _position;
    public double Position
    {
        get => _position;
        set { if (Set(ref _position, value)) OnPropertyChanged(nameof(TimeText)); }
    }

    double _length = NoLength;
    /// <summary>Track length in seconds; never zero, so an empty seek bar draws empty rather than full.</summary>
    public double Length { get => _length; private set => Set(ref _length, value > 0 ? value : NoLength); }

    double _listened;

    public string TimeText
    {
        get
        {
            if (_currentStation is not null) return _isPlaying ? $"LIVE  {Track.FormatTime(TimeSpan.FromSeconds(_listened))}" : "LIVE";
            if (_currentTrack is null) return "0:00 / 0:00";
            return $"{Track.FormatTime(TimeSpan.FromSeconds(_position))} / {Track.FormatTime(TimeSpan.FromSeconds(_length))}";
        }
    }

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

    static List<Track> VisibleTracks(TrackLibrary library) => library.View?.Cast<Track>().ToList() ?? [];

    /// <summary>Double-click on a track: play its library's list starting there.</summary>
    public void PlayFrom(Track track) => PlayTrack(_queue.Start(VisibleTracks(LibraryFor(track.Library)), track));

    /// <summary>
    /// Play/pause. Radio stops rather than pauses, since a paused live stream just falls behind.
    /// With nothing loaded, plays the selection (or the whole tab) of the current tab.
    /// </summary>
    public void PlayPause(object? selected)
    {
        if (_engine.IsLoaded)
        {
            if (_engine.IsLive) { StopStation(); return; }
            if (_engine.IsPlaying) _engine.Pause(); else _engine.Resume();
            IsPlaying = _engine.IsPlaying;
            return;
        }

        if (_tab == LibraryTab.Radio)
        {
            if ((selected as Station ?? _currentStation ?? Stations.FirstOrDefault()) is { } station) _ = PlayStation(station);
            return;
        }
        if (_currentStation is not null) { _ = PlayStation(_currentStation); return; }

        var library = _tab == LibraryTab.Mixes ? Mixes : Music;
        PlayTrack(_queue.Start(VisibleTracks(library), Shuffle ? null : selected as Track));
    }

    /// <summary>Stops playback entirely (radio keeps its station so Play tunes back in).</summary>
    public void Stop()
    {
        if (_engine.IsLive || _currentStation is not null) StopStation();
        else StopAll();
    }

    /// <summary>Next track, or the next station when listening to radio.</summary>
    public void Next()
    {
        if (_currentStation is { } station) _ = PlayStation(AdjacentStation(station, +1));
        else if (_engine.IsLoaded) PlayTrack(_queue.Next(manual: true));
        else PlayPause(null);
    }

    public void Previous()
    {
        if (_currentStation is { } station) _ = PlayStation(AdjacentStation(station, -1));
        else if (_engine.IsLoaded && _engine.Position > RestartThresholdSeconds) _engine.Position = 0;
        else if (_engine.IsLoaded) PlayTrack(_queue.Previous());
    }

    Station AdjacentStation(Station station, int step)
    {
        var index = Stations.IndexOf(station);
        if (index < 0 || Stations.Count == 0) return station;
        return Stations[(index + step + Stations.Count) % Stations.Count];
    }

    public void SeekTo(double seconds)
    {
        if (!CanSeek) return;
        _engine.Position = seconds;
        Position = seconds;
    }

    void PlayTrack(Track? track)
    {
        ClearStation();

        // Skip files BASS can't open, but don't spin forever on a folder of broken files.
        for (var attempt = 0; track is not null && attempt < MaxSkipsOnError; attempt++)
        {
            if (_engine.Play(track.Path))
            {
                _currentTrack = track;
                Length = _engine.Length;
                Position = 0;
                IsPlaying = true;
                Scrobbler.TrackStarted(track, TimeSpan.FromSeconds(_engine.Length));
                FormatText = DescribeFormat();
                HighlightPlaying();
                OnNowPlayingChanged();
                return;
            }
            track = _queue.Next(manual: true);
        }

        StopAll();
    }

    /// <summary>Tunes in to a station. Connecting can take a few seconds; the UI shows it meanwhile.</summary>
    public async Task PlayStation(Station station)
    {
        _currentTrack = null;
        Scrobbler.TrackStopped();
        ClearStation();
        _currentStation = station;
        station.IsPlaying = true;
        _streamTitle = null;
        _streamStatus = "Connecting…";
        IsPlaying = false;
        Position = 0;
        Length = NoLength;
        FormatText = "";
        HighlightPlaying();
        OnNowPlayingChanged();

        var result = await _engine.PlayUrlAsync(station.Url);
        if (result == StreamResult.Superseded || _currentStation != station) return;

        if (result == StreamResult.Failed)
        {
            _streamStatus = StationProbe.Describe(_engine.LastError);
            OnNowPlayingChanged();
            return;
        }

        _streamStatus = null;
        _listened = 0;
        IsPlaying = true;
        FormatText = DescribeFormat();
        UpdateStreamTitle();
    }

    void StopStation()
    {
        _engine.Stop();
        IsPlaying = false;
        _streamTitle = null;
        _streamStatus = "Stopped";
        FormatText = "";
        OnNowPlayingChanged();
    }

    void ClearStation()
    {
        if (_currentStation is null) return;
        _currentStation.IsPlaying = false;
        _currentStation.NowPlaying = null;
        _currentStation = null;
        _streamTitle = null;
        _streamStatus = null;
    }

    void StopAll()
    {
        _engine.Stop();
        _currentTrack = null;
        Scrobbler.TrackStopped();
        ClearStation();
        IsPlaying = false;
        Position = 0;
        Length = NoLength;
        FormatText = "";
        HighlightPlaying();
        OnNowPlayingChanged();
    }

    void OnEnded()
    {
        if (_currentStation is null)
        {
            PlayTrack(_queue.Next());
            return;
        }

        // A live stream only "ends" when the connection drops.
        _engine.Stop();
        IsPlaying = false;
        _streamTitle = null;
        _streamStatus = "Stream ended";
        FormatText = "";
        OnNowPlayingChanged();
    }

    void UpdateStreamTitle()
    {
        if (_currentStation is null) return;
        _streamTitle = _engine.StreamTitle;
        OnNowPlayingChanged();
    }

    string DescribeFormat()
    {
        if (_engine.Format is not { } format) return _currentTrack?.Codec ?? "";
        var (codec, rate, channels, kbps) = format;
        // Files are labelled by extension (FLAC/OPUS/MP3); streams by what BASS decoded.
        if (_currentTrack is not null) codec = _currentTrack.Codec;

        var layout = channels switch { 1 => "mono", 2 => "stereo", _ => $"{channels} ch" };
        var parts = new List<string> { codec, $"{rate / 1000.0:0.#} kHz" };
        if (kbps > 0) parts.Add($"{kbps:N0} kbps");
        parts.Add(layout);
        return string.Join("  ·  ", parts);
    }

    void HighlightPlaying()
    {
        var path = _currentTrack?.Path;
        foreach (var t in Music.Tracks.Concat(Mixes.Tracks))
            t.IsPlaying = path is not null && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase);
    }

    // Ticks are 250 ms: re-read the stream title every 3 s.
    const int TitleCheckTicks = 12;
    int _titleCheckTicks;
    readonly Stopwatch _tickClock = Stopwatch.StartNew();
    TimeSpan _lastTick;

    void Tick()
    {
        // Real time between ticks, counted only while a track is actually playing.
        var now = _tickClock.Elapsed;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (!_engine.IsLive && _currentTrack is not null && _engine.IsPlaying) Scrobbler.Listened(elapsed);

        if (_engine.IsLive)
        {
            _listened = _engine.Position;
            OnPropertyChanged(nameof(TimeText));

            // Some decoders never send the "title changed" event, so check now and then too.
            if (++_titleCheckTicks >= TitleCheckTicks)
            {
                _titleCheckTicks = 0;
                if (_engine.StreamTitle != _streamTitle) UpdateStreamTitle();
            }
            return;
        }

        IsPlaying = _engine.IsPlaying;
        if (!IsSeeking && _engine.IsLoaded) Position = _engine.Position;
    }

    // ---- Stations ----

    public Station AddStation(string name, string url)
    {
        var station = new Station { Id = _db.AddStation(name, url), Name = name, Url = url };
        InsertSorted(station);
        return station;
    }

    public void UpdateStation(Station station, string name, string url)
    {
        _db.UpdateStation(station.Id, name, url);
        var urlChanged = station.Url != url;
        station.Name = name;
        station.Url = url;
        Stations.Remove(station);
        InsertSorted(station);
        OnNowPlayingChanged();

        // Re-tune if the address of the station being played changed.
        if (urlChanged && _currentStation == station && _engine.IsLive) _ = PlayStation(station);
    }

    public void DeleteStation(Station station)
    {
        if (_currentStation == station) StopAll();
        _db.DeleteStation(station.Id);
        Stations.Remove(station);
    }

    void InsertSorted(Station station)
    {
        var index = 0;
        while (index < Stations.Count && string.Compare(Stations[index].Name, station.Name, StringComparison.CurrentCultureIgnoreCase) <= 0)
            index++;
        Stations.Insert(index, station);
    }

    public void Dispose()
    {
        _timer.Stop();
        Scrobbler.Dispose();
        Music.Service.Dispose();
        Mixes.Service.Dispose();
        _engine.Dispose();
    }
}
