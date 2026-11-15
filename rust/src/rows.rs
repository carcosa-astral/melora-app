//! The library as list rows: a heading for each album (or DJ), a "Disc N" heading where an album
//! has several discs, and its tracks. Kept apart from the UI so it can be tested without a window.

use crate::library::Library;

/// Row heights in pixels; the Slint list draws each kind at exactly this height.
pub const ALBUM_HEIGHT: f32 = 68.0;
pub const DISC_HEIGHT: f32 = 30.0;
pub const TRACK_HEIGHT: f32 = 32.0;
/// The album cover, drawn left of the album's title and tracks (as in 2.x), and the space an
/// album takes at least so a short one leaves room for its cover: margin, cover, margin.
pub const COVER_SIZE: f32 = 168.0;
pub const MIN_ALBUM_BLOCK: f32 = 20.0 + COVER_SIZE + 8.0;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RowKind {
    Album,
    Disc,
    Track,
}

#[derive(Clone, Debug, PartialEq)]
pub struct ListRow {
    pub kind: RowKind,
    pub title: String,
    /// Album: "Artist · Year". Track: its artist when it differs from the album's.
    pub subtitle: String,
    /// Track number ("01"), or empty.
    pub number: String,
    pub length: String,
    /// For track rows: the index into `Library::tracks`.
    pub track: Option<usize>,
    /// For album rows: the index into `Library::albums`.
    pub album: Option<usize>,
    /// Top of the row within the list, in pixels.
    pub y: f32,
    pub height: f32,
}

/// 3:07, or 1:02:45 past an hour.
pub fn clock(seconds: f64) -> String {
    let t = seconds.max(0.0) as u64;
    let (h, m, s) = (t / 3600, t / 60 % 60, t % 60);
    if h > 0 {
        format!("{h}:{m:02}:{s:02}")
    } else {
        format!("{m}:{s:02}")
    }
}

/// "1,234 tracks  ·  3d 4h" (or "2h 5m", "45m"); empty for an empty library.
pub fn summary(lib: &Library) -> String {
    let n = lib.tracks.len();
    if n == 0 {
        return String::new();
    }
    let digits = n.to_string();
    let mut count = String::new();
    for (i, c) in digits.chars().enumerate() {
        if i > 0 && (digits.len() - i).is_multiple_of(3) {
            count.push(',');
        }
        count.push(c);
    }
    let noun = if n == 1 { "track" } else { "tracks" };
    let mins = lib.tracks.iter().map(|t| t.duration_ms).sum::<i64>() / 60_000;
    let (d, h, m) = (mins / 1440, mins / 60 % 24, mins % 60);
    let total = if d > 0 {
        format!("{d}d {h}h")
    } else if mins >= 60 {
        format!("{}h {m}m", mins / 60)
    } else {
        format!("{m}m")
    };
    format!("{count} {noun}  ·  {total}")
}

pub fn build(lib: &Library) -> Vec<ListRow> {
    let mut rows = Vec::new();
    let mut y = 0.0;
    let mut album_top = f32::MIN;
    let mut push = |rows: &mut Vec<ListRow>, mut row: ListRow| {
        row.height = match row.kind {
            RowKind::Album => ALBUM_HEIGHT,
            RowKind::Disc => DISC_HEIGHT,
            RowKind::Track => TRACK_HEIGHT,
        };
        if row.kind == RowKind::Album {
            // A short album above still needs room for its cover.
            y = f32::max(y, album_top + MIN_ALBUM_BLOCK);
            album_top = y;
        }
        row.y = y;
        y += row.height;
        rows.push(row);
    };
    let (mut album, mut disc) = (None, String::new());
    for (i, t) in lib.tracks.iter().enumerate() {
        let a = &lib.albums[t.album];
        if album != Some(t.album) {
            album = Some(t.album);
            disc.clear();
            let subtitle = if a.year > 0 {
                format!("{}  ·  {}", a.artist, a.year)
            } else {
                a.artist.clone()
            };
            push(
                &mut rows,
                ListRow {
                    kind: RowKind::Album,
                    title: a.title.clone(),
                    subtitle,
                    album: Some(t.album),
                    ..blank()
                },
            );
        }
        if !t.disc.is_empty() && t.disc != disc {
            disc = t.disc.clone();
            push(
                &mut rows,
                ListRow {
                    kind: RowKind::Disc,
                    title: disc.clone(),
                    ..blank()
                },
            );
        }
        push(
            &mut rows,
            ListRow {
                kind: RowKind::Track,
                title: t.title.clone(),
                subtitle: if t.artist.eq_ignore_ascii_case(&a.artist) {
                    String::new()
                } else {
                    t.artist.clone()
                },
                number: if t.track_no > 0 {
                    format!("{:02}", t.track_no)
                } else {
                    String::new()
                },
                length: if t.duration_ms > 0 {
                    clock(t.duration_ms as f64 / 1000.0)
                } else {
                    String::new()
                },
                track: Some(i),
                ..blank()
            },
        );
    }
    rows
}

