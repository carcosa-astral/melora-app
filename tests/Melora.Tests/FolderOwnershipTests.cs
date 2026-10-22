using Melora.Library;

namespace Melora.Tests;

public class FolderOwnershipTests
{
    const string Music = @"D:\Music";
    const string Mixes = @"D:\Music\Mixes";

    [Fact]
    public void MixesFolderInsideMusic_BelongsToMixes()
    {
        var file = @"D:\Music\Mixes\2024\set.mp3";
        Assert.False(LibraryService.Owns(file, Music, [Mixes]));
        Assert.True(LibraryService.Owns(file, Mixes, [Music]));
    }

    [Fact]
    public void OrdinaryMusicFile_StaysInMusic() =>
        Assert.True(LibraryService.Owns(@"D:\Music\Album\01.flac", Music, [Mixes]));

    [Theory]
    [InlineData(@"D:\Music\Mixes\DJ Kestrel\set.mp3", "DJ Kestrel")]
    [InlineData(@"D:\Music\Mixes\DJ Kestrel\2024\Summer\set.mp3", "DJ Kestrel")]
    [InlineData(@"D:\Music\Mixes\loose-set.mp3", "Mixes")]
    public void Mixes_AreGroupedByTheirTopLevelFolder(string file, string dj) =>
        Assert.Equal(dj, LibraryService.CollectionFor(file, [Mixes]));

    [Fact]
    public void Mixes_UseTheDeepestMatchingMixesFolder() =>
        Assert.Equal("Mira Vale", LibraryService.CollectionFor(@"D:\Sets\Live\Mira Vale\a.opus", [@"D:\Sets", @"D:\Sets\Live"]));

    [Fact]
    public void SimilarlyNamedFolder_IsNotNested() =>
        // "D:\Music\Mixes Old" is not inside "D:\Music\Mixes".
        Assert.True(LibraryService.Owns(@"D:\Music\Mixes Old\a.mp3", Music, [Mixes]));
}

public class MixDateTests
{
    [Theory]
    [InlineData("Club Set (2020-10-14) [LP Giobbi]", 0, "2020-10-14")]
    [InlineData("Live at EDC Mexico 2023 (Kinetic Field) (2023-03-02)", 0, "2023-03-02")] // the bare year isn't the date
    [InlineData("Boiler Room 2019.07.04", 0, "2019-07-04")]
    [InlineData("Set (2021-02-30)", 0, null)]                                             // not a real date
    [InlineData("Warehouse Set", 2018, "2018-01-01")]                                     // falls back to the year tag
    [InlineData("Warehouse Set", 0, null)]
    public void MixDate(string title, int year, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), Melora.Library.LibraryService.MixDate(title, year));
}
