//! The library database, `data\melora.db`, in exactly 2.x's layout so either version can open it.

use rusqlite::{Connection, params};

use crate::paths::{from_stored, to_stored};

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Kind {
    Music,
    Mixes,
    /// Internet radio: stations rather than files.
    Radio,
}

impl Kind {
    fn key(self) -> &'static str {
        match self {
            Kind::Music => "music",
            Kind::Mixes => "mixes",
            Kind::Radio => "radio",
        }
    }
}

/// A scanned file as stored. The path is in stored form (see `paths`).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct TrackRow {
    pub stored_path: String,
    /// Last write time in .NET ticks (100 ns since year 1, UTC), as 2.x stores it.
    pub mtime: i64,
    pub title: Option<String>,
    pub artist: Option<String>,
    pub album_artist: Option<String>,
    pub album: Option<String>,
    pub year: i32,
    pub track_no: i32,
    pub disc_no: i32,
    pub duration_ms: i64,
}

pub struct Db {
    conn: Connection,
    /// Melora's folder, which stored paths are relative to.
    base: String,
}

// The tables 2.x creates, all of them, so a database made here is one 2.x can use too.
const SCHEMA: &str = "
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
    );";

impl Db {
    /// Opens (or creates) the database at `file`; stored paths are relative to `base`.
    pub fn open(file: &std::path::Path, base: &str) -> rusqlite::Result<Db> {
        let conn = Connection::open(file)?;
        conn.execute_batch(SCHEMA)?;
        Ok(Db {
            conn,
            base: base.to_string(),
        })
    }

    pub fn folders(&self, kind: Kind) -> rusqlite::Result<Vec<String>> {
        let mut q = self
            .conn
            .prepare("SELECT path FROM folders WHERE library = ?1 ORDER BY path")?;
        let rows = q.query_map([kind.key()], |r| r.get::<_, String>(0))?;
        rows.map(|r| r.map(|p| from_stored(&p, &self.base)))
            .collect()
    }

    pub fn add_folder(&self, kind: Kind, full: &str) -> rusqlite::Result<()> {
        self.conn.execute(
            "INSERT OR IGNORE INTO folders (library, path) VALUES (?1, ?2)",
            params![kind.key(), to_stored(full, &self.base)],
        )?;
        Ok(())
    }

    pub fn tracks(&self, kind: Kind) -> rusqlite::Result<Vec<TrackRow>> {
        let mut q = self.conn.prepare(
            "SELECT path, mtime, title, artist, album_artist, album, year, track_no, disc_no, duration_ms
             FROM tracks WHERE library = ?1",
        )?;
        let rows = q.query_map([kind.key()], |r| {
            Ok(TrackRow {
                stored_path: r.get(0)?,
                mtime: r.get(1)?,
                title: r.get(2)?,
                artist: r.get(3)?,
                album_artist: r.get(4)?,
                album: r.get(5)?,
                year: r.get(6)?,
                track_no: r.get(7)?,
                disc_no: r.get(8)?,
                duration_ms: r.get(9)?,
            })
        })?;
        rows.collect()
    }

