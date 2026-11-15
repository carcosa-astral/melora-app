//! Scans library folders into the database and turns the stored tracks into albums (Music) or
//! DJs (Mixes) for the UI, with the same rules as 2.x's LibraryService.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use lofty::prelude::*;

use crate::db::{Db, Kind, TrackRow};
use crate::folder;
use crate::paths::{from_stored, to_stored};

/// The file types the library picks up (as in 2.x).
const EXTENSIONS: [&str; 3] = ["flac", "opus", "mp3"];
const UNKNOWN_ARTIST: &str = "Unknown Artist";
/// What an album is listed under when it has no album artist and its tracks have several artists.
pub const VARIOUS_ARTISTS: &str = "Various Artists";

/// An album (Music) or a mix's group (Mixes): what a heading in the list shows.
#[derive(Debug, Clone, PartialEq)]
pub struct Album {
    pub title: String,
    pub artist: String,
    pub year: i32,
    /// A track of the album, to find its cover from.
    pub sample_path: String,
}

#[derive(Debug, Clone, PartialEq)]
pub struct Track {
    pub path: String,
    pub title: String,
    pub artist: String,
    /// Index into `Library::albums`.
    pub album: usize,
    /// For mixes: the DJ, i.e. the top-level folder the mix sits in under a mixes folder.
    pub collection: String,
    pub track_no: i32,
    pub disc_no: i32,
    /// "Disc 2" for a track on an album with more than one disc; empty otherwise.
    pub disc: String,
    pub duration_ms: i64,
    /// The album and album artist as tagged (None when untagged), for scrobbling.
    pub album_tag: Option<String>,
    pub album_artist_tag: Option<String>,
}

#[derive(Debug, Default)]
pub struct Library {
    pub albums: Vec<Album>,
    /// In list order.
    pub tracks: Vec<Track>,
}

