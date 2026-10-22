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
