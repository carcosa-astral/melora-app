//! A small text log in the data folder (`data\melora.log`) for things that go wrong without
//! anyone seeing: a refused scrobble, a file that can't be played, Discord trouble. Past 1 MB it's
//! renamed after the time it was closed (`melora-2026-10-22_195601.log`) and a new one started;
//! the newest five of those are kept. As 2.x.

use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::Mutex;

const MAX_SIZE: u64 = 1024 * 1024;
const KEEP_OLD: usize = 5;

static LOCK: Mutex<()> = Mutex::new(());

pub fn path() -> PathBuf {
    crate::paths::data_dir().join("melora.log")
}

/// Adds a line, stamped with the local time. Never fails: a log that can't be written mustn't
/// take anything else down with it.
pub fn write(message: &str) {
    write_to(&path(), message);
}

fn write_to(file: &Path, message: &str) {
    let _guard = LOCK.lock().unwrap_or_else(|e| e.into_inner());
    if std::fs::metadata(file).is_ok_and(|m| m.len() > MAX_SIZE) {
        roll(file);
    }
    let line = format!(
        "{}  {message}\r\n",
        chrono::Local::now().format("%Y-%m-%d %H:%M:%S")
    );
    if let Ok(mut f) = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(file)
    {
        let _ = f.write_all(line.as_bytes());
    }
}

/// Renames the full log after the current time and deletes all but the newest few old ones.
fn roll(file: &Path) {
    let (Some(dir), Some(stem)) = (
        file.parent(),
        file.file_stem().map(|s| s.to_string_lossy().into_owned()),
    ) else {
        return;
    };
    let stamp = chrono::Local::now().format("%Y-%m-%d_%H%M%S");
    let _ = std::fs::rename(file, dir.join(format!("{stem}-{stamp}.log")));
    // The timestamped names sort oldest first.
    let mut old: Vec<PathBuf> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| {
            p.file_name().is_some_and(|n| {
                let n = n.to_string_lossy();
                n.starts_with(&format!("{stem}-")) && n.ends_with(".log")
            })
        })
        .collect();
    old.sort();
    for stale in old.iter().take(old.len().saturating_sub(KEEP_OLD)) {
        let _ = std::fs::remove_file(stale);
    }
}

/// Makes sure the log exists (so it can be opened), and returns where it is.
pub fn ensure() -> PathBuf {
    let p = path();
    if !p.exists() {
        write("Log started.");
    }
    p
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lines_are_stamped_and_a_full_log_rolls_over_keeping_five() {
        let dir = std::env::temp_dir().join(format!("melora-rs-log-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).expect("dir");
        let file = dir.join("melora.log");
        write_to(&file, "first");
        let text = std::fs::read_to_string(&file).expect("read");
        assert!(text.ends_with("  first\r\n") && text.len() > "  first\r\n".len() + 18);

        for i in 0..7 {
            std::fs::write(
                dir.join(format!("melora-2020-01-0{}_000000.log", i + 1)),
                "old",
            )
            .expect("old");
        }
        std::fs::write(&file, vec![b'x'; (MAX_SIZE + 1) as usize]).expect("big");
        write_to(&file, "after");
        assert_eq!(
            std::fs::read_to_string(&file)
                .expect("read")
                .lines()
                .count(),
            1
        );
        let old = std::fs::read_dir(&dir)
            .expect("dir")
            .flatten()
            .filter(|e| e.file_name() != "melora.log")
            .count();
        assert_eq!(old, KEEP_OLD);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
