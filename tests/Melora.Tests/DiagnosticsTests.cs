using System.IO;
using Melora.Library;
using Microsoft.Data.Sqlite;

namespace Melora.Tests;

public sealed class DiagnosticsTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"melora-diag-{Guid.NewGuid():N}")).FullName;

    public DiagnosticsTests() => Log.FilePath = Path.Combine(_dir, "melora.log");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Log_AddsStampedLines_AndRollsOverPastOneMegabyte()
    {
        Log.Write("first");
        // Other tests running alongside may log too, so look for the line rather than the start of the file.
        Assert.Matches(@"(?m)^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  first\r?$", File.ReadAllText(Log.FilePath));

        File.AppendAllText(Log.FilePath, new string('x', 1024 * 1024));
        Log.Write("after rollover");

        var rolled = Assert.Single(Directory.GetFiles(_dir, "melora-*.log"));
        Assert.Matches(@"melora-\d{4}-\d{2}-\d{2}_\d{6}\.log$", rolled);
        Assert.Contains("first", File.ReadAllText(rolled));
        Assert.DoesNotContain("first", File.ReadAllText(Log.FilePath));
        Assert.Contains("after rollover", File.ReadAllText(Log.FilePath));
    }

    [Fact]
    public void Log_KeepsOnlyTheNewestFiveOldLogs()
    {
        for (var day = 1; day <= 7; day++)
            File.WriteAllText(Path.Combine(_dir, $"melora-2020-01-0{day}_120000.log"), "old");
        File.WriteAllText(Log.FilePath, new string('x', 1024 * 1024 + 1));

        Log.Write("new");

        var kept = Directory.GetFiles(_dir, "melora-*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(5, kept.Count);
        foreach (var oldest in new[] { "01", "02", "03" })
            Assert.DoesNotContain($"melora-2020-01-{oldest}_120000.log", kept);
    }

    [Fact]
    public async Task MissingFolder_KeepsItsTracks_AndIsLogged()
    {
        var music = Directory.CreateDirectory(Path.Combine(_dir, "Music")).FullName;
        File.WriteAllBytes(Path.Combine(music, "song.mp3"), [1, 2, 3]); // unreadable tags still count as a track
        var db = new LibraryDb(Path.Combine(_dir, "melora.db"));
        using var library = new LibraryService(db, LibraryKind.Music, () => []);
        library.AddFolder(music);

        await library.ScanAsync();
        Assert.Single(library.Load());

        Directory.Delete(music, recursive: true); // the drive was unplugged
        await library.ScanAsync();

        Assert.Single(library.Load());
        Assert.Contains("folder not found, keeping its tracks", File.ReadAllText(Log.FilePath));
    }
}
