using Melora.Scrobbling;
using Microsoft.Data.Sqlite;

namespace Melora.Library;

/// <summary>A scanned file as stored in SQLite. Paths are in stored (possibly relative) form.</summary>
public sealed record TrackRow(
    string StoredPath, long Mtime, string? Title, string? Artist, string? AlbumArtist,
    string? Album, int Year, int TrackNo, int DiscNo, long DurationMs);

public sealed class LibraryDb
{
    readonly string _connectionString;

    public LibraryDb(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        using var db = Open();
        Exec(db, """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS folders (
                id      INTEGER PRIMARY KEY,
                library TEXT NOT NULL,
                path    TEXT NOT NULL,
                UNIQUE (library, path)
            );
            CREATE TABLE IF NOT EXISTS tracks (
                id           INTEGER PRIMARY KEY,
                library      TEXT NOT NULL,
                path         TEXT NOT NULL UNIQUE,
                mtime        INTEGER NOT NULL,
                title        TEXT,
                artist       TEXT,
                album_artist TEXT,
                album        TEXT,
                year         INTEGER NOT NULL DEFAULT 0,
                track_no     INTEGER NOT NULL DEFAULT 0,
                disc_no      INTEGER NOT NULL DEFAULT 0,
                duration_ms  INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS tracks_library ON tracks (library);
            CREATE TABLE IF NOT EXISTS stations (
                id   INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                url  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS scrobbles (
                id           INTEGER PRIMARY KEY,
                service      TEXT NOT NULL,
                artist       TEXT NOT NULL,
                title        TEXT NOT NULL,
                album        TEXT,
                album_artist TEXT,
                track_no     INTEGER NOT NULL DEFAULT 0,
                duration_ms  INTEGER NOT NULL DEFAULT 0,
                started_at   INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS loves (
                artist TEXT NOT NULL COLLATE NOCASE,
                title  TEXT NOT NULL COLLATE NOCASE,
                album  TEXT,
                mbid   TEXT,
                loved  INTEGER NOT NULL,
                PRIMARY KEY (artist, title)
            );
            CREATE TABLE IF NOT EXISTS love_queue (
                service TEXT NOT NULL,
                artist  TEXT NOT NULL COLLATE NOCASE,
                title   TEXT NOT NULL COLLATE NOCASE,
                PRIMARY KEY (service, artist, title)
            );
            CREATE TABLE IF NOT EXISTS covers (
                key        TEXT PRIMARY KEY,
                url        TEXT,
                checked_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
    }

    SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        return db;
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static string Key(LibraryKind library) => library.ToString().ToLowerInvariant();

    // ---- Folders ----

    public List<string> GetFolders(LibraryKind library)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path FROM folders WHERE library = $lib ORDER BY path";
        cmd.Parameters.AddWithValue("$lib", Key(library));
        var result = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add(AppPaths.FromStored(r.GetString(0)));
        return result;
    }

    public void AddFolder(LibraryKind library, string fullPath)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO folders (library, path) VALUES ($lib, $path)";
        cmd.Parameters.AddWithValue("$lib", Key(library));
        cmd.Parameters.AddWithValue("$path", AppPaths.ToStored(fullPath));
        cmd.ExecuteNonQuery();
    }

    public void RemoveFolder(LibraryKind library, string fullPath)
    {
        using var db = Open();

        // Compare resolved paths: a folder may be stored relative or absolute.
        var target = NormalizeFolder(fullPath);
        var ids = new List<long>();
        using (var query = db.CreateCommand())
        {
            query.CommandText = "SELECT id, path FROM folders WHERE library = $lib";
            query.Parameters.AddWithValue("$lib", Key(library));
            using var r = query.ExecuteReader();
            while (r.Read())
            {
                if (string.Equals(NormalizeFolder(AppPaths.FromStored(r.GetString(1))), target, StringComparison.OrdinalIgnoreCase))
                    ids.Add(r.GetInt64(0));
            }
        }

        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM folders WHERE id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var folderId in ids)
        {
            id.Value = folderId;
            cmd.ExecuteNonQuery();
        }
        // Tracks that are no longer under any folder are dropped by the next scan.
    }

    static string NormalizeFolder(string path) => System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');

    // ---- Tracks ----

    public List<TrackRow> GetTracks(LibraryKind library)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT path, mtime, title, artist, album_artist, album, year, track_no, disc_no, duration_ms
            FROM tracks WHERE library = $lib
            """;
        cmd.Parameters.AddWithValue("$lib", Key(library));
        var result = new List<TrackRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new TrackRow(
                r.GetString(0), r.GetInt64(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt64(9)));
        }
        return result;
    }

