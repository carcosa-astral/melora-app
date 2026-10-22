using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Melora.Library;

namespace Melora.Scrobbling;

/// <summary>
/// Watches what's played, decides when a play counts, and sends it to every connected service.
/// Plays are written to a queue in the database first, so nothing is lost while offline.
/// </summary>
public sealed class Scrobbler : ObservableObject, IDisposable
{
    static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    readonly LibraryDb _db;
    readonly IReadOnlyList<IScrobbleService> _services;
    readonly SemaphoreSlim _flushLock = new(1, 1);
    readonly DispatcherTimer? _retryTimer;
    Listen? _current;

    sealed class Listen(Scrobble play)
    {
        public Scrobble Play { get; } = play;
        public TimeSpan Listened { get; set; }
        public bool Queued { get; set; }
    }

    public LastFmClient? LastFm { get; }
    public ListenBrainzClient? ListenBrainz { get; }

    public Scrobbler(LibraryDb db)
    {
        _db = db;
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Melora/{version}");

        LastFm = new LastFmClient(http, db);
        ListenBrainz = new ListenBrainzClient(http, db);
        _services = [LastFm, ListenBrainz];

        _retryTimer = new DispatcherTimer(RetryInterval, DispatcherPriority.Background, async (_, _) => await FlushAsync(),
            Dispatcher.CurrentDispatcher);
        _retryTimer.Start();
        _pending = _db.CountScrobbles();
    }

    /// <summary>For tests: a scrobbler over the given services, with no retry timer.</summary>
    public Scrobbler(LibraryDb db, IReadOnlyList<IScrobbleService> services)
    {
        _db = db;
        _services = services;
        _pending = _db.CountScrobbles();
    }

    /// <summary>Whether plays from the Mixes library count. Radio never does.</summary>
    public bool ScrobbleMixes
    {
        get => _db.GetSetting("scrobbleMixes") != "0";
        set
        {
            _db.SetSetting("scrobbleMixes", value ? "1" : "0");
            OnPropertyChanged();
        }
    }

    int _pending;
    /// <summary>Plays waiting to be sent, across all services.</summary>
    public int Pending
    {
        get => _pending;
        private set
        {
            if (!Set(ref _pending, value)) return;
            OnPropertyChanged(nameof(PendingText));
        }
    }

    public string PendingText => _pending switch
    {
        0 => "",
        1 => "1 play is waiting to be sent.",
        var n => $"{n:N0} plays are waiting to be sent.",
    };

    bool AnyConnected => _services.Any(s => s.IsConnected);

    // ---- Playback events (UI thread) ----

    /// <summary>A track started playing (including a repeat of the same track).</summary>
    public void TrackStarted(Track track, TimeSpan length)
    {
        _current = null;
        if (track.Library == LibraryKind.Mixes && !ScrobbleMixes) return;

        var duration = track.Duration > TimeSpan.Zero ? track.Duration : length;
        if (!ScrobbleRules.IsLongEnough(duration) || !AnyConnected) return;

        var play = new Scrobble(track.Artist, track.Title, track.Album, track.AlbumArtist, track.TrackNo,
            duration, DateTimeOffset.UtcNow);
        _current = new Listen(play);
        foreach (var service in _services.Where(s => s.IsConnected))
            _ = service.NowPlayingAsync(play);
    }

    /// <summary>Playback stopped or moved to something that isn't scrobbled (e.g. radio).</summary>
    public void TrackStopped() => _current = null;

    /// <summary>
    /// Time actually spent listening (paused time and seeking don't count). Once enough has been
    /// heard, the play is queued and sent.
    /// </summary>
    public void Listened(TimeSpan time)
    {
        if (_current is not { Queued: false } listen) return;
        listen.Listened += time;
        if (!ScrobbleRules.IsDue(listen.Play.Duration, listen.Listened)) return;

        listen.Queued = true;
        foreach (var service in _services.Where(s => s.IsConnected))
            _db.AddScrobble(service.Id, listen.Play);
        Pending = _db.CountScrobbles();
        _ = FlushAsync();
    }

    // ---- Sending ----

    /// <summary>Sends everything queued, oldest first, per service. Safe to call any time.</summary>
    public async Task FlushAsync()
    {
        if (!await _flushLock.WaitAsync(0)) return; // already sending
        try
        {
            foreach (var service in _services.Where(s => s.IsConnected))
            {
                while (_db.GetScrobbles(service.Id, service.MaxBatch) is { Count: > 0 } batch)
                {
                    var outcome = await service.SubmitAsync(batch.Select(b => b.Play).ToList());
                    if (outcome is SubmitOutcome.RetryLater or SubmitOutcome.AuthFailed) break;
                    _db.DeleteScrobbles(batch.Select(b => b.Id)); // sent, or refused for good
                    if (outcome == SubmitOutcome.Rejected)
                        Debug.WriteLine($"{service.Id} rejected {batch.Count} play(s)");
                }
            }
        }
        catch (Exception e)
        {
            // Never let a scrobbling problem disturb playback; the queue is retried later.
            Debug.WriteLine($"Scrobble flush failed: {e.Message}");
        }
        finally
        {
            Pending = _db.CountScrobbles();
            _flushLock.Release();
        }
    }

    public void Dispose() => _retryTimer?.Stop();
}
