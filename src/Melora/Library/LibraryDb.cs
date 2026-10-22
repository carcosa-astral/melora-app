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
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM folders WHERE library = $lib AND path = $path";
        cmd.Parameters.AddWithValue("$lib", Key(library));
        cmd.Parameters.AddWithValue("$path", AppPaths.ToStored(fullPath));
        cmd.ExecuteNonQuery();
        // Tracks that are no longer under any folder are dropped by the next scan.
    }

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
            cmd.CommandText = "DELETE FROM tracks WHERE path = $path";
            var path = cmd.Parameters.Add("$path", SqliteType.Text);
            foreach (var p in removedPaths)
            {
                path.Value = p;
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
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
