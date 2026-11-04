using System.IO;
using Melora.Library;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public sealed class LibraryDbTests : IDisposable
{
    readonly string _path = Path.Combine(Path.GetTempPath(), $"melora-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public void RemoveFolder_RemovesAFolderAddedNormally()
    {
        var db = new LibraryDb(_path);
        var folder = Path.Combine(AppPaths.BaseDir, "Music");
        db.AddFolder(LibraryKind.Music, folder);

        db.RemoveFolder(LibraryKind.Music, folder);

        Assert.Empty(db.GetFolders(LibraryKind.Music));
    }

    [Fact]
    public void RemoveFolder_RemovesAFolderStoredAsAnAbsolutePath()
    {
        // Same drive as Melora, but stored absolute rather than relative.
        var db = new LibraryDb(_path);
        var folder = Path.Combine(AppPaths.BaseDir, "Elsewhere", "Music");
        using (var raw = new SqliteConnection($"Data Source={_path}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "INSERT INTO folders (library, path) VALUES ('music', $path)";
            cmd.Parameters.AddWithValue("$path", folder);
            cmd.ExecuteNonQuery();
        }

        db.RemoveFolder(LibraryKind.Music, folder + "\\");

        Assert.Empty(db.GetFolders(LibraryKind.Music));
    }

    [Fact]
    public void RemoveFolder_LeavesOtherLibrariesAlone()
    {
        var db = new LibraryDb(_path);
        var folder = Path.Combine(AppPaths.BaseDir, "Shared");
        db.AddFolder(LibraryKind.Mixes, folder);

        db.RemoveFolder(LibraryKind.Music, folder);

        Assert.Single(db.GetFolders(LibraryKind.Mixes));
    }
}

public class AppPathsTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Music\a.flac", @"\Users\me\Music\a.flac")]   // same drive, outside Melora
    [InlineData(@"C:\_Apps\Melora\Music\a.flac", @"Music\a.flac")]        // inside Melora's folder
    [InlineData(@"D:\media\music\a.flac", @"D:\media\music\a.flac")]       // another drive
    public void Stored_AndBackAgain(string full, string stored)
    {
        Assert.Equal(stored, AppPaths.ToStored(full, @"C:\_Apps\Melora\"));
        Assert.Equal(full, AppPaths.FromStored(stored, @"C:\_Apps\Melora\"));
    }

    [Fact]
    public void MovingMelora_KeepsMusicElsewhereOnItsDrive_AndUnderIt()
    {
        var outside = AppPaths.ToStored(@"C:\Users\me\Music\a.flac", @"C:\_Apps\Melora\");
        var inside = AppPaths.ToStored(@"C:\_Apps\Melora\Music\a.flac", @"C:\_Apps\Melora\");

        // Moved deeper, and onto a USB stick that came up as E:.
        Assert.Equal(@"C:\Users\me\Music\a.flac", AppPaths.FromStored(outside, @"C:\Apps\Portable\Melora\"));
        Assert.Equal(@"E:\Users\me\Music\a.flac", AppPaths.FromStored(outside, @"E:\Melora\"));
        Assert.Equal(@"C:\Apps\Portable\Melora\Music\a.flac", AppPaths.FromStored(inside, @"C:\Apps\Portable\Melora\"));
    }

    [Fact]
    public void OlderRelativePaths_StillResolve() =>
        Assert.Equal(@"C:\Users\me\Music\a.flac", AppPaths.FromStored(@"..\..\Users\me\Music\a.flac", @"C:\_Apps\Melora\"));
}