    /// Saved stations as (id, name, address), in the order they were added.
    pub fn stations(&self) -> rusqlite::Result<Vec<(i64, String, String)>> {
        let mut q = self
            .conn
            .prepare("SELECT id, name, url FROM stations ORDER BY id")?;
        let rows = q.query_map([], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))?;
        rows.collect()
    }

    pub fn add_station(&self, name: &str, url: &str) -> rusqlite::Result<i64> {
        self.conn.execute(
            "INSERT INTO stations (name, url) VALUES (?1, ?2)",
            params![name, url],
        )?;
        Ok(self.conn.last_insert_rowid())
    }

    pub fn update_station(&self, id: i64, name: &str, url: &str) -> rusqlite::Result<()> {
        self.conn.execute(
            "UPDATE stations SET name = ?1, url = ?2 WHERE id = ?3",
            params![name, url, id],
        )?;
        Ok(())
    }

    pub fn delete_station(&self, id: i64) -> rusqlite::Result<()> {
        self.conn
            .execute("DELETE FROM stations WHERE id = ?1", [id])?;
        Ok(())
    }

    /// A cached online lookup as 2.x keeps it: the address found (None: nothing was found) and
    /// when it was checked (Unix seconds). None if it was never looked up.
    pub fn cover(&self, key: &str) -> Option<(Option<String>, i64)> {
        self.conn
            .query_row(
                "SELECT url, checked_at FROM covers WHERE key = ?1",
                [key],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )
            .ok()
    }

    pub fn set_cover(&self, key: &str, url: Option<&str>, checked_at: i64) -> rusqlite::Result<()> {
        self.conn.execute(
            "INSERT INTO covers (key, url, checked_at) VALUES (?1, ?2, ?3)
             ON CONFLICT (key) DO UPDATE SET url = excluded.url, checked_at = excluded.checked_at",
            params![key, url, checked_at],
        )?;
        Ok(())
    }

    /// A setting as 2.x stores it (key and text value), or None when it was never set.
    pub fn setting(&self, key: &str) -> Option<String> {
        self.conn
            .query_row("SELECT value FROM settings WHERE key = ?1", [key], |r| {
                r.get(0)
            })
            .ok()
    }

    pub fn set_setting(&self, key: &str, value: &str) -> rusqlite::Result<()> {
        self.conn.execute(
            "INSERT INTO settings (key, value) VALUES (?1, ?2) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            params![key, value],
        )?;
        Ok(())
    }

    /// Saves what a scan found, all or nothing.
    pub fn save_scan(
        &mut self,
        kind: Kind,
        upserts: &[TrackRow],
        removed: &[String],
    ) -> rusqlite::Result<()> {
        let tx = self.conn.transaction()?;
        {
            // Windows paths ignore case but the UNIQUE index doesn't: drop the old spelling of a
            // file renamed only in case (song.flac → Song.flac) so it isn't stored twice.
            let mut drop_case =
                tx.prepare("DELETE FROM tracks WHERE path = ?1 COLLATE NOCASE AND path <> ?1")?;
            let mut upsert = tx.prepare(
                "INSERT INTO tracks (library, path, mtime, title, artist, album_artist, album, year, track_no, disc_no, duration_ms)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
                 ON CONFLICT (path) DO UPDATE SET
                     library = excluded.library, mtime = excluded.mtime, title = excluded.title,
                     artist = excluded.artist, album_artist = excluded.album_artist, album = excluded.album,
                     year = excluded.year, track_no = excluded.track_no, disc_no = excluded.disc_no,
                     duration_ms = excluded.duration_ms",
            )?;
            for t in upserts {
                drop_case.execute([&t.stored_path])?;
                upsert.execute(params![
                    kind.key(),
                    t.stored_path,
                    t.mtime,
                    t.title,
                    t.artist,
                    t.album_artist,
                    t.album,
                    t.year,
                    t.track_no,
                    t.disc_no,
                    t.duration_ms
                ])?;
            }
            // Scoped to this library: a file that moved to the other library must survive this delete.
            let mut delete = tx.prepare("DELETE FROM tracks WHERE path = ?1 AND library = ?2")?;
            for p in removed {
                delete.execute(params![p, kind.key()])?;
            }
        }
        tx.commit()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn db() -> Db {
        let conn = Connection::open_in_memory().expect("memory db");
        conn.execute_batch(SCHEMA).expect("schema");
        Db {
            conn,
            base: r"C:\_Apps\Melora\".into(),
        }
    }

    fn row(path: &str, title: &str) -> TrackRow {
        TrackRow {
            stored_path: path.into(),
            mtime: 1,
            title: Some(title.into()),
            ..TrackRow::default()
        }
    }

    #[test]
    fn folders_come_back_as_full_paths() {
        let d = db();
        d.add_folder(Kind::Music, r"C:\Users\me\Music")
            .expect("add");
        d.add_folder(Kind::Music, r"C:\Users\me\Music")
            .expect("twice is fine");
        assert_eq!(
            d.folders(Kind::Music).expect("folders"),
            [r"C:\Users\me\Music"]
        );
        assert!(d.folders(Kind::Mixes).expect("folders").is_empty());
    }

    #[test]
    fn stations_add_edit_and_delete() {
        let d = db();
        let a = d.add_station("Groove Salad", "http://a/gs").expect("add");
        let b = d.add_station("BBC", "http://b/x").expect("add");
        d.update_station(a, "Groove Salad Classic", "http://a/gsc")
            .expect("update");
        d.delete_station(b).expect("delete");
        assert_eq!(
            d.stations().expect("stations"),
            [(
                a,
                "Groove Salad Classic".to_string(),
                "http://a/gsc".to_string()
            )]
        );
    }

    #[test]
    fn settings_save_and_overwrite() {
        let d = db();
        assert_eq!(d.setting("volume"), None);
        d.set_setting("volume", "0.5").expect("set");
        d.set_setting("volume", "0.8").expect("set");
        assert_eq!(d.setting("volume").as_deref(), Some("0.8"));
    }

    #[test]
    fn a_case_only_rename_is_stored_once() {
        let mut d = db();
        d.save_scan(Kind::Music, &[row(r"D:\m\song.flac", "old")], &[])
            .expect("save");
        d.save_scan(Kind::Music, &[row(r"D:\m\Song.flac", "new")], &[])
            .expect("save");
        let tracks = d.tracks(Kind::Music).expect("tracks");
        assert_eq!(tracks.len(), 1);
        assert_eq!(tracks[0].stored_path, r"D:\m\Song.flac");
        assert_eq!(tracks[0].title.as_deref(), Some("new"));
    }

    #[test]
    fn removing_only_touches_its_own_library() {
        let mut d = db();
        d.save_scan(Kind::Mixes, &[row(r"D:\m\set.mp3", "set")], &[])
            .expect("save");
        d.save_scan(Kind::Music, &[], &[r"D:\m\set.mp3".into()])
            .expect("save");
        assert_eq!(d.tracks(Kind::Mixes).expect("tracks").len(), 1);
    }
}
