using System.IO;
using Melora.Library;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public sealed class AlbumGroupingTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"melora-albums-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    static TrackRow Row(string path, string title, string artist, string? album, string? albumArtist = null, int track = 0, int disc = 0) =>
        new(path, 0, title, artist, albumArtist, album, 2020, track, disc, 180_000);

    List<Track> Load(params TrackRow[] rows)
    {
        var db = new LibraryDb(_path);
        db.SaveScan(LibraryKind.Music, rows, []);
        using var library = new LibraryService(db, LibraryKind.Music, () => []);
        return library.Load();
    }

    [Fact]
    public void Compilation_WithoutAlbumArtist_StaysOneAlbum_ByVariousArtists()
    {
        var tracks = Load(
            Row(@"D:\m\Hits\01.flac", "One", "Artist A", "Summer Hits", track: 1),
            Row(@"D:\m\Hits\02.flac", "Two", "Artist B", "Summer Hits", track: 2));

        Assert.Same(tracks[0].Group, tracks[1].Group);
        Assert.Equal(LibraryService.VariousArtists, tracks[0].Group.Artist);
        Assert.All(tracks, t => Assert.Equal("", t.Disc)); // one disc: no heading
    }

    [Fact]
    public void SameAlbumName_InDifferentFolders_WithoutAlbumArtist_AreDifferentAlbums()
    {
        var tracks = Load(
            Row(@"D:\m\A\01.flac", "One", "Artist A", "Greatest Hits"),
            Row(@"D:\m\B\01.flac", "Two", "Artist B", "Greatest Hits"));

        Assert.NotSame(tracks[0].Group, tracks[1].Group);
    }

    [Fact]
    public void DiscFolders_AreOneAlbum_WithDiscHeadings_InDiscOrder()
    {
        var tracks = Load(
            Row(@"D:\m\Live\CD2\01.flac", "Encore", "Band", "Live", track: 1),
            Row(@"D:\m\Live\CD1\02.flac", "Second", "Band", "Live", track: 2),
            Row(@"D:\m\Live\CD1\01.flac", "First", "Band", "Live", track: 1));

        Assert.Single(tracks.Select(t => t.Group).Distinct());
        Assert.Equal(["First", "Second", "Encore"], tracks.Select(t => t.Title));
        Assert.Equal(["Disc 1", "Disc 1", "Disc 2"], tracks.Select(t => t.Disc));
    }

    [Fact]
    public void DiscTags_GiveHeadings_EvenInOneFolder()
    {
        var tracks = Load(
            Row(@"D:\m\Box\01.flac", "A", "Band", "Box", "Band", track: 1, disc: 1),
            Row(@"D:\m\Box\02.flac", "B", "Band", "Box", "Band", track: 1, disc: 2));

        Assert.Equal(["Disc 1", "Disc 2"], tracks.Select(t => t.Disc));
    }

    [Theory]
    [InlineData("CD1", 1)]
    [InlineData("cd 2", 2)]
    [InlineData("Disc 3", 3)]
    [InlineData("Disk_04", 4)]
    [InlineData("CD1 Bonus", 0)]
    [InlineData("Discography", 0)]
    public void DiscFolderNumber(string name, int expected) => Assert.Equal(expected, LibraryService.DiscFolderNumber(name));
}
