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

    [Fact]
    public void SimilarlyNamedFolder_IsNotNested() =>
        // "D:\Music\Mixes Old" is not inside "D:\Music\Mixes".
        Assert.True(LibraryService.Owns(@"D:\Music\Mixes Old\a.mp3", Music, [Mixes]));
}
