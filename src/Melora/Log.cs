using System.IO;

namespace Melora;

/// <summary>
/// A small text log in the data folder (<c>data\melora.log</c>) for things that go wrong without
/// anyone seeing: a refused scrobble, a file that can't be played, Discord trouble. When it passes
/// 1 MB it's moved to <c>melora.log.old</c> (replacing the previous one) and a new one started.
/// </summary>
public static class Log
{
    const long MaxSize = 1024 * 1024;
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
                if (file.Exists && file.Length > MaxSize)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // A log that can't be written mustn't take anything else down with it.
        }
    }
}
