//! Finding pictures online, as 2.x's CoverLookup does: album covers from the Cover Art Archive
//! (for files tagged by Picard), then iTunes, then Deezer; DJ photos from Deezer; station logos
//! from Radio Browser. Each answer is a web address (Discord can only show pictures from the web)
//! and is kept in the `covers` table under 2.x's keys: a find for good, a miss for a week, and
//! being offline isn't a miss.

use serde_json::Value;

use crate::db::Db;

const RETRY_MISSES_AFTER: i64 = 7 * 24 * 3600;
const ITUNES_SIZE: u32 = 600;
const USER_AGENT: &str = concat!("Melora/", env!("CARGO_PKG_VERSION"));

/// What to find a picture for.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Query {
    /// An album. A file from it is given so its MusicBrainz release ID (if tagged) finds the exact cover.
    Album {
        artist: String,
        album: String,
        sample: Option<String>,
    },
    /// One song (radio).
    Song { artist: String, title: String },
    /// The artist themself: a DJ, for mixes.
    Artist(String),
    /// A radio station's logo, by its address, with its name to fall back on.
    Station { url: String, name: String },
}

/// 2.x's cache key for a query.
pub fn key(q: &Query) -> String {
    match q {
        Query::Station { url, .. } => format!("station|{url}"),
        Query::Artist(a) => format!("artist|{}", normalize(a, false)),
        Query::Album { artist, album, .. } => format!(
            "album|{}|{}",
            normalize(artist, false),
            normalize(album, false)
        ),
        Query::Song { artist, title } => format!(
            "song|{}|{}",
            normalize(artist, false),
            normalize(title, false)
        ),
    }
}

/// The letter without its accent, for the accented letters that turn up in names.
fn plain(c: char) -> char {
    const FROM: &str = "àáâãäåāăąçćčďèéêëēėęěìíîïīįıñńňòóôõöøōőùúûüūůűųýÿżźžśšşřŕľĺłġğ";
    const TO: &str = "aaaaaaaaacccdeeeeeeeeiiiiiiinnnoooooooouuuuuuuuyyzzzsssrrlllgg";
    FROM.chars()
        .position(|f| f == c)
        .and_then(|i| TO.chars().nth(i))
        .unwrap_or(c)
}

/// A name reduced for matching, as 2.x does: no accents, punctuation or (unless kept) bracketed
/// parts, "&" as "and", lower case, without a leading "the", and no spaces.
pub fn normalize(name: &str, keep_brackets: bool) -> String {
    let mut text = String::new();
    let mut depth = 0;
    for c in name.chars() {
        match c {
            '(' | '[' if !keep_brackets => depth += 1,
            ')' | ']' if !keep_brackets && depth > 0 => depth -= 1,
            _ if depth == 0 => text.push(c),
            _ => {}
        }
    }
    let words: Vec<String> = text
        .replace('&', " and ")
        .split(|c: char| !c.is_alphanumeric())
        .filter(|w| !w.is_empty())
        .map(|w| w.to_lowercase().chars().map(plain).collect())
        .collect();
    let words = if words.first().is_some_and(|w| w == "the") {
        &words[1..]
    } else {
        &words[..]
    };
    words.concat()
}

/// Loose comparison: as `normalize`, and one name may start with the other ("Album" and "Album
/// Deluxe Edition") if the shorter has at least 4 letters.
pub fn same_name(a: &str, b: &str) -> bool {
    let (x, y) = (normalize(a, false), normalize(b, false));
    if x.is_empty() || y.is_empty() {
        return false;
    }
    if x == y {
        return true;
    }
    let (short, long) = if x.len() < y.len() {
        (&x, &y)
    } else {
        (&y, &x)
    };
    short.chars().count() >= 4 && long.starts_with(short.as_str())
}

/// The picture of the best search result: one whose name matches exactly, brackets included (so a
/// remix gets the remix's cover), or else the first that matches loosely.
pub fn pick(artist: &str, name: &str, results: &[(String, String, String)]) -> Option<String> {
    let usable: Vec<&(String, String, String)> = results
        .iter()
        .filter(|(a, n, url)| !n.is_empty() && !url.is_empty() && same_name(artist, a))
        .collect();
    usable
        .iter()
        .find(|(_, n, _)| normalize(n, true) == normalize(name, true))
        .or_else(|| usable.iter().find(|(_, n, _)| same_name(name, n)))
        .map(|(_, _, url)| url.clone())
}

