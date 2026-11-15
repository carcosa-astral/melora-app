//! Tells you when a newer Melora is on GitHub, as 2.x: it only looks and links to the release
//! page; it never downloads or installs anything (Melora is a folder you replace yourself). It
//! checks at most once a day, and the answer is kept in 2.x's settings.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::db::Db;

const LATEST_RELEASE: &str =
    "https://api.github.com/repos/carcosa-astral/melora-app/releases/latest";
pub const RELEASES_PAGE: &str = "https://github.com/carcosa-astral/melora-app/releases";
const INTERVAL_SECS: i64 = 24 * 3600;
pub const CURRENT: &str = env!("CARGO_PKG_VERSION");

/// "v1.2.0", "1.2" or "3.0.0-alpha.1" as (1, 2, 0) and so on; None for anything else.
pub fn parse(tag: &str) -> Option<(u32, u32, u32)> {
    let text = tag.trim().trim_start_matches(['v', 'V']);
    let text = text.split(['-', '+']).next()?; // a pre-release counts as its version
    let mut parts = text.split('.').map(|p| p.parse::<u32>());
    let major = parts.next()?.ok()?;
    let minor = parts.next().unwrap_or(Ok(0)).ok()?;
    let patch = parts.next().unwrap_or(Ok(0)).ok()?;
    parts.next().is_none().then_some((major, minor, patch))
}

/// Whether `latest` is a later version than `current`.
pub fn is_newer(latest: &str, current: &str) -> bool {
    matches!((parse(latest), parse(current)), (Some(l), Some(c)) if l > c)
}

pub fn enabled(db: &Db) -> bool {
    db.setting("update.enabled").as_deref() != Some("0")
}

/// Due when it's on and a day has passed since the last check.
pub fn due(db: &Db, now: i64) -> bool {
    let last = db
        .setting("update.checkedAt")
        .and_then(|s| s.parse::<i64>().ok())
        .unwrap_or(0);
    enabled(db) && now - last >= INTERVAL_SECS
}

/// The newer version and its page, if one is out.
pub fn available(db: &Db) -> Option<(String, String)> {
    let latest = db.setting("update.latest")?;
    (enabled(db) && is_newer(&latest, CURRENT)).then(|| {
        (
            latest,
            db.setting("update.url")
                .unwrap_or_else(|| RELEASES_PAGE.to_string()),
        )
    })
}

/// Checks in the background, as 2.x does.
pub struct Checker {
    pub checking: Arc<AtomicBool>,
    pub failed: Arc<AtomicBool>,
    db_file: PathBuf,
    base: String,
}

impl Checker {
    pub fn new(db_file: PathBuf, base: String) -> Checker {
        Checker {
            checking: Arc::default(),
            failed: Arc::default(),
            db_file,
            base,
        }
    }

    /// Asks GitHub for the latest release now (unless a check is running); `done` is called after.
    pub fn check(&self, done: impl Fn() + Send + 'static) {
        if self.checking.swap(true, Ordering::SeqCst) {
            return;
        }
        let (checking, failed, file, base) = (
            self.checking.clone(),
            self.failed.clone(),
            self.db_file.clone(),
            self.base.clone(),
        );
        std::thread::spawn(move || {
            let result = (|| -> Result<(), String> {
                let db = Db::open(&file, &base).map_err(|e| e.to_string())?;
                let mut response = crate::online::agent()
                    .get(LATEST_RELEASE)
                    .call()
                    .map_err(|e| e.to_string())?;
                let text = response
                    .body_mut()
                    .read_to_string()
                    .map_err(|e| e.to_string())?;
                let json: serde_json::Value =
                    serde_json::from_str(&text).map_err(|e| e.to_string())?;
                if let Some((a, b, c)) = json["tag_name"].as_str().and_then(parse) {
                    let _ = db.set_setting("update.latest", &format!("{a}.{b}.{c}"));
                    if let Some(url) = json["html_url"].as_str() {
                        let _ = db.set_setting("update.url", url);
                    }
                }
                let _ = db.set_setting("update.checkedAt", &crate::online::now().to_string());
                Ok(())
            })();
            if let Err(e) = &result {
                // Offline is normal; the next start tries again.
                crate::log::write(&format!("Update check: {e}"));
            }
            failed.store(result.is_err(), Ordering::SeqCst);
            checking.store(false, Ordering::SeqCst);
            done();
        });
    }

    /// One line for Settings, as 2.x shows it.
    pub fn status(&self, db: &Db) -> String {
        if !enabled(db) {
            "Not checking for new versions.".into()
        } else if self.checking.load(Ordering::SeqCst) {
            "Checking…".into()
        } else if let Some((latest, _)) = available(db) {
            format!("Melora {latest} is available.")
        } else if self.failed.load(Ordering::SeqCst) {
            "Couldn't reach GitHub to check for a new version.".into()
        } else if db.setting("update.latest").is_some() {
            "This is the newest version.".into()
        } else {
            String::new()
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versions_parse_and_compare() {
        assert_eq!(parse("v2.0.0"), Some((2, 0, 0)));
        assert_eq!(parse("1.3"), Some((1, 3, 0)));
        assert_eq!(parse("3.0.0-alpha.1"), Some((3, 0, 0)));
        assert_eq!(parse("latest"), None);
        assert_eq!(parse("1.2.3.4"), None);
        assert!(is_newer("v3.0.1", "3.0.0-alpha.1"));
        assert!(!is_newer("v2.0.0", "3.0.0-alpha.1"));
        assert!(!is_newer("v3.0.0", "3.0.0"));
        assert!(!is_newer("nonsense", "1.0.0"));
    }
}
