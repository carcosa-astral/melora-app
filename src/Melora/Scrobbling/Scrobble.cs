namespace Melora.Scrobbling;

/// <summary>One play to report: what it was and when it started.</summary>
public sealed record Scrobble(
    string Artist,
    string Title,
    string? Album,
    string? AlbumArtist,
    int TrackNo,
    TimeSpan Duration,
    DateTimeOffset StartedAt);

/// <summary>Loving (or un-loving) a track. The MusicBrainz recording ID is from the file's tags, if it has one.</summary>
public sealed record Love(string Artist, string Title, string? Album, string? RecordingMbid, bool Loved);

public enum SubmitOutcome
{
    /// <summary>Accepted (or deliberately ignored by the service); drop from the queue.</summary>
    Sent,
    /// <summary>Network trouble, rate limit or server error; keep and try again later.</summary>
    RetryLater,
    /// <summary>The service refused these plays for good; drop them.</summary>
    Rejected,
    /// <summary>The login is no longer valid; keep the plays until the user reconnects.</summary>
    AuthFailed,
}

public interface IScrobbleService
{
    /// <summary>Key used for this service's queue in the database.</summary>
    string Id { get; }

    bool IsConnected { get; }

    /// <summary>Most plays the service accepts in one request.</summary>
    int MaxBatch { get; }

    Task NowPlayingAsync(Scrobble play);

    Task<SubmitOutcome> SubmitAsync(IReadOnlyList<Scrobble> plays);

    /// <summary>Marks a track as loved (or not) on the service.</summary>
    Task<SubmitOutcome> LoveAsync(Love love);
}

/// <summary>The usual scrobbling rules shared by Last.fm and ListenBrainz.</summary>
public static class ScrobbleRules
{
    public static readonly TimeSpan MinimumLength = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumWait = TimeSpan.FromMinutes(4);

    /// <summary>Tracks of 30 seconds or less are never scrobbled.</summary>
    public static bool IsLongEnough(TimeSpan duration) => duration > MinimumLength;

    /// <summary>How long you have to actually listen: half the track, or 4 minutes, whichever comes first.</summary>
    public static TimeSpan ListenNeeded(TimeSpan duration) =>
        duration / 2 < MaximumWait ? duration / 2 : MaximumWait;

    public static bool IsDue(TimeSpan duration, TimeSpan listened) =>
        IsLongEnough(duration) && listened >= ListenNeeded(duration);
}
