using System.IO;

namespace Melora.Library;

/// <summary>
/// Scans watched folders into the database, keeps them watched, and builds the
/// in-memory track list (grouped by album) for the UI.
/// </summary>
public sealed class LibraryService : IDisposable
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".opus", ".mp3" };
    static readonly TimeSpan WatchDebounce = TimeSpan.FromSeconds(2);

    readonly LibraryDb _db;
    readonly LibraryKind _kind;
    readonly Func<IReadOnlyList<string>> _otherFolders;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly SemaphoreSlim _scanLock = new(1, 1);
    Timer? _debounce;

    /// <summary>Raised (on a background thread) when a scan changed the library.</summary>
    public event Action? Changed;

    /// <param name="otherFolders">The other library's folders, so nested folders aren't scanned twice.</param>
    public LibraryService(LibraryDb db, LibraryKind kind, Func<IReadOnlyList<string>> otherFolders)
    {
        _db = db;
        _kind = kind;
        _otherFolders = otherFolders;
    }

    public LibraryKind Kind => _kind;

    public List<string> Folders => _db.GetFolders(_kind);

    public void AddFolder(string path) { _db.AddFolder(_kind, path); Rewatch(); }

    public void RemoveFolder(string path) { _db.RemoveFolder(_kind, path); Rewatch(); }

    /// <summary>
    /// Loads the library from the database. Music is sorted by artist, year, album, disc and track;
    /// Mixes by DJ (their folder) and title.
    /// </summary>
    public List<Track> Load()
    {
        var groups = new Dictionary<string, AlbumGroup>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<Track>();
        var roots = _kind == LibraryKind.Mixes ? Folders : [];

        foreach (var row in _db.GetTracks(_kind))
        {
            var path = AppPaths.FromStored(row.StoredPath);
            var artist = Clean(row.Artist) ?? "Unknown Artist";
            var albumArtist = Clean(row.AlbumArtist) ?? artist;
            var album = Clean(row.Album);
            // Untagged files are grouped by folder so they don't all merge into one "album".
            var key = album is null
                ? "folder|" + System.IO.Path.GetDirectoryName(path)
                : albumArtist + "|" + album;

            if (!groups.TryGetValue(key, out var group))
            {
                group = new AlbumGroup
                {
                    Title = album ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "Unknown Album",
                    Artist = albumArtist,
                    Year = row.Year,
                    SampleTrackPath = path,
                };
                groups[key] = group;
            }

            tracks.Add(new Track
            {
                Path = path,
                Title = Clean(row.Title) ?? System.IO.Path.GetFileNameWithoutExtension(path),
                Artist = artist,
                Group = group,
                Library = _kind,
                Collection = _kind == LibraryKind.Mixes ? CollectionFor(path, roots) : "",
                TrackNo = row.TrackNo,
                DiscNo = row.DiscNo,
                Duration = TimeSpan.FromMilliseconds(row.DurationMs),
            });
        }

        if (_kind == LibraryKind.Mixes)
        {
            return tracks
                .OrderBy(t => t.Collection, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        return tracks
            .OrderBy(t => t.Group.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Group.Year)
            .ThenBy(t => t.Group.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DiscNo)
            .ThenBy(t => t.TrackNo)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// The DJ for a mix: the first folder under the mixes folder it lives in
    /// (Mixes\DJ Kestrel\2024\set.mp3 → "DJ Kestrel"). Mixes sitting directly in the
    /// mixes folder are grouped under that folder's own name.
    /// </summary>
    public static string CollectionFor(string path, IEnumerable<string> roots)
    {
        var root = roots
            .Where(r => IsUnder(path, r))
            .OrderByDescending(r => r.Length)
            .FirstOrDefault();
        if (root is null) return System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? "";

        var relative = System.IO.Path.GetRelativePath(root, path);
        var separator = relative.IndexOfAny(['\\', '/']);
        return separator > 0
            ? relative[..separator]
            : System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
    }

    /// <summary>Brings the database in line with the folders on disk. Returns true if anything changed.</summary>
    public async Task<bool> ScanAsync()
    {
        await _scanLock.WaitAsync();
        try
        {
            return await Task.Run(Scan);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    bool Scan()
    {
        var known = _db.GetTracks(_kind).ToDictionary(t => t.StoredPath, t => t.Mtime, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var upserts = new List<TrackRow>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var otherFolders = _otherFolders();

        foreach (var folder in Folders.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", options))
            {
                if (!Extensions.Contains(System.IO.Path.GetExtension(file))) continue;
                if (!Owns(file, folder, otherFolders)) continue;

                var stored = AppPaths.ToStored(file);
                if (!seen.Add(stored)) continue;

                var mtime = File.GetLastWriteTimeUtc(file).Ticks;
                if (known.TryGetValue(stored, out var knownMtime) && knownMtime == mtime) continue;

                upserts.Add(ReadTags(file, stored, mtime));
            }
        }

        var removed = known.Keys.Where(p => !seen.Contains(p)).ToList();
        if (upserts.Count == 0 && removed.Count == 0) return false;

        _db.SaveScan(_kind, upserts, removed);
        return true;
    }

    /// <summary>
    /// A file belongs to the library whose folder is the deepest one containing it,
    /// so a Mixes folder inside the Music folder only shows up in Mixes.
    /// </summary>
    public static bool Owns(string file, string ownFolder, IEnumerable<string> otherFolders) =>
        !otherFolders.Any(other => IsUnder(file, other) && other.Length > ownFolder.Length);

    public static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);

    static TrackRow ReadTags(string file, string stored, long mtime)
    {
        try
        {
            using var f = TagLib.File.Create(file);
            var tag = f.Tag;
            return new TrackRow(
                stored, mtime,
                tag.Title,
                tag.JoinedPerformers,
                tag.JoinedAlbumArtists,
                tag.Album,
                (int)tag.Year,
                (int)tag.Track,
                (int)tag.Disc,
                (long)f.Properties.Duration.TotalMilliseconds);
        }
        catch
        {
            // Unreadable tags: keep the file, named after itself.
            return new TrackRow(stored, mtime, null, null, null, null, 0, 0, 0, 0);
        }
    }

    // ---- Watching ----

    public void Rewatch()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();

        foreach (var folder in Folders.Where(Directory.Exists))
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += OnFileSystemChange;
            watcher.Deleted += OnFileSystemChange;
            watcher.Renamed += OnFileSystemChange;
            watcher.Changed += OnFileSystemChange;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    void OnFileSystemChange(object sender, FileSystemEventArgs e)
    {
        // Folder copies fire hundreds of events; wait for them to settle before rescanning.
        _debounce?.Dispose();
        _debounce = new Timer(async _ =>
        {
            if (await ScanAsync()) Changed?.Invoke();
        }, null, WatchDebounce, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        foreach (var w in _watchers) w.Dispose();
    }
}
