using System.IO;

namespace Melora;

/// <summary>
/// Melora is portable: everything it writes lives in a <c>data</c> folder next to the exe.
/// </summary>
public static class AppPaths
{
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    public static string DataDir { get; } = Directory.CreateDirectory(Path.Combine(BaseDir, "data")).FullName;

    public static string Database => Path.Combine(DataDir, "melora.db");

    public static string ToStored(string fullPath) => ToStored(fullPath, BaseDir);

    public static string FromStored(string stored) => FromStored(stored, BaseDir);

    /// <summary>
    /// Paths on Melora's own drive are stored without the drive letter, so a portable copy keeps
    /// working when the letter changes: relative to Melora for files inside its folder (they move
    /// with it), and from the drive's root otherwise (they stay put when Melora's folder moves).
    /// </summary>
    public static string ToStored(string fullPath, string baseDir)
    {
        var root = Path.GetPathRoot(fullPath);
        if (!string.Equals(root, Path.GetPathRoot(baseDir), StringComparison.OrdinalIgnoreCase)) return fullPath;
        var relative = Path.GetRelativePath(baseDir, fullPath);
        return relative == ".." || relative.StartsWith(@"..\", StringComparison.Ordinal)
            ? fullPath[(root!.Length - 1)..] // "C:\Music\a.flac" → "\Music\a.flac"
            : relative;
    }

    /// <summary>The full path for a stored one. Paths stored relative by Melora before 1.3.2 still resolve as before.</summary>
    public static string FromStored(string stored, string baseDir) =>
        stored.StartsWith('\\') && !stored.StartsWith(@"\\", StringComparison.Ordinal)
            ? Path.GetPathRoot(baseDir)!.TrimEnd('\\') + stored
            : Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(baseDir, stored));
}
