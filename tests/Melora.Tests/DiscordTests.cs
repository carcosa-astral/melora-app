using System.IO;
using System.Net.Http;
using Melora.Discord;
using Melora.Library;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public class CoverMatchingTests
{
    [Theory]
    [InlineData("Sigur Rós", "Sigur Ros", true)]                                // accents
    [InlineData("OK Computer", "OK Computer OKNOTOK 1997 2017", true)]          // one starts with the other
    [InlineData("OK Computer (Remastered)", "OK Computer", true)]               // brackets dropped
    [InlineData("The Beatles", "Beatles", true)]                                // leading "the"
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel", true)]
    [InlineData("AC/DC", "ACDC", true)]                                         // punctuation
    [InlineData("Air", "Airbag", false)]                                        // too short to be a prefix match
    [InlineData("Geogaddi", "Music Has the Right to Children", false)]
    [InlineData("", "Anything", false)]
    public void SameName(string a, string b, bool expected) => Assert.Equal(expected, CoverLookup.SameName(a, b));

    [Fact]
    public void Key_IsTheSameForNamesThatMatchExactly()
    {
        Assert.Equal(CoverLookup.Key(new CoverQuery("Sigur Rós", "Ágætis byrjun", null)),
            CoverLookup.Key(new CoverQuery("SIGUR ROS", "Agætis Byrjun", null, @"C:\m\01.flac")));
        Assert.NotEqual(CoverLookup.Key(new CoverQuery("Daft Punk", "Discovery", null)),
            CoverLookup.Key(new CoverQuery("Daft Punk", null, "Discovery")));
    }
}

public sealed class CoverCacheTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"melora-covers-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    /// <summary>Answers every request from a fixed function and counts them.</summary>
    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            return Task.FromResult(answer(request));
        }
    }

    [Fact]
    public async Task FoundCover_IsCached_AndITunesArtIsAskedForAtFullSize()
    {
        var handler = new FakeHandler(request => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.Host == "itunes.apple.com"
                ? """{"results":[{"artistName":"Other","collectionName":"Discovery","artworkUrl100":"https://x/wrong/100x100bb.jpg"},{"artistName":"Daft Punk","collectionName":"Discovery","artworkUrl100":"https://x/right/100x100bb.jpg"}]}"""
                : "{}"),
        });
        var lookup = new CoverLookup(new HttpClient(handler), new LibraryDb(_path));
        var query = new CoverQuery("Daft Punk", "Discovery", null);

        Assert.Equal("https://x/right/600x600bb.jpg", await lookup.FindAsync(query));
        var requests = handler.Requests;
        Assert.Equal("https://x/right/600x600bb.jpg", await lookup.FindAsync(query));
        Assert.Equal(requests, handler.Requests);
    }

    [Fact]
    public async Task Offline_IsNotRememberedAsNoCover()
    {
        var online = false;
        var handler = new FakeHandler(_ => online
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"results":[],"data":[]}""") }
            : throw new HttpRequestException("offline"));
        var db = new LibraryDb(_path);
        var lookup = new CoverLookup(new HttpClient(handler), db);
        var query = new CoverQuery("Daft Punk", "Discovery", null);

        Assert.Null(await lookup.FindAsync(query));
        Assert.False(db.GetCover(CoverLookup.Key(query)).Found);

        online = true;
        Assert.Null(await lookup.FindAsync(query));
        Assert.True(db.GetCover(CoverLookup.Key(query)).Found); // a real miss is remembered for a while
    }
}

public class PresenceTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Activity_IsListening_WithProgressAndCover()
    {
        var info = new PresenceInfo(PresenceSource.Music, "Digital Love", "Daft Punk", "Discovery", null, Start, Start.AddMinutes(5));
        var activity = DiscordPresence.BuildActivity(info, "https://x/cover.jpg");

        Assert.Equal(2, activity["type"]!.GetValue<int>());
        Assert.Equal("Digital Love", activity["details"]!.GetValue<string>());
        Assert.Equal("Daft Punk", activity["state"]!.GetValue<string>());
        Assert.Equal(1_700_000_000_000, activity["timestamps"]!["start"]!.GetValue<long>());
        Assert.Equal(1_700_000_300_000, activity["timestamps"]!["end"]!.GetValue<long>());
        Assert.Equal("https://x/cover.jpg", activity["assets"]!["large_image"]!.GetValue<string>());
        Assert.Equal("Discovery", activity["assets"]!["large_text"]!.GetValue<string>());
    }

    [Fact]
    public void Activity_WithoutCoverOrEnd_LeavesThemOut()
    {
        var info = new PresenceInfo(PresenceSource.Radio, "Song", "Station", "Station", null, Start, null);
        var activity = DiscordPresence.BuildActivity(info, null);

        Assert.Null(activity["assets"]);
        Assert.Null(activity["timestamps"]!["end"]);
    }

    [Fact]
    public void Text_IsFittedToDiscordsLimits()
    {
        Assert.Equal(2, DiscordPresence.Fit("X").Length);
        Assert.Equal(128, DiscordPresence.Fit(new string('a', 300)).Length);
        Assert.Equal("Fine", DiscordPresence.Fit("Fine"));
    }
}
