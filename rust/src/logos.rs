//! Station logos from Radio Browser (the community station directory), found as 2.x finds them:
//! by the station's address, else by its name (the most-voted match). Results are cached in the
//! `covers` table under 2.x's keys, so a logo found by either version is found for both: a logo
//! is kept for good, a miss is tried again after 7 days, and being offline isn't a miss.

use std::path::PathBuf;
use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex};

use serde_json::Value;
use slint::{Rgba8Pixel, SharedPixelBuffer};

use crate::covers::thumbnail;
use crate::db::Db;

const API: &str = "https://all.api.radio-browser.info/json/stations/";
const RETRY_MISSES_AFTER: i64 = 7 * 24 * 3600;
const USER_AGENT: &str = concat!("Melora/", env!("CARGO_PKG_VERSION"));

/// 2.x's cache key for a station's logo.
pub fn key(url: &str) -> String {
    format!("station|{url}")
}

/// A name reduced for matching, as 2.x does: no bracketed parts, "&" as "and", letters and
/// digits only, lower case, without a leading "the".
pub fn normalize(name: &str) -> String {
    let mut text = String::new();
    let mut depth = 0;
    for c in name.chars() {
        match c {
            '(' | '[' => depth += 1,
            ')' | ']' if depth > 0 => depth -= 1,
            _ if depth == 0 => text.push(c),
            _ => {}
        }
    }
    let words: Vec<String> = text
        .replace('&', " and ")
        .split(|c: char| !c.is_alphanumeric())
        .filter(|w| !w.is_empty())
        .map(str::to_lowercase)
        .collect();
    let words = if words.first().is_some_and(|w| w == "the") {
        &words[1..]
    } else {
        &words[..]
    };
    words.concat()
}

/// Percent-encodes a value for a query string.
fn encode(s: &str) -> String {
    s.bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (b as char).to_string()
            }
            _ => format!("%{b:02X}"),
        })
        .collect()
}

fn get_json(url: &str) -> Result<Value, ()> {
    let mut response = ureq::get(url)
        .header("User-Agent", USER_AGENT)
        .call()
        .map_err(|_| ())?;
    let text = response.body_mut().read_to_string().map_err(|_| ())?;
    serde_json::from_str(&text).map_err(|_| ())
}

fn favicon(station: &Value) -> Option<String> {
    station["favicon"]
        .as_str()
        .filter(|s| !s.is_empty())
        .map(str::to_string)
}

/// The station's logo address from Radio Browser. Err when Radio Browser couldn't be reached.
fn lookup(url: &str, name: &str) -> Result<Option<String>, ()> {
    let by_url = get_json(&format!("{API}byurl?url={}", encode(url)))?;
    if let Some(found) = by_url.as_array().into_iter().flatten().find_map(favicon) {
        return Ok(Some(found));
    }
    let wanted = normalize(name);
    if wanted.is_empty() {
        return Ok(None);
    }
    let by_name = get_json(&format!(
        "{API}search?name={}&order=votes&reverse=true&hidebroken=true&limit=20",
        encode(name)
    ))?;
    Ok(by_name
        .as_array()
        .into_iter()
        .flatten()
        .filter(|s| normalize(s["name"].as_str().unwrap_or("")).contains(&wanted))
        .find_map(favicon))
}

/// The logo address to use, from the cache or Radio Browser, updating the cache.
fn logo_url(db: &Db, url: &str, name: &str, now: i64) -> Option<String> {
    match db.cover(&key(url)) {
        Some((Some(found), _)) => return Some(found),
        Some((None, at)) if now - at < RETRY_MISSES_AFTER => return None,
        _ => {}
    }
    let found = lookup(url, name).ok()?; // offline: try again next time
    let _ = db.set_cover(&key(url), found.as_deref(), now);
    found
}

fn download(url: &str) -> Option<Vec<u8>> {
    let mut response = ureq::get(url)
        .header("User-Agent", USER_AGENT)
        .call()
        .ok()?;
    response
        .body_mut()
        .with_config()
        .limit(5 * 1024 * 1024)
        .read_to_vec()
        .ok()
}

type Done = Vec<(String, Option<SharedPixelBuffer<Rgba8Pixel>>)>;

/// Finds and fetches logos on its own thread, one station at a time.
pub struct Loader {
    requests: Sender<(String, String, u32)>,
    /// Finished logos (None: there's none), by station address.
    pub done: Arc<Mutex<Done>>,
}

impl Loader {
    /// `db_file` is opened on the worker (a second connection, as the scanner does). `notify` is
    /// called after each logo.
    pub fn new(db_file: PathBuf, base: String, notify: impl Fn() + Send + 'static) -> Loader {
        let (requests, rx) = channel::<(String, String, u32)>();
        let done: Arc<Mutex<Done>> = Arc::default();
        let out = done.clone();
        std::thread::spawn(move || {
            let Ok(db) = Db::open(&db_file, &base) else {
                return;
            };
            for (url, name, px) in rx {
                let now = std::time::SystemTime::now()
                    .duration_since(std::time::UNIX_EPOCH)
                    .map_or(0, |d| d.as_secs() as i64);
                let logo = logo_url(&db, &url, &name, now)
                    .and_then(|l| download(&l))
                    .and_then(|bytes| thumbnail(&bytes, px, px as f32 / 2.0));
                out.lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .push((url, logo));
                notify();
            }
        });
        Loader { requests, done }
    }

    /// Asks for a station's logo, `px` wide and round.
    pub fn request(&self, url: &str, name: &str, px: u32) {
        let _ = self.requests.send((url.to_string(), name.to_string(), px));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_are_normalized_like_2x() {
        assert_eq!(normalize("The Groove Salad (SomaFM) [128k]"), "groovesalad");
        assert_eq!(normalize("Drum & Bass FM"), "drumandbassfm");
        assert_eq!(normalize("  "), "");
    }

    #[test]
    fn query_values_are_encoded() {
        assert_eq!(
            encode("http://a.b/x y?z=1&w"),
            "http%3A%2F%2Fa.b%2Fx%20y%3Fz%3D1%26w"
        );
    }

    #[test]
    fn the_cache_decides_before_any_lookup() {
        let dir = std::env::temp_dir().join(format!("melora-rs-logos-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("dir");
        let db = Db::open(&dir.join("t.db"), r"C:\Melora").expect("db");
        db.set_cover(&key("http://found"), Some("http://logo.png"), 0)
            .expect("set");
        db.set_cover(&key("http://missed"), None, 1_000)
            .expect("set");
        assert_eq!(
            logo_url(&db, "http://found", "x", 99_999_999).as_deref(),
            Some("http://logo.png")
        ); // kept for good
        assert_eq!(logo_url(&db, "http://missed", "x", 1_000 + 3600), None); // a recent miss isn't retried
        drop(db);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