fn clean(s: &Option<String>) -> Option<String> {
    s.as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

fn parent(path: &str) -> &str {
    path.rfind('\\').map_or("", |i| &path[..i])
}

fn name(path: &str) -> &str {
    let path = path.trim_end_matches('\\');
    path.rfind('\\').map_or(path, |i| &path[i + 1..])
}

fn stem(path: &str) -> &str {
    let n = name(path);
    n.rfind('.').map_or(n, |i| &n[..i])
}

/// Turns stored rows into the list: Music sorted by artist, year, album, disc and track; Mixes
/// by DJ (their folder), then date, oldest first.
pub fn load(rows: &[TrackRow], kind: Kind, roots: &[String], base: &str) -> Library {
    struct File<'a> {
        row: &'a TrackRow,
        path: String,
        folder: String,
        album: Option<String>,
        album_artist: Option<String>,
        key: String,
        disc: i32,
    }

    // First work out which album each file belongs to, then make the albums: an album's artist
    // ("Various Artists" or not) depends on all of its tracks.
    let files: Vec<File> = rows
        .iter()
        .map(|row| {
            let path = from_stored(&row.stored_path, base);
            let folder = parent(&path).to_string();
            // CD1\, Disc 2\ and the like are part of the album in the folder above.
            let disc_from_folder = disc_folder_number(name(&folder));
            let album_folder = if disc_from_folder > 0 {
                parent(&folder).to_string()
            } else {
                folder.clone()
            };
            let (album, album_artist) = (clean(&row.album), clean(&row.album_artist));
            // Tagged album artist: that artist's album, wherever its files are. Otherwise the album
            // in its folder, so a compilation without an album artist stays together. Files with
            // no album tag are grouped by folder so they don't all merge into one "album".
            let key = match (&album_artist, &album) {
                (Some(aa), Some(a)) => format!("artist|{aa}|{a}"),
                (None, Some(a)) => format!("folder|{album_folder}|{a}"),
                _ => format!("folder|{folder}"),
            }
            .to_lowercase();
            let disc = if row.disc_no > 0 {
                row.disc_no
            } else {
                disc_from_folder
            };
            File {
                row,
                path,
                folder,
                album,
                album_artist,
                key,
                disc,
            }
        })
        .collect();

    let mut albums = Vec::new();
    let mut index: HashMap<&str, usize> = HashMap::new();
    let mut multi_disc = HashSet::new();
    for f in &files {
        if index.contains_key(f.key.as_str()) {
            continue;
        }
        let members: Vec<&File> = files.iter().filter(|g| g.key == f.key).collect();
        let mut artists: Vec<String> = Vec::new();
        for m in &members {
            let a = clean(&m.row.artist).unwrap_or_else(|| UNKNOWN_ARTIST.into());
            if !artists.iter().any(|x| x.to_lowercase() == a.to_lowercase()) {
                artists.push(a);
            }
        }
        let artist = f.album_artist.clone().unwrap_or_else(|| {
            if artists.len() == 1 {
                artists[0].clone()
            } else {
                VARIOUS_ARTISTS.into()
            }
        });
        let discs: HashSet<i32> = members.iter().map(|m| m.disc).filter(|&d| d > 0).collect();
        if discs.len() > 1 {
            multi_disc.insert(f.key.as_str());
        }
        index.insert(f.key.as_str(), albums.len());
        albums.push(Album {
            title: f
                .album
                .clone()
                .unwrap_or_else(|| name(&f.folder).to_string()),
            artist,
            year: members
                .iter()
                .map(|m| m.row.year)
                .find(|&y| y > 0)
                .unwrap_or(0),
            sample_path: f.path.clone(),
        });
    }

    let mut tracks: Vec<Track> = files
        .iter()
        .map(|f| Track {
            path: f.path.clone(),
            title: clean(&f.row.title).unwrap_or_else(|| stem(&f.path).to_string()),
            artist: clean(&f.row.artist).unwrap_or_else(|| UNKNOWN_ARTIST.into()),
            album: index[f.key.as_str()],
            collection: if kind == Kind::Mixes {
                collection_for(&f.path, roots)
            } else {
                String::new()
            },
            track_no: f.row.track_no,
            disc_no: f.disc,
            disc: if multi_disc.contains(f.key.as_str()) && f.disc > 0 {
                format!("Disc {}", f.disc)
            } else {
                String::new()
            },
            duration_ms: f.row.duration_ms,
            album_tag: f.album.clone(),
            album_artist_tag: f.album_artist.clone(),
        })
        .collect();

    match kind {
        Kind::Mixes => tracks.sort_by_cached_key(|t| {
            let date = mix_date(&t.title, albums[t.album].year).unwrap_or((i32::MAX, 0, 0)); // undated mixes last
            (t.collection.to_lowercase(), date, t.title.to_lowercase())
        }),
        Kind::Music | Kind::Radio => tracks.sort_by_cached_key(|t| {
            let a = &albums[t.album];
            (
                a.artist.to_lowercase(),
                a.year,
                a.title.to_lowercase(),
                t.disc_no,
                t.track_no,
                t.path.to_lowercase(),
            )
        }),
    }
    Library { albums, tracks }
}

/// The disc number of a folder named like "CD1", "CD 2", "Disc 3" or "Disk04"; 0 for any other name.
pub fn disc_folder_number(name: &str) -> i32 {
    let lower = name.to_lowercase();
    let Some(rest) = ["disc", "disk", "cd"]
        .iter()
        .find_map(|p| lower.strip_prefix(p))
    else {
        return 0;
    };
    let digits = rest.trim_start_matches([' ', '\t', '.', '_', '-']);
    if (1..=2).contains(&digits.len()) && digits.bytes().all(|b| b.is_ascii_digit()) {
        digits.parse().unwrap_or(0)
    } else {
        0
    }
}