pub fn encode(s: &str) -> String {
    s.bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (b as char).to_string()
            }
            _ => format!("%{b:02X}"),
        })
        .collect()
}

pub fn agent() -> ureq::Agent {
    ureq::Agent::config_builder()
        .timeout_global(Some(std::time::Duration::from_secs(10)))
        .user_agent(USER_AGENT)
        .build()
        .into()
}

/// JSON from a web service; Err when it couldn't be reached or answered with an error.
fn get_json(agent: &ureq::Agent, url: &str) -> Result<Value, ()> {
    let mut response = agent.get(url).call().map_err(|_| ())?;
    let text = response.body_mut().read_to_string().map_err(|_| ())?;
    let json: Value = serde_json::from_str(&text).map_err(|_| ())?;
    // Deezer reports quota and other errors inside a normal-looking answer.
    if json.get("error").is_some() {
        Err(())
    } else {
        Ok(json)
    }
}

fn str_at<'a>(v: &'a Value, path: &[&str]) -> &'a str {
    path.iter().fold(v, |v, k| &v[*k]).as_str().unwrap_or("")
}

fn cover_art_archive(agent: &ureq::Agent, sample: &str) -> Result<Option<String>, ()> {
    let Some(mbid) = crate::library::release_mbid(std::path::Path::new(sample)) else {
        return Ok(None);
    };
    if mbid.len() != 36 || !mbid.chars().all(|c| c.is_ascii_hexdigit() || c == '-') {
        return Ok(None);
    }
    // The archive redirects to whichever archive.org server holds the image today, so keep its own
    // address, which stays put, and let Discord follow the redirect.
    let url = format!("https://coverartarchive.org/release/{mbid}/front-500");
    match agent.head(&url).call() {
        Ok(r) if r.status().is_success() => Ok(Some(url)),
        Ok(_) | Err(ureq::Error::StatusCode(_)) => Ok(None),
        Err(_) => Err(()),
    }
}

fn itunes(
    agent: &ureq::Agent,
    artist: &str,
    name: &str,
    album: bool,
) -> Result<Option<String>, ()> {
    let entity = if album { "album" } else { "song" };
    let json = get_json(
        agent,
        &format!(
            "https://itunes.apple.com/search?term={}&entity={entity}&limit=15",
            encode(&format!("{artist} {name}"))
        ),
    )?;
    let results: Vec<(String, String, String)> = json["results"]
        .as_array()
        .into_iter()
        .flatten()
        .map(|r| {
            let n = if album { "collectionName" } else { "trackName" };
            (
                str_at(r, &["artistName"]).into(),
                str_at(r, &[n]).into(),
                str_at(r, &["artworkUrl100"]).into(),
            )
        })
        .collect();
    Ok(pick(artist, name, &results)
        .map(|u| u.replace("100x100bb", &format!("{ITUNES_SIZE}x{ITUNES_SIZE}bb"))))
}

fn deezer(
    agent: &ureq::Agent,
    artist: &str,
    name: &str,
    album: bool,
) -> Result<Option<String>, ()> {
    let (kind, q) = if album {
        ("album", format!("artist:\"{artist}\" album:\"{name}\""))
    } else {
        ("track", format!("artist:\"{artist}\" track:\"{name}\""))
    };
    let json = get_json(
        agent,
        &format!(
            "https://api.deezer.com/search/{kind}?q={}&limit=15",
            encode(&q)
        ),
    )?;
    let results: Vec<(String, String, String)> = json["data"]
        .as_array()
        .into_iter()
        .flatten()
        .map(|r| {
            let cover = if album {
                str_at(r, &["cover_xl"])
            } else {
                str_at(r, &["album", "cover_xl"])
            };
            (
                str_at(r, &["artist", "name"]).into(),
                str_at(r, &["title"]).into(),
                cover.into(),
            )
        })
        .collect();
    Ok(pick(artist, name, &results))
}

fn deezer_artist(agent: &ureq::Agent, artist: &str) -> Result<Option<String>, ()> {
    let json = get_json(
        agent,
        &format!(
            "https://api.deezer.com/search/artist?q={}&limit=10",
            encode(artist)
        ),
    )?;
    Ok(json["data"].as_array().into_iter().flatten().find_map(|r| {
        // Only the same name exactly: a prefix match could put up a stranger's photo. Artists
        // without a photo get a placeholder whose address has an empty image ID.
        let url = str_at(r, &["picture_xl"]);
        (normalize(str_at(r, &["name"]), false) == normalize(artist, false)
            && !url.is_empty()
            && !url.contains("/artist//"))
        .then(|| url.to_string())
    }))
}

