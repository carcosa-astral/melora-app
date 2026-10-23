using System.IO;

namespace Melora;

/// <summary>
/// A small text log in the data folder (<c>data\melora.log</c>) for things that go wrong without
/// anyone seeing: a refused scrobble, a file that can't be played, Discord trouble. When it passes
/// 1 MB it's renamed after the time it was closed (<c>melora-2026-10-22_195601.log</c>) and a new one
/// started; only the newest few of those are kept.
/// </summary>
public static class Log
{
    const long MaxSize = 1024 * 1024;
    const int KeepOld = 5;
    static readonly object Lock = new();

    /// <summary>Where the log is written; tests point it elsewhere.</summary>
    public static string FilePath { get; set; } = Path.Combine(AppPaths.DataDir, "melora.log");

    /// <summary>Adds a line, stamped with the local time. Never throws.</summary>
    public static void Write(string message)
    {
        try
        {
            lock (Lock)
            {
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize) Roll();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // A log that can't be written mustn't take anything else down with it.
        }
    }

    /// <summary>Renames the full log after the current time and deletes all but the newest few old ones.</summary>
    static void Roll()
    {
        var dir = Path.GetDirectoryName(FilePath)!;
        var name = Path.GetFileNameWithoutExtension(FilePath);
        File.Move(FilePath, Path.Combine(dir, $"{name}-{DateTime.Now:yyyy-MM-dd_HHmmss}.log"), overwrite: true);

        // The timestamped names sort oldest first.
        var old = Directory.GetFiles(dir, $"{name}-*.log").Order(StringComparer.Ordinal).ToList();
        foreach (var stale in old.Take(Math.Max(0, old.Count - KeepOld))) File.Delete(stale);
    }
}