    public void SaveScan(LibraryKind library, IReadOnlyCollection<TrackRow> upserts, IReadOnlyCollection<string> removedPaths)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tracks (library, path, mtime, title, artist, album_artist, album, year, track_no, disc_no, duration_ms)
                VALUES ($lib, $path, $mtime, $title, $artist, $albumArtist, $album, $year, $trackNo, $discNo, $duration)
                ON CONFLICT (path) DO UPDATE SET
                    library = excluded.library, mtime = excluded.mtime, title = excluded.title,
                    artist = excluded.artist, album_artist = excluded.album_artist, album = excluded.album,
                    year = excluded.year, track_no = excluded.track_no, disc_no = excluded.disc_no,
                    duration_ms = excluded.duration_ms
                """;
            var lib = cmd.Parameters.AddWithValue("$lib", Key(library));
            var path = cmd.Parameters.Add("$path", SqliteType.Text);
            var mtime = cmd.Parameters.Add("$mtime", SqliteType.Integer);
            var title = cmd.Parameters.Add("$title", SqliteType.Text);
            var artist = cmd.Parameters.Add("$artist", SqliteType.Text);
            var albumArtist = cmd.Parameters.Add("$albumArtist", SqliteType.Text);
            var album = cmd.Parameters.Add("$album", SqliteType.Text);
            var year = cmd.Parameters.Add("$year", SqliteType.Integer);
            var trackNo = cmd.Parameters.Add("$trackNo", SqliteType.Integer);
            var discNo = cmd.Parameters.Add("$discNo", SqliteType.Integer);
            var duration = cmd.Parameters.Add("$duration", SqliteType.Integer);

            foreach (var t in upserts)
            {
                path.Value = t.StoredPath;
                mtime.Value = t.Mtime;
                title.Value = (object?)t.Title ?? DBNull.Value;
                artist.Value = (object?)t.Artist ?? DBNull.Value;
                albumArtist.Value = (object?)t.AlbumArtist ?? DBNull.Value;
                album.Value = (object?)t.Album ?? DBNull.Value;
                year.Value = t.Year;
                trackNo.Value = t.TrackNo;
                discNo.Value = t.DiscNo;
                duration.Value = t.DurationMs;
                cmd.ExecuteNonQuery();
            }
        }

        using (var cmd = db.CreateCommand())
        {
            // Scoped to this library: a file that moved to the other library must survive this delete.
            cmd.CommandText = "DELETE FROM tracks WHERE path = $path AND library = $lib";
            cmd.Parameters.AddWithValue("$lib", Key(library));
            var path = cmd.Parameters.Add("$path", SqliteType.Text);
            foreach (var p in removedPaths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    // ---- Radio stations ----

    public List<(long Id, string Name, string Url)> GetStations()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, url FROM stations";
        var result = new List<(long, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) result.Add((r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return result;
    }

    public long AddStation(string name, string url)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO stations (name, url) VALUES ($name, $url) RETURNING id";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$url", url);
        return (long)cmd.ExecuteScalar()!;
    }

    public void UpdateStation(long id, string name, string url)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE stations SET name = $name, url = $url WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$url", url);
        cmd.ExecuteNonQuery();
    }

    public void DeleteStation(long id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM stations WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Scrobble queue (one row per play per service, until it's been sent) ----

    public void AddScrobble(string service, Scrobble play)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO scrobbles (service, artist, title, album, album_artist, track_no, duration_ms, started_at)
            VALUES ($service, $artist, $title, $album, $albumArtist, $trackNo, $duration, $startedAt)
            """;
        cmd.Parameters.AddWithValue("$service", service);
        cmd.Parameters.AddWithValue("$artist", play.Artist);
        cmd.Parameters.AddWithValue("$title", play.Title);
        cmd.Parameters.AddWithValue("$album", (object?)play.Album ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$albumArtist", (object?)play.AlbumArtist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$trackNo", play.TrackNo);
        cmd.Parameters.AddWithValue("$duration", (long)play.Duration.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$startedAt", play.StartedAt.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>The oldest queued plays for a service.</summary>
    public List<(long Id, Scrobble Play)> GetScrobbles(string service, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, artist, title, album, album_artist, track_no, duration_ms, started_at
            FROM scrobbles WHERE service = $service ORDER BY started_at, id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$service", service);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = new List<(long, Scrobble)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add((r.GetInt64(0), new Scrobble(
                r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.GetInt32(5),
                TimeSpan.FromMilliseconds(r.GetInt64(6)),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(7)))));
        }
        return result;
    }

    public void DeleteScrobbles(IEnumerable<long> ids)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM scrobbles WHERE id = $id";
        var id = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var value in ids)
        {
            id.Value = value;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public int CountScrobbles(string? service = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = service is null
            ? "SELECT COUNT(*) FROM scrobbles"
            : "SELECT COUNT(*) FROM scrobbles WHERE service = $service";
        if (service is not null) cmd.Parameters.AddWithValue("$service", service);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- Loves (one row per track; the queue holds services that haven't been told yet) ----

    /// <summary>Whether a track is loved; null if it's never been loved or un-loved.</summary>
    public bool? GetLoved(string artist, string title)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT loved FROM loves WHERE artist = $artist AND title = $title";
        cmd.Parameters.AddWithValue("$artist", artist);
        cmd.Parameters.AddWithValue("$title", title);
        return cmd.ExecuteScalar() is long loved ? loved != 0 : null;
    }

    /// <summary>Loves or un-loves a track here, and queues telling each of <paramref name="services"/>.</summary>
    public void SetLoved(Love love, IEnumerable<string> services)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO loves (artist, title, album, mbid, loved) VALUES ($artist, $title, $album, $mbid, $loved)
                ON CONFLICT (artist, title) DO UPDATE SET album = excluded.album, mbid = excluded.mbid, loved = excluded.loved
                """;
            cmd.Parameters.AddWithValue("$artist", love.Artist);
            cmd.Parameters.AddWithValue("$title", love.Title);
            cmd.Parameters.AddWithValue("$album", (object?)love.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$mbid", (object?)love.RecordingMbid ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$loved", love.Loved ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "INSERT OR IGNORE INTO love_queue (service, artist, title) VALUES ($service, $artist, $title)";
            var service = cmd.Parameters.Add("$service", SqliteType.Text);
            cmd.Parameters.AddWithValue("$artist", love.Artist);
            cmd.Parameters.AddWithValue("$title", love.Title);
            foreach (var id in services)
            {
                service.Value = id;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>
    /// Records what a service says about a track (e.g. loved on the Last.fm website), unless a
    /// change made in Melora is still waiting to be sent.
    /// </summary>
    public void SyncLoved(string artist, string title, bool loved)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO loves (artist, title, loved)
            SELECT $artist, $title, $loved
            WHERE NOT EXISTS (SELECT 1 FROM love_queue WHERE artist = $artist AND title = $title)
            ON CONFLICT (artist, title) DO UPDATE SET loved = excluded.loved
            """;
        cmd.Parameters.AddWithValue("$artist", artist);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$loved", loved ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Loves and un-loves a service still has to be told about.</summary>
    public List<Love> GetPendingLoves(string service)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT l.artist, l.title, l.album, l.mbid, l.loved
            FROM love_queue q JOIN loves l ON l.artist = q.artist AND l.title = q.title
            WHERE q.service = $service
            """;
        cmd.Parameters.AddWithValue("$service", service);
        var result = new List<Love>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            result.Add(new Love(r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetInt64(4) != 0));
        }
        return result;
    }

    public void ClearPendingLove(string service, string artist, string title)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM love_queue WHERE service = $service AND artist = $artist AND title = $title";
        cmd.Parameters.AddWithValue("$service", service);
        cmd.Parameters.AddWithValue("$artist", artist);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.ExecuteNonQuery();
    }

    // ---- Cover art addresses found online, for Discord (url is null when nothing was found) ----

    /// <summary>The cached result for a lookup key: Found is false if it was never looked up.</summary>
    public (bool Found, string? Url, DateTimeOffset CheckedAt) GetCover(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT url, checked_at FROM covers WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (false, null, default);
        return (true, r.IsDBNull(0) ? null : r.GetString(0), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1)));
    }

    public void SetCover(string key, string? url)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO covers (key, url, checked_at) VALUES ($key, $url, $at)
            ON CONFLICT (key) DO UPDATE SET url = excluded.url, checked_at = excluded.checked_at
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$url", (object?)url ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    // ---- Settings ----

    public string? GetSetting(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void DeleteSetting(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public void SetSetting(string key, string value)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