fn radio_browser(agent: &ureq::Agent, url: &str, name: &str) -> Result<Option<String>, ()> {
    const API: &str = "https://all.api.radio-browser.info/json/stations/";
    let favicon = |s: &Value| Some(str_at(s, &["favicon"]).to_string()).filter(|f| !f.is_empty());
    let by_url = get_json(agent, &format!("{API}byurl?url={}", encode(url)))?;
    if let Some(found) = by_url.as_array().into_iter().flatten().find_map(favicon) {
        return Ok(Some(found));
    }
    let wanted = normalize(name, false);
    if wanted.is_empty() {
        return Ok(None);
    }
    let by_name = get_json(
        agent,
        &format!(
            "{API}search?name={}&order=votes&reverse=true&hidebroken=true&limit=20",
            encode(name)
        ),
    )?;
    Ok(by_name
        .as_array()
        .into_iter()
        .flatten()
        .filter(|s| normalize(str_at(s, &["name"]), false).contains(&wanted))
        .find_map(favicon))
}

/// The picture's web address, from the cache or online (updating the cache); None for none.
pub fn find(db: &Db, agent: &ureq::Agent, q: &Query, now: i64) -> Option<String> {
    let key = key(q);
    match db.cover(&key) {
        Some((Some(found), _)) => return Some(found),
        Some((None, at)) if now - at < RETRY_MISSES_AFTER => return None,
        _ => {}
    }
    let mut unreachable = false;
    let mut found = None;
    let mut try_source = |r: Result<Option<String>, ()>| match r {
        Ok(Some(u)) => {
            found = Some(u);
            true
        }
        Ok(None) => false,
        Err(()) => {
            unreachable = true;
            false
        }
    };
    match q {
        Query::Station { url, name } => {
            try_source(radio_browser(agent, url, name));
        }
        Query::Artist(a) => {
            try_source(deezer_artist(agent, a));
        }
        Query::Album {
            artist,
            album,
            sample,
        } => {
            let _ = sample
                .as_deref()
                .is_some_and(|s| try_source(cover_art_archive(agent, s)))
                || try_source(itunes(agent, artist, album, true))
                || try_source(deezer(agent, artist, album, true));
        }
        Query::Song { artist, title } => {
            let _ = try_source(itunes(agent, artist, title, false))
                || try_source(deezer(agent, artist, title, false));
        }
    }
    // A miss only counts if every source actually answered; being offline isn't "no cover".
    if found.is_some() || !unreachable {
        let _ = db.set_cover(&key, found.as_deref(), now);
    }
    found
}

/// SHA-1 of some bytes, in hex: 2.x names saved pictures after it.
pub fn sha1_hex(data: &[u8]) -> String {
    let mut h: [u32; 5] = [
        0x6745_2301,
        0xEFCD_AB89,
        0x98BA_DCFE,
        0x1032_5476,
        0xC3D2_E1F0,
    ];
    let mut msg = data.to_vec();
    msg.push(0x80);
    while msg.len() % 64 != 56 {
        msg.push(0);
    }
    msg.extend((data.len() as u64 * 8).to_be_bytes());
    for block in msg.chunks(64) {
        let mut w = [0u32; 80];
        for (i, word) in block.chunks(4).enumerate() {
            w[i] = u32::from_be_bytes([word[0], word[1], word[2], word[3]]);
        }
        for i in 16..80 {
            w[i] = (w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16]).rotate_left(1);
        }
        let [mut a, mut b, mut c, mut d, mut e] = h;
        for (i, wi) in w.iter().enumerate() {
            let (f, k) = match i {
                0..=19 => ((b & c) | (!b & d), 0x5A82_7999),
                20..=39 => (b ^ c ^ d, 0x6ED9_EBA1),
                40..=59 => ((b & c) | (b & d) | (c & d), 0x8F1B_BCDC),
                _ => (b ^ c ^ d, 0xCA62_C1D6),
            };
            let t = a
                .rotate_left(5)
                .wrapping_add(f)
                .wrapping_add(e)
                .wrapping_add(k)
                .wrapping_add(*wi);
            (e, d, c, b, a) = (d, c, b.rotate_left(30), a, t);
        }
        for (x, y) in h.iter_mut().zip([a, b, c, d, e]) {
            *x = x.wrapping_add(y);
        }
    }
    h.iter().map(|x| format!("{x:08x}")).collect()
}

