using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Melora.Library;

/// <summary>
/// Scans watched folders into the database, keeps them watched, and builds the
/// in-memory track list (grouped by album) for the UI.
/// </summary>
public sealed partial class LibraryService : IDisposable
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
    /// Mixes by DJ (their folder), then date, oldest first.
    /// </summary>
    public List<Track> Load()
    {
        var roots = _kind == LibraryKind.Mixes ? Folders : [];

        // First work out which album each file belongs to, then make the albums: an album's artist
        // ("Various Artists" or not) depends on all of its tracks.
        var files = _db.GetTracks(_kind).Select(row =>
        {
            var path = AppPaths.FromStored(row.StoredPath);
            var folder = System.IO.Path.GetDirectoryName(path) ?? "";
            // CD1\, Disc 2\ and the like are part of the album in the folder above.
            var discFromFolder = DiscFolderNumber(System.IO.Path.GetFileName(folder));
            var albumFolder = discFromFolder > 0 ? System.IO.Path.GetDirectoryName(folder) ?? folder : folder;
            var album = Clean(row.Album);
            var albumArtist = Clean(row.AlbumArtist);

            // Tagged album artist: that artist's album, wherever its files are. Otherwise the album
            // in its folder, so a compilation without an album artist stays together. Files with
            // no album tag are grouped by folder so they don't all merge into one "album".
            var key = albumArtist is not null && album is not null ? $"artist|{albumArtist}|{album}"
                : album is not null ? $"folder|{albumFolder}|{album}"
                : $"folder|{folder}";
            var disc = row.DiscNo > 0 ? row.DiscNo : discFromFolder;
            return (Row: row, Path: path, Folder: folder, Album: album, AlbumArtist: albumArtist, Key: key, Disc: disc);
        }).ToList();

        var groups = new Dictionary<string, AlbumGroup>(StringComparer.OrdinalIgnoreCase);
        var discLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // albums with more than one disc
        foreach (var album in files.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = album.First();
            var artists = album.Select(f => Clean(f.Row.Artist) ?? "Unknown Artist").Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            groups[album.Key] = new AlbumGroup
            {
                Title = first.Album ?? System.IO.Path.GetFileName(first.Folder) ?? "Unknown Album",
                Artist = first.AlbumArtist ?? (artists.Count == 1 ? artists[0] : VariousArtists),
                Year = album.Select(f => f.Row.Year).FirstOrDefault(y => y > 0),
                SampleTrackPath = first.Path,
                ArtSize = _kind == LibraryKind.Mixes ? AlbumGroup.DjArtSize : AlbumGroup.AlbumArtSize,
            };
            if (album.Select(f => f.Disc).Where(d => d > 0).Distinct().Count() > 1) discLabels.Add(album.Key);
        }

        var tracks = files.Select(f => new Track
        {
            Path = f.Path,
            Title = Clean(f.Row.Title) ?? System.IO.Path.GetFileNameWithoutExtension(f.Path),
            Artist = Clean(f.Row.Artist) ?? "Unknown Artist",
            Group = groups[f.Key],
            Album = f.Album,
            AlbumArtist = f.AlbumArtist,
            Library = _kind,
            Collection = _kind == LibraryKind.Mixes ? CollectionFor(f.Path, roots) : "",
            TrackNo = f.Row.TrackNo,
            DiscNo = f.Disc,
            Disc = discLabels.Contains(f.Key) && f.Disc > 0 ? $"Disc {f.Disc}" : "",
            Duration = TimeSpan.FromMilliseconds(f.Row.DurationMs),
        }).ToList();

        if (_kind == LibraryKind.Mixes)
        {
            return tracks
                .OrderBy(t => t.Collection, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(t => MixDate(t.Title, t.Group.Year) ?? DateOnly.MaxValue) // undated mixes last
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

    /// <summary>What an album is listed under when it has no album artist and its tracks have several artists.</summary>
    public const string VariousArtists = "Various Artists";

    /// <summary>The disc number of a folder named like "CD1", "CD 2", "Disc 3" or "Disk04"; 0 for any other name.</summary>
    public static int DiscFolderNumber(string? name)
    {
        var match = name is null ? Match.Empty : DiscFolder().Match(name);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    [GeneratedRegex(@"^(?:cd|disc|disk)[\s._-]*(\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscFolder();

    /// <summary>
    /// When a mix was played: a date in its title like "(2020-10-14)" (also with dots or slashes),
    /// or failing that the year tag, as 1 January. Null when there's neither.
    /// </summary>
    public static DateOnly? MixDate(string title, int year)
    {
        foreach (Match m in DateInTitle().Matches(title))
        {
            if (DateOnly.TryParseExact($"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}", "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return date;
        }
        return year is > 0 and < 10000 ? new DateOnly(year, 1, 1) : null;
    }

    [GeneratedRegex(@"(?<!\d)((?:19|20)\d{2})[-./](\d{2})[-./](\d{2})(?!\d)")]
    private static partial Regex DateInTitle();

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

        foreach (var folder in Folders)
        {
            if (!Directory.Exists(folder))
            {
                // An unplugged drive or a network share that's down: keep what was there rather than
                // emptying the library, and pick it up again when the folder comes back.
                Log.Write($"{_kind} folder not found, keeping its tracks: {folder}");
                foreach (var path in known.Keys.Where(p => IsUnder(AppPaths.FromStored(p), folder))) seen.Add(path);
                continue;
            }

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
        catch (Exception e)
        {
            // Unreadable tags: keep the file, named after itself.
            Log.Write($"Couldn't read the tags of {file}: {e.Message}");
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