/// When a mix was played, as (year, month, day): a date in its title like "(2020-10-14)" (also
/// with dots or slashes), or failing that the year tag, as 1 January. None when there's neither.
pub fn mix_date(title: &str, year: i32) -> Option<(i32, u32, u32)> {
    let b = title.as_bytes();
    let digit = |i: usize| b.get(i).is_some_and(u8::is_ascii_digit);
    let num = |from: usize, len: usize| {
        title
            .get(from..from + len)
            .and_then(|s| s.parse::<u32>().ok())
    };
    for i in 0..b.len() {
        // yyyy-mm-dd with no digit right before or after, the year starting 19 or 20.
        let shape = (i == 0 || !digit(i - 1))
            && (title.get(i..i + 2) == Some("19") || title.get(i..i + 2) == Some("20"))
            && (i..i + 4).all(digit)
            && matches!(b.get(i + 4), Some(b'-' | b'.' | b'/'))
            && digit(i + 5)
            && digit(i + 6)
            && matches!(b.get(i + 7), Some(b'-' | b'.' | b'/'))
            && digit(i + 8)
            && digit(i + 9)
            && !digit(i + 10);
        if let (true, Some(y), Some(m), Some(d)) = (shape, num(i, 4), num(i + 5, 2), num(i + 8, 2))
            && (1..=12).contains(&m)
            && d >= 1
            && d <= days_in_month(y, m)
        {
            return Some((y as i32, m, d));
        }
    }
    (1..10000).contains(&year).then_some((year, 1, 1))
}

fn days_in_month(y: u32, m: u32) -> u32 {
    match m {
        2 if (y.is_multiple_of(4) && !y.is_multiple_of(100)) || y.is_multiple_of(400) => 29,
        2 => 28,
        4 | 6 | 9 | 11 => 30,
        _ => 31,
    }
}

pub fn is_under(path: &str, folder: &str) -> bool {
    let prefix = format!("{}\\", folder.trim_end_matches(['\\', '/']));
    path.get(..prefix.len())
        .is_some_and(|head| head.eq_ignore_ascii_case(&prefix))
}

/// A file belongs to the library whose folder is the deepest one containing it, so a Mixes
/// folder inside the Music folder only shows up in Mixes.
pub fn owns(file: &str, own_folder: &str, other_folders: &[String]) -> bool {
    !other_folders
        .iter()
        .any(|o| is_under(file, o) && o.len() > own_folder.len())
}

/// The DJ for a mix: the first folder under the mixes folder it lives in
/// (Mixes\DJ Kestrel\2024\set.mp3 → "DJ Kestrel"). Mixes sitting directly in the mixes folder
/// are grouped under that folder's own name.
pub fn collection_for(path: &str, roots: &[String]) -> String {
    let Some(root) = roots
        .iter()
        .filter(|r| is_under(path, r))
        .max_by_key(|r| r.len())
    else {
        return name(parent(path)).to_string();
    };
    let relative = &path[root.trim_end_matches(['\\', '/']).len() + 1..];
    match relative.find(['\\', '/']) {
        Some(i) if i > 0 => relative[..i].to_string(),
        _ => name(root).to_string(),
    }
}

/// .NET ticks (100 ns since year 1, UTC) of a file's last write, as 2.x stores them.
fn mtime_ticks(path: &Path) -> i64 {
    const UNIX_EPOCH_TICKS: i64 = 621_355_968_000_000_000;
    let since = std::fs::metadata(path)
        .and_then(|m| m.modified())
        .ok()
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .unwrap_or_default();
    UNIX_EPOCH_TICKS + (since.as_nanos() / 100) as i64
}

/// Reads a file's tags; unreadable tags keep the file, named after itself.
fn read_tags(path: &Path, stored: String, mtime: i64) -> TrackRow {
    let mut row = TrackRow {
        stored_path: stored,
        mtime,
        ..TrackRow::default()
    };
    let file = match lofty::read_from_path(path) {
        Ok(f) => f,
        Err(e) => {
            // Unreadable tags: keep the file, named after itself.
            crate::log::write(&format!(
                "Couldn't read the tags of {}: {e}",
                path.display()
            ));
            return row;
        }
    };
    row.duration_ms = file.properties().duration().as_millis() as i64;
    let Some(tag) = file.primary_tag().or_else(|| file.first_tag()) else {
        return row;
    };
    let joined = |key: ItemKey| {
        let all: Vec<&str> = tag.get_strings(key).collect();
        (!all.is_empty()).then(|| all.join("; "))
    };
    row.title = tag.title().map(|s| s.into_owned());
    row.artist = joined(ItemKey::TrackArtist);
    row.album_artist = joined(ItemKey::AlbumArtist);
    row.album = tag.album().map(|s| s.into_owned());
    row.track_no = tag.track().unwrap_or(0) as i32;
    row.disc_no = tag.disk().unwrap_or(0) as i32;
    row.year = [ItemKey::Year, ItemKey::RecordingDate]
        .into_iter()
        .filter_map(|k| tag.get_string(k))
        .find_map(|s| s.get(..4).and_then(|y| y.parse().ok()))
        .unwrap_or(0);
    row
}