/// Where a picture found online for this query is saved: `data\covers\<first 16 of the key's
/// SHA-1>.jpg`, as 2.x saves them, so pictures 2.x found are used as they are.
pub fn saved_file(q: &Query) -> std::path::PathBuf {
    crate::paths::data_dir()
        .join("covers")
        .join(format!("{}.jpg", &sha1_hex(key(q).as_bytes())[..16]))
}

/// A picture from the web (at most 5 MB); None if it can't be fetched.
pub fn download(agent: &ureq::Agent, url: &str) -> Option<Vec<u8>> {
    let mut response = agent.get(url).call().ok()?;
    response
        .body_mut()
        .with_config()
        .limit(5 * 1024 * 1024)
        .read_to_vec()
        .ok()
}

pub fn now() -> i64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_secs() as i64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_normalized_like_2x() {
        assert_eq!(
            normalize("The Groove Salad (SomaFM) [128k]", false),
            "groovesalad"
        );
        assert_eq!(normalize("Drum & Bass FM", false), "drumandbassfm");
        assert_eq!(normalize("Café del Mar", false), "cafedelmar");
        assert_eq!(normalize("Genesis (Remix)", true), "genesisremix");
    }

    #[test]
    fn same_name_allows_a_longer_edition() {
        assert!(same_name("White Pony", "White Pony (Deluxe Edition)"));
        assert!(same_name("Ohms", "Ohms Deluxe"));
        assert!(!same_name("Ohm", "Ohms Deluxe")); // too short to trust a prefix
        assert!(!same_name("", "Anything"));
    }

    #[test]
    fn an_exact_title_wins_so_remixes_get_their_own_cover() {
        let r = |n: &str, u: &str| ("Deftones".to_string(), n.to_string(), u.to_string());
        let results = [
            r("Genesis", "plain"),
            r("Genesis (Christopher Lobsinger Remix)", "remix"),
        ];
        assert_eq!(
            pick(
                "Deftones",
                "Genesis (Christopher Lobsinger Remix)",
                &results
            )
            .as_deref(),
            Some("remix")
        );
        assert_eq!(
            pick("Deftones", "Genesis (Some Other Remix)", &results).as_deref(),
            Some("plain")
        );
        assert_eq!(pick("Someone Else", "Genesis", &results), None);
    }

    #[test]
    fn sha1_matches_the_standard() {
        assert_eq!(sha1_hex(b""), "da39a3ee5e6b4b0d3255bfef95601890afd80709");
        assert_eq!(sha1_hex(b"abc"), "a9993e364706816aba3e25717850c26c9cd0d89d");
        let long = b"abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
        assert_eq!(sha1_hex(long), "84983e441c3bd26ebaae4aa1f95129e5e54670f1");
    }

    #[test]
    fn keys_match_2x() {
        assert_eq!(
            key(&Query::Station {
                url: "http://x".into(),
                name: "X".into()
            }),
            "station|http://x"
        );
        assert_eq!(key(&Query::Artist("The Prodigy".into())), "artist|prodigy");
        assert_eq!(
            key(&Query::Album {
                artist: "Deftones".into(),
                album: "White Pony".into(),
                sample: None
            }),
            "album|deftones|whitepony"
        );
    }

    #[test]
    fn the_cache_decides_before_any_lookup() {
        let dir = std::env::temp_dir().join(format!("melora-rs-online-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("dir");
        let db = Db::open(&dir.join("t.db"), r"C:\Melora").expect("db");
        let found = Query::Station {
            url: "http://found".into(),
            name: "x".into(),
        };
        let missed = Query::Station {
            url: "http://missed".into(),
            name: "x".into(),
        };
        db.set_cover(&key(&found), Some("http://logo.png"), 0)
            .expect("set");
        db.set_cover(&key(&missed), None, 1_000).expect("set");
        let agent = agent();
        assert_eq!(
            find(&db, &agent, &found, 99_999_999).as_deref(),
            Some("http://logo.png")
        ); // kept for good
        assert_eq!(find(&db, &agent, &missed, 1_000 + 3600), None); // a recent miss isn't retried
        drop(db);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
