using System.IO;
using System.Net;
using Melora.Library;
using Melora.Scrobbling;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public class ScrobbleRulesTests
{
    [Theory]
    [InlineData(30, 0, false)]     // 30 s or shorter never counts
    [InlineData(200, 99, false)]   // under half
    [InlineData(200, 100, true)]   // half of a 3:20 track
    [InlineData(3600, 239, false)] // a one-hour mix: under 4 minutes
    [InlineData(3600, 240, true)]  // 4 minutes is enough for anything long
    public void IsDue(int durationSeconds, int listenedSeconds, bool expected) =>
        Assert.Equal(expected, ScrobbleRules.IsDue(TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(listenedSeconds)));
}

public class LastFmTests
{
    [Fact]
    public void SignatureBase_SortsParameters_SkipsFormat_AppendsSecret()
    {
        var parameters = new Dictionary<string, string>
        {
            ["track"] = "Roygbiv",
            ["method"] = "track.updateNowPlaying",
            ["format"] = "json",
            ["artist"] = "Boards of Canada",
            ["api_key"] = "KEY",
        };

        Assert.Equal("api_keyKEYartistBoards of Canadamethodtrack.updateNowPlayingtrackRoygbivSECRET",
            LastFmClient.SignatureBase(parameters, "SECRET"));
    }

    [Fact]
    public void Sign_IsLowercaseMd5()
    {
        // md5("methodauth.getTokenapi_keyabc") computed independently: parameters are sorted first.
        var sig = LastFmClient.Sign(new Dictionary<string, string> { ["method"] = "auth.getToken", ["api_key"] = "abc" }, "s");
        Assert.Equal(32, sig.Length);
        Assert.Equal(sig.ToLowerInvariant(), sig);
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData("api_keyabcmethodauth.getTokens"u8)),
            sig);
    }

    [Fact]
    public void AddPlay_UsesIndexedNamesInBatches_AndOmitsEmptyFields()
    {
        var play = new Scrobble("Bonobo", "Kerala", null, "Bonobo", 0, TimeSpan.FromSeconds(245),
            DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var parameters = new Dictionary<string, string>();

        LastFmClient.AddPlay(parameters, play, 3);

        Assert.Equal("Bonobo", parameters["artist[3]"]);
        Assert.Equal("Kerala", parameters["track[3]"]);
        Assert.Equal("1700000000", parameters["timestamp[3]"]);
        Assert.Equal("245", parameters["duration[3]"]);
        Assert.False(parameters.ContainsKey("album[3]"));
        Assert.False(parameters.ContainsKey("albumArtist[3]")); // same as the artist
        Assert.False(parameters.ContainsKey("trackNumber[3]"));
    }

    [Theory]
    [InlineData(11, SubmitOutcome.RetryLater)] // service offline
    [InlineData(29, SubmitOutcome.RetryLater)] // rate limited
    [InlineData(9, SubmitOutcome.AuthFailed)]  // invalid session
    [InlineData(10, SubmitOutcome.AuthFailed)] // invalid API key
    [InlineData(6, SubmitOutcome.Rejected)]    // invalid parameters
    public void Classify(int code, SubmitOutcome expected) => Assert.Equal(expected, LastFmClient.Classify(code));
}

public class ListenBrainzTests
{
    static readonly Scrobble Play = new("Bonobo", "Kerala", "Migration", null, 4, TimeSpan.FromSeconds(245),
        DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

    [Fact]
    public void Payload_Single_HasTimestampAndMetadata()
    {
        var json = ListenBrainzClient.BuildPayload("single", [Play]);
        var listen = json["payload"]![0]!;

        Assert.Equal("single", json["listen_type"]!.GetValue<string>());
        Assert.Equal(1_700_000_000, listen["listened_at"]!.GetValue<long>());
        Assert.Equal("Bonobo", listen["track_metadata"]!["artist_name"]!.GetValue<string>());
        Assert.Equal("Kerala", listen["track_metadata"]!["track_name"]!.GetValue<string>());
        Assert.Equal("Migration", listen["track_metadata"]!["release_name"]!.GetValue<string>());
        Assert.Equal(245_000, listen["track_metadata"]!["additional_info"]!["duration_ms"]!.GetValue<long>());
        Assert.Equal("Melora", listen["track_metadata"]!["additional_info"]!["media_player"]!.GetValue<string>());
    }

    [Fact]
    public void Payload_PlayingNow_HasNoTimestamp() =>
        Assert.Null(ListenBrainzClient.BuildPayload("playing_now", [Play])["payload"]![0]!["listened_at"]);

    [Theory]
    [InlineData(HttpStatusCode.OK, SubmitOutcome.Sent)]
    [InlineData(HttpStatusCode.Unauthorized, SubmitOutcome.AuthFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, SubmitOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadGateway, SubmitOutcome.RetryLater)]
    [InlineData(HttpStatusCode.BadRequest, SubmitOutcome.Rejected)]
    public void Classify(HttpStatusCode status, SubmitOutcome expected) =>
        Assert.Equal(expected, ListenBrainzClient.Classify(status));
}

public sealed class ScrobblerTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"melora-scrobble-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    sealed class FakeService(string id) : IScrobbleService
    {
        public string Id => id;
        public bool IsConnected { get; set; } = true;
        public int MaxBatch => 2;
        public Queue<SubmitOutcome> Outcomes { get; } = new();
        public List<Scrobble> NowPlaying { get; } = [];
        public List<List<Scrobble>> Batches { get; } = [];