/// A file's MusicBrainz recording ID (Picard writes one), for loving on ListenBrainz.
pub fn recording_mbid(path: &Path) -> Option<String> {
    let file = lofty::read_from_path(path).ok()?;
    let tag = file.primary_tag().or_else(|| file.first_tag())?;
    tag.get_string(ItemKey::MusicBrainzRecordingId)
        .map(str::to_string)
        .filter(|s| !s.is_empty())
}

/// A file's MusicBrainz release ID (Picard writes one), for the album's exact cover.
pub fn release_mbid(path: &Path) -> Option<String> {
    let file = lofty::read_from_path(path).ok()?;
    let tag = file.primary_tag().or_else(|| file.first_tag())?;
    tag.get_string(ItemKey::MusicBrainzReleaseId)
        .map(str::to_string)
        .filter(|s| !s.is_empty())
}

/// Brings the database in line with the folders on disk. Returns true if anything changed.
pub fn scan(
    db: &mut Db,
    kind: Kind,
    other_folders: &[String],
    base: &str,
) -> rusqlite::Result<bool> {
    // A database from before case-only renames were handled can hold one file twice; reading it
    // again saves it once (save_scan drops the other spelling).
    let mut known: HashMap<String, (String, i64)> = HashMap::new();
    for t in db.tracks(kind)? {
        let entry = known
            .entry(t.stored_path.to_lowercase())
            .or_insert((t.stored_path.clone(), t.mtime));
        if entry.0 != t.stored_path {
            entry.1 = -1;
        }
    }
    let mut seen: HashSet<String> = HashSet::new();
    let mut upserts = Vec::new();

    for root in db.folders(kind)? {
        if !Path::new(&root).is_dir() {
            // An unplugged drive or a share that's down: keep what was there rather than emptying
            // the library, and pick it up again when the folder comes back.
            crate::log::write(&format!(
                "{kind:?} folder not found, keeping its tracks: {root}"
            ));
            for (_, (stored, _)) in known
                .iter()
                .filter(|(_, (s, _))| is_under(&from_stored(s, base), &root))
            {
                seen.insert(stored.to_lowercase());
            }
            continue;
        }
        for file in folder::files_with(Path::new(&root), &EXTENSIONS) {
            let full = file.to_string_lossy().into_owned();
            if !owns(&full, &root, other_folders) {
                continue;
            }
            let stored = to_stored(&full, base);
            if !seen.insert(stored.to_lowercase()) {
                continue;
            }
            let mtime = mtime_ticks(&file);
            if known
                .get(&stored.to_lowercase())
                .is_some_and(|(s, m)| *s == stored && *m == mtime)
            {
                continue;
            }
            upserts.push(read_tags(&file, stored, mtime));
        }
    }

    let removed: Vec<String> = known
        .into_iter()
        .filter(|(lower, _)| !seen.contains(lower))
        .map(|(_, (stored, _))| stored)
        .collect();
    if upserts.is_empty() && removed.is_empty() {
        return Ok(false);
    }
    db.save_scan(kind, &upserts, &removed)?;
    Ok(true)
}

#[cfg(test)]
mod tests {
    use super::*;

    const MUSIC: &str = r"D:\Music";
    const MIXES: &str = r"D:\Music\Mixes";