fn blank() -> ListRow {
    ListRow {
        kind: RowKind::Track,
        title: String::new(),
        subtitle: String::new(),
        number: String::new(),
        length: String::new(),
        track: None,
        album: None,
        y: 0.0,
        height: 0.0,
    }
}

/// The rows that show in a view `height` tall scrolled `top` down the list, plus a few either
/// side so fast scrolling doesn't show gaps.
pub fn in_view(rows: &[ListRow], top: f32, height: f32) -> std::ops::Range<usize> {
    const SPARE: usize = 4;
    let mut start = rows
        .partition_point(|r| r.y + r.height <= top)
        .saturating_sub(SPARE);
    // The album heading draws the cover, which reaches below it: keep the heading while its
    // cover can still be seen.
    if let Some(a) = rows[..start].iter().rposition(|r| r.kind == RowKind::Album)
        && rows[a].y + MIN_ALBUM_BLOCK > top
        && rows[a + 1..start].iter().all(|r| r.kind != RowKind::Album)
    {
        start = a;
    }
    let end = (rows.partition_point(|r| r.y < top + height) + SPARE).min(rows.len());
    start..end.max(start)
}

/// The height of the whole list, including room for the last album's cover.
pub fn total_height(rows: &[ListRow]) -> f32 {
    let end = rows.last().map_or(0.0, |r| r.y + r.height);
    let last_album = rows
        .iter()
        .rev()
        .find(|r| r.kind == RowKind::Album)
        .map_or(0.0, |r| r.y + MIN_ALBUM_BLOCK);
    end.max(last_album)
}

/// How far to scroll so row `r` is fully in view, moving as little as possible. Scrolling up to
/// an album's first track brings its headings into view as well.
pub fn scroll_to(rows: &[ListRow], r: usize, top: f32, height: f32) -> f32 {
    let Some(row) = rows.get(r) else { return top };
    if row.y < top {
        let mut first = r;
        while first > 0 && rows[first - 1].track.is_none() {
            first -= 1;
        }
        rows[first].y
    } else if row.y + row.height > top + height {
        row.y + row.height - height
    } else {
        top
    }
}

