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

    /// <summary>
    /// Paths on the same drive as Melora are stored relative to it, so a portable copy
    /// keeps working when the drive letter changes.
    /// </summary>
    public static string ToStored(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(root, Path.GetPathRoot(BaseDir), StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(BaseDir, fullPath)
            : fullPath;
    }

    public static string FromStored(string stored) =>
        Path.IsPathRooted(stored) ? stored : Path.GetFullPath(Path.Combine(BaseDir, stored));
}