    #[test]
    fn a_nested_mixes_folder_owns_its_files() {
        let file = r"D:\Music\Mixes\DJ Kestrel\set.mp3";
        assert!(!owns(file, MUSIC, &[MIXES.into()]));
        assert!(owns(file, MIXES, &[MUSIC.into()]));
        assert!(owns(r"D:\Music\Album\01.flac", MUSIC, &[MIXES.into()]));
        assert!(owns(r"D:\Music\Mixes Old\a.mp3", MUSIC, &[MIXES.into()])); // not inside "Mixes"
    }

    #[test]
    fn mixes_are_grouped_by_their_top_level_folder() {
        for (file, dj) in [
            (r"D:\Music\Mixes\DJ Kestrel\set.mp3", "DJ Kestrel"),
            (
                r"D:\Music\Mixes\DJ Kestrel\2024\Summer\set.mp3",
                "DJ Kestrel",
            ),
            (r"D:\Music\Mixes\loose-set.mp3", "Mixes"),
        ] {
            assert_eq!(collection_for(file, &[MIXES.into()]), dj);
        }
        assert_eq!(
            collection_for(
                r"D:\Sets\Live\Mira Vale\a.opus",
                &[r"D:\Sets".into(), r"D:\Sets\Live".into()]
            ),
            "Mira Vale"
        );
    }

    #[test]
    fn mix_dates() {
        assert_eq!(
            mix_date("Club Set (2020-10-14) [LP Giobbi]", 0),
            Some((2020, 10, 14))
        );
        assert_eq!(
            mix_date("Live at EDC Mexico 2023 (Kinetic Field) (2023-03-02)", 0),
            Some((2023, 3, 2))
        );
        assert_eq!(mix_date("Boiler Room 2019.07.04", 0), Some((2019, 7, 4)));
        assert_eq!(mix_date("Set (2021-02-30)", 0), None); // not a real date
        assert_eq!(mix_date("Warehouse Set", 2018), Some((2018, 1, 1)));
        assert_eq!(mix_date("Warehouse Set", 0), None);
    }

    #[test]
    fn disc_folders() {
        for (name, n) in [
            ("CD1", 1),
            ("cd 2", 2),
            ("Disc 3", 3),
            ("Disk_04", 4),
            ("CD1 Bonus", 0),
            ("Discography", 0),
        ] {
            assert_eq!(disc_folder_number(name), n, "{name}");
        }
    }

    fn row(
        path: &str,
        artist: &str,
        album: Option<&str>,
        album_artist: Option<&str>,
        disc: i32,
        track: i32,
    ) -> TrackRow {
        TrackRow {
            stored_path: path.into(),
            title: Some(format!("t{track}")),
            artist: Some(artist.into()),
            album: album.map(Into::into),
            album_artist: album_artist.map(Into::into),
            disc_no: disc,
            track_no: track,
            ..TrackRow::default()
        }
    }

    #[test]
    fn a_compilation_without_album_artist_stays_together_as_various_artists() {
        let rows = [
            row(r"D:\Music\Comp\01.flac", "Ana", Some("Comp"), None, 0, 1),
            row(r"D:\Music\Comp\02.flac", "Ben", Some("Comp"), None, 0, 2),
        ];
        let lib = load(&rows, Kind::Music, &[], r"C:\Melora");
        assert_eq!(lib.albums.len(), 1);
        assert_eq!(lib.albums[0].artist, VARIOUS_ARTISTS);
    }

    #[test]
    fn disc_folders_join_their_album_and_get_headings() {
        let rows = [
            row(r"D:\Music\Big\CD2\01.flac", "Ana", Some("Big"), None, 0, 1),
            row(r"D:\Music\Big\CD1\01.flac", "Ana", Some("Big"), None, 0, 1),
            row(r"D:\Music\Big\CD1\02.flac", "Ana", Some("Big"), None, 0, 2),
        ];
        let lib = load(&rows, Kind::Music, &[], r"C:\Melora");
        assert_eq!(lib.albums.len(), 1);
        let order: Vec<(&str, &str)> = lib
            .tracks
            .iter()
            .map(|t| (t.disc.as_str(), t.title.as_str()))
            .collect();
        assert_eq!(
            order,
            [("Disc 1", "t1"), ("Disc 1", "t2"), ("Disc 2", "t1")]
        );
    }