/// The track row `steps` track rows away from `from` (negative: up), stopping at the first and
/// last tracks. Headings are skipped. None when the list has no tracks.
pub fn step(rows: &[ListRow], from: Option<usize>, steps: i32) -> Option<usize> {
    let tracks: Vec<usize> = (0..rows.len())
        .filter(|&i| rows[i].track.is_some())
        .collect();
    let last = tracks.len().checked_sub(1)?;
    // Where we are, counted in tracks. From a heading, the track below it is one step down and
    // the track above it one step up.
    let here = match from.map(|f| (f, tracks.iter().position(|&i| i >= f))) {
        None => -1,
        Some((_, None)) => tracks.len() as i32,
        Some((f, Some(p))) if tracks[p] == f || steps < 0 => p as i32,
        Some((_, Some(p))) => p as i32 - 1,
    };
    let to = (here + steps).clamp(0, last as i32) as usize;
    Some(tracks[to])
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::db::{Kind, TrackRow};
    use crate::library::load;

    fn lib() -> Library {
        let row =
            |path: &str, title: &str, album: &str, disc: i32, track: i32, artist: &str| TrackRow {
                stored_path: path.into(),
                title: Some(title.into()),
                artist: Some(artist.into()),
                album: Some(album.into()),
                album_artist: Some("Ana".into()),
                year: 2001,
                disc_no: disc,
                track_no: track,
                duration_ms: 187_000,
                ..TrackRow::default()
            };
        load(
            &[
                row(r"D:\M\A\1.flac", "One", "Alpha", 1, 1, "Ana"),
                row(r"D:\M\A\2.flac", "Two", "Alpha", 2, 1, "Ana feat. Ben"),
                row(r"D:\M\B\1.flac", "Solo", "Beta", 0, 3, "Ana"),
            ],
            Kind::Music,
            &[],
            r"C:\Melora",
        )
    }

    #[test]
    fn headings_come_before_each_album_and_disc() {
        let rows = build(&lib());
        let shape: Vec<(RowKind, &str)> = rows.iter().map(|r| (r.kind, r.title.as_str())).collect();
        assert_eq!(
            shape,
            [
                (RowKind::Album, "Alpha"),
                (RowKind::Disc, "Disc 1"),
                (RowKind::Track, "One"),
                (RowKind::Disc, "Disc 2"),
                (RowKind::Track, "Two"),
                (RowKind::Album, "Beta"),
                (RowKind::Track, "Solo"),
            ]
        );
        assert_eq!(rows[0].subtitle, "Ana  ·  2001");
        assert_eq!(rows[2].subtitle, ""); // same artist as the album
        assert_eq!(rows[4].subtitle, "Ana feat. Ben");
        assert_eq!(
            (rows[6].number.as_str(), rows[6].length.as_str()),
            ("03", "3:07")
        );
        assert_eq!(rows[2].y, ALBUM_HEIGHT + DISC_HEIGHT);
    }

    #[test]
    fn stepping_skips_headings_and_stops_at_the_ends() {
        let rows = build(&lib()); // tracks at rows 2, 4 and 6
        assert_eq!(step(&rows, None, 1), Some(2));
        assert_eq!(step(&rows, Some(2), 1), Some(4));
        assert_eq!(step(&rows, Some(4), 1), Some(6));
        assert_eq!(step(&rows, Some(6), 1), Some(6));
        assert_eq!(step(&rows, Some(6), -1), Some(4));
        assert_eq!(step(&rows, Some(2), -5), Some(2));
        assert_eq!(step(&rows, Some(2), 10), Some(6));
        assert_eq!(step(&rows, Some(5), 1), Some(6)); // from a heading
        assert_eq!(step(&[], None, 1), None);
    }

    #[test]
    fn only_rows_in_view_and_a_few_spare() {
        let mut l = lib();
        let t = l.tracks[2].clone();
        l.tracks = vec![t; 100]; // one album heading, then 100 tracks
        let rows = build(&l);
        assert_eq!(in_view(&rows, 0.0, 200.0), 0..10); // the heading and 5 tracks start above 200 px, then 4 spare
        let r = in_view(&rows, 150.0, 100.0);
        assert_eq!(r.start, 0); // the heading stays while its cover shows
        let r = in_view(&rows, 1000.0, 100.0);
        assert!(rows[r.start + 4].y <= 1000.0 && rows[r.end - 5].y < 1100.0);
        assert_eq!(in_view(&rows, 99_999.0, 100.0), 97..101);
        assert_eq!(in_view(&[], 0.0, 100.0), 0..0);
    }

    #[test]
    fn scrolling_to_a_row_moves_as_little_as_possible() {
        let rows = build(&lib()); // Alpha 0-68, Disc 1 68-98, One 98-130, Disc 2 130-160, Two 160-192, Beta 196-264, Solo 264-296
        assert_eq!(rows[5].y, MIN_ALBUM_BLOCK); // Alpha is short, so Beta waits for its cover
        assert_eq!(total_height(&rows), 196.0 + MIN_ALBUM_BLOCK);
        assert_eq!(scroll_to(&rows, 2, 0.0, 200.0), 0.0); // already in view
        assert_eq!(scroll_to(&rows, 6, 0.0, 200.0), 96.0); // bottom edge lines up
        assert_eq!(scroll_to(&rows, 2, 150.0, 200.0), 0.0); // up to an album's first track: its headings too
        assert_eq!(scroll_to(&rows, 4, 180.0, 200.0), 130.0); // and to a disc's first track: its disc heading
    }

    #[test]
    fn summary_counts_and_totals() {
        let mut l = lib();
        assert_eq!(summary(&l), "3 tracks  ·  9m");
        l.tracks.truncate(1);
        assert_eq!(summary(&l), "1 track  ·  3m");
        let t = l.tracks[0].clone();
        l.tracks = vec![t; 1234];
        assert_eq!(summary(&l), "1,234 tracks  ·  2d 16h");
        assert_eq!(summary(&Library::default()), "");
    }

    #[test]
    fn clock_formats() {
        assert_eq!(clock(0.0), "0:00");
        assert_eq!(clock(187.9), "3:07");
        assert_eq!(clock(3765.0), "1:02:45");
    }
}
