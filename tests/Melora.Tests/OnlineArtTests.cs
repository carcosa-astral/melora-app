using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Melora.Discord;
using Melora.Library;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public sealed class OnlineArtTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"melora-art-{Guid.NewGuid():N}")).FullName;
    string Db => Path.Combine(_dir, "melora.db");
    string Covers => Path.Combine(_dir, "covers");
    string Album => Directory.CreateDirectory(Path.Combine(_dir, "music", "Daft Punk", "Discovery")).FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    static readonly byte[] Png = MakePng();

    static byte[] MakePng()
    {
        var pixels = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(pixels));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>iTunes finds the album; the picture download answers with <paramref name="picture"/>.</summary>
    sealed class Store(byte[] picture) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            HttpContent content = request.RequestUri!.Host switch
            {
                "itunes.apple.com" => new StringContent("""{"results":[{"artistName":"Daft Punk","collectionName":"Discovery","artworkUrl100":"https://img.test/a/100x100bb.jpg"}]}"""),
                "img.test" => new ByteArrayContent(picture),
                _ => new StringContent("{}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    (AlbumGroup Group, OnlineArt Online, Store Store) Setup(byte[]? picture = null)
    {
        var track = Path.Combine(Album, "01.flac");
        File.WriteAllText(track, "not really audio");
        var store = new Store(picture ?? Png);
        var online = new OnlineArt(new CoverLookup(new HttpClient(store), new LibraryDb(Db)), Covers);
        var group = new AlbumGroup
        {
            Title = "Discovery",
            Artist = "Daft Punk",
            SampleTrackPath = track,
            OnlineQuery = new CoverQuery("Daft Punk", "Discovery", null),
            Online = online,
        };
        return (group, online, store);
    }

    [Fact]
    public async Task AlbumWithoutArt_GetsTheCoverFoundOnline_SavedInData_NotInTheMusicFolder()
    {
        var (group, _, store) = Setup();

        Assert.Equal(Png, await group.GetArtBytesAsync());
        Assert.Single(Directory.GetFiles(Covers));
        Assert.Equal(["01.flac"], Directory.GetFiles(Album).Select(Path.GetFileName));

        // Next time (and after a restart) it's read from disk: nothing more is asked online.
        var requests = store.Requests;
        Assert.Equal(Png, group.ReadArtBytes());
        Assert.Equal(Png, await group.GetArtBytesAsync());
        Assert.Equal(requests, store.Requests);
    }

    [Fact]
    public async Task FolderCover_WinsOverOnline_AndNothingIsLookedUp()
    {
        var (group, _, store) = Setup();
        File.WriteAllBytes(Path.Combine(Album, "cover.jpg"), [1, 2, 3]);

        Assert.Equal([1, 2, 3], await group.GetArtBytesAsync());
        Assert.Equal(0, store.Requests);
    }

    [Fact]
    public async Task TurnedOff_LooksNothingUp_AndHidesWhatWasFound()
    {
        var (group, online, store) = Setup();
        await group.GetArtBytesAsync();
        online.Enabled = false;

        Assert.Null(group.ReadArtBytes());
        var requests = store.Requests;
        Assert.Null(await new AlbumGroup { Title = "X", Artist = "Y", SampleTrackPath = group.SampleTrackPath, OnlineQuery = new CoverQuery("Y", "X", null), Online = online }.GetArtBytesAsync());
        Assert.Equal(requests, store.Requests);
    }

    [Fact]
    public async Task ADownloadThatIsntAPicture_IsNotSaved()
    {
        var (group, _, _) = Setup(picture: "<html>oops</html>"u8.ToArray());

        Assert.Null(await group.GetArtBytesAsync());
        Assert.False(Directory.Exists(Covers) && Directory.GetFiles(Covers).Length > 0);
    }

    static TrackRow Row(string path, string artist, string? album, string? albumArtist = null) =>
        new(path, 0, "Title", artist, albumArtist, album, 2020, 1, 0, 180_000);

    [Fact]
    public void Library_LooksUpTaggedAlbums_AndDjs_ButNotUntaggedFolders()
    {
        var db = new LibraryDb(Db);
        db.SaveScan(LibraryKind.Music, [
            Row(@"D:\m\Discovery\01.flac", "Daft Punk", "Discovery"),
            Row(@"D:\m\Hits\01.flac", "A", "Summer Hits"),
            Row(@"D:\m\Hits\02.flac", "B", "Summer Hits"),
            Row(@"D:\m\Loose\01.flac", "Someone", null),
        ], []);
        using var music = new LibraryService(db, LibraryKind.Music, () => []);
        var albums = music.Load().Select(t => t.Group).Distinct().ToDictionary(g => g.Title, g => g.OnlineQuery);

        Assert.Equal(new CoverQuery("Daft Punk", "Discovery", null, @"D:\m\Discovery\01.flac"), albums["Discovery"]);
        Assert.Equal("Various Artists", albums["Summer Hits"]!.Artist);
        Assert.Null(albums["Loose"]);
    }
}

public class ListJumpTests
{
    static readonly string[] Artists =
        ["Aphex Twin", "Barry Can't Swim", "Barry Can't Swim", "Bicep", "Björk", "The Chemical Brothers", "Caribou"];

    static string? Jump(string typed) => ListJump.Find(Artists, a => a, typed);

    [Theory]
    [InlineData("b", "Barry Can't Swim")]
    [InlineData("bi", "Bicep")]
    [InlineData("BJO", "Björk")]                   // case and accents
    [InlineData("barry c", "Barry Can't Swim")]    // spaces
    [InlineData("che", "The Chemical Brothers")]   // without "The"
    [InlineData("the c", "The Chemical Brothers")]
    [InlineData("ca", "Caribou")]
    [InlineData("z", null)]
    [InlineData(" ", null)]
    public void JumpsToTheFirstNameStartingWithWhatWasTyped(string typed, string? expected) => Assert.Equal(expected, Jump(typed));

    [Fact]
    public void TheSameLetterAgain_StepsThroughTheNamesStartingWithIt()
    {
        Assert.Equal("Barry Can't Swim", Jump("b"));
        Assert.Equal("Bicep", Jump("bb"));
        Assert.Equal("Björk", Jump("bbb"));
        Assert.Equal("Barry Can't Swim", Jump("bbbb")); // round again
    }

    [Fact]
    public void JumpsToTheFirstRowOfAName()
    {
        var rows = new[] { new Row("Aphex Twin"), new Row("Bicep"), new Row("Bicep") };
        Assert.Same(rows[1], ListJump.Find(rows, r => r.Name, "bi"));
    }

    sealed record Row(string Name);
}