    #[test]
    fn untagged_files_are_grouped_by_folder_and_named_after_themselves() {
        let rows = [TrackRow {
            stored_path: r"D:\Music\Demos\take one.mp3".into(),
            ..TrackRow::default()
        }];
        let lib = load(&rows, Kind::Music, &[], r"C:\Melora");
        assert_eq!(lib.albums[0].title, "Demos");
        assert_eq!(lib.albums[0].artist, UNKNOWN_ARTIST);
        assert_eq!(lib.tracks[0].title, "take one");
    }

    #[test]
    fn mixes_sort_by_dj_then_date_with_undated_last() {
        let mut rows = Vec::new();
        for (path, title) in [
            (r"D:\Mixes\Zed\a.mp3", "Undated"),
            (r"D:\Mixes\Zed\b.mp3", "Late (2021-01-01)"),
            (r"D:\Mixes\Zed\c.mp3", "Early (2019-05-05)"),
            (r"D:\Mixes\Amy\d.mp3", "Amy set"),
        ] {
            rows.push(TrackRow {
                stored_path: path.into(),
                title: Some(title.into()),
                ..TrackRow::default()
            });
        }
        let lib = load(&rows, Kind::Mixes, &[r"D:\Mixes".into()], r"C:\Melora");
        let order: Vec<&str> = lib.tracks.iter().map(|t| t.title.as_str()).collect();
        assert_eq!(
            order,
            [
                "Amy set",
                "Early (2019-05-05)",
                "Late (2021-01-01)",
                "Undated"
            ]
        );
    }

    #[test]
    fn scanning_finds_files_and_drops_deleted_ones() {
        let dir = std::env::temp_dir().join(format!("melora-rs-scan-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("dir");
        let (a, b) = (dir.join("a.flac"), dir.join("b.mp3"));
        std::fs::write(&a, b"not really audio").expect("a");
        std::fs::write(&b, b"").expect("b");
        std::fs::write(dir.join("cover.jpg"), b"").expect("jpg");

        let mut db = Db::open(&dir.join("test.db"), r"C:\Melora").expect("db");
        db.add_folder(Kind::Music, &dir.to_string_lossy())
            .expect("folder");
        assert!(scan(&mut db, Kind::Music, &[], r"C:\Melora").expect("scan"));
        assert_eq!(db.tracks(Kind::Music).expect("tracks").len(), 2); // unreadable tags still count
        assert!(!scan(&mut db, Kind::Music, &[], r"C:\Melora").expect("scan")); // nothing changed

        std::fs::remove_file(&b).expect("rm");
        assert!(scan(&mut db, Kind::Music, &[], r"C:\Melora").expect("scan"));
        assert_eq!(db.tracks(Kind::Music).expect("tracks").len(), 1);
        drop(db);
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// Reads a real file's tags: `MELORA_TAG_FILE=<path> cargo test -- --ignored real_tags --nocapture`.
    #[test]
    #[ignore = "needs a real music file"]
    fn real_tags() {
        let Ok(path) = std::env::var("MELORA_TAG_FILE") else {
            return;
        };
        let row = read_tags(Path::new(&path), path.clone(), 0);
        println!("{row:?}");
        assert!(row.title.is_some() && row.duration_ms > 0);
    }

    #[test]
    fn a_missing_folder_keeps_its_tracks() {
        let dir = std::env::temp_dir().join(format!("melora-rs-missing-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("dir");
        let mut db = Db::open(&dir.join("test.db"), r"C:\Melora").expect("db");
        db.add_folder(Kind::Music, r"Q:\Unplugged").expect("folder");
        db.save_scan(
            Kind::Music,
            &[TrackRow {
                stored_path: r"Q:\Unplugged\a.flac".into(),
                ..TrackRow::default()
            }],
            &[],
        )
        .expect("save");
        assert!(!scan(&mut db, Kind::Music, &[], r"C:\Melora").expect("scan"));
        assert_eq!(db.tracks(Kind::Music).expect("tracks").len(), 1);
        drop(db);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