        public Task NowPlayingAsync(Scrobble play)
        {
            NowPlaying.Add(play);
            return Task.CompletedTask;
        }

        public Task<SubmitOutcome> SubmitAsync(IReadOnlyList<Scrobble> plays)
        {
            Batches.Add([.. plays]);
            return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : SubmitOutcome.Sent);
        }
    }

    static Track MakeTrack(string title, int seconds, LibraryKind library = LibraryKind.Music) => new()
    {
        Path = $@"C:\m\{title}.flac",
        Title = title,
        Artist = "Artist",
        Group = new AlbumGroup { Title = "Album", Artist = "Artist", SampleTrackPath = $@"C:\m\{title}.flac" },
        Album = "Album",
        Library = library,
        Duration = TimeSpan.FromSeconds(seconds),
    };

    [Fact]
    public async Task PlayIsSentOnceHalfHasBeenHeard()
    {
        var db = new LibraryDb(_path);
        var service = new FakeService("a");
        var scrobbler = new Scrobbler(db, [service]);

        scrobbler.TrackStarted(MakeTrack("Song", 200), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromSeconds(99));
        Assert.Empty(service.Batches);

        scrobbler.Listened(TimeSpan.FromSeconds(1));
        await scrobbler.FlushAsync();
        scrobbler.Listened(TimeSpan.FromSeconds(100)); // already counted; no second scrobble

        Assert.Single(service.NowPlaying);
        Assert.Single(service.Batches);
        Assert.Equal("Song", service.Batches[0][0].Title);
        Assert.Equal(0, db.CountScrobbles());
    }

    [Fact]
    public async Task OfflinePlaysStayQueued_ThenSendInBatches()
    {
        var db = new LibraryDb(_path);
        var service = new FakeService("a");
        var scrobbler = new Scrobbler(db, [service]);

        // Three plays while the service is unreachable (each one tries to send straight away).
        foreach (var title in new[] { "One", "Two", "Three" })
        {
            service.Outcomes.Enqueue(SubmitOutcome.RetryLater);
            scrobbler.TrackStarted(MakeTrack(title, 120), TimeSpan.Zero);
            scrobbler.Listened(TimeSpan.FromSeconds(60));
        }
        Assert.Equal(3, db.CountScrobbles());
        Assert.Equal(3, scrobbler.Pending);

        // Back online: sent oldest first, two at a time.
        service.Batches.Clear();
        await scrobbler.FlushAsync();

        Assert.Equal(["One", "Two"], service.Batches[0].Select(p => p.Title));
        Assert.Equal(["Three"], service.Batches[1].Select(p => p.Title));
        Assert.Equal(0, scrobbler.Pending);
    }

    [Fact]
    public async Task OneServiceFailing_DoesNotHoldUpTheOther()
    {
        var db = new LibraryDb(_path);
        var down = new FakeService("down");
        var up = new FakeService("up");
        down.Outcomes.Enqueue(SubmitOutcome.AuthFailed);
        var scrobbler = new Scrobbler(db, [down, up]);

        scrobbler.TrackStarted(MakeTrack("Song", 120), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromSeconds(60)); // sends straight away
        await Task.Yield();

        Assert.Equal(1, db.CountScrobbles("down"));
        Assert.Equal(0, db.CountScrobbles("up"));
    }

    [Fact]
    public async Task RejectedPlaysAreDropped()
    {
        var db = new LibraryDb(_path);
        var service = new FakeService("a");
        service.Outcomes.Enqueue(SubmitOutcome.Rejected);
        var scrobbler = new Scrobbler(db, [service]);

        scrobbler.TrackStarted(MakeTrack("Song", 120), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromSeconds(60));
        await scrobbler.FlushAsync();

        Assert.Equal(0, db.CountScrobbles());
    }

    [Fact]
    public void ShortTracks_Mixes_WhenTurnedOff_AndNoServices_AreNotScrobbled()
    {
        var db = new LibraryDb(_path);
        var service = new FakeService("a");
        var scrobbler = new Scrobbler(db, [service]) { ScrobbleMixes = false };

        scrobbler.TrackStarted(MakeTrack("Jingle", 25), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromSeconds(25));
        scrobbler.TrackStarted(MakeTrack("Set", 3600, LibraryKind.Mixes), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromMinutes(10));

        service.IsConnected = false;
        scrobbler.TrackStarted(MakeTrack("Song", 200), TimeSpan.Zero);
        scrobbler.Listened(TimeSpan.FromSeconds(200));

        Assert.Equal(0, db.CountScrobbles());
        Assert.Empty(service.NowPlaying);
    }
}
