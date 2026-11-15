//! Watches the library folders and asks for a rescan once changes settle, as 2.x does: copying a
//! folder fires hundreds of events, so it waits until two seconds pass without another.

use std::path::Path;
use std::sync::mpsc::{RecvTimeoutError, channel};
use std::time::Duration;

use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};

const SETTLE: Duration = Duration::from_secs(2);

/// Folders being watched. Watching stops when this is dropped.
pub struct Watch {
    _watchers: Vec<RecommendedWatcher>,
}

/// Whether a change matters to the library: a file it picks up, or a folder (which has no
/// extension), renamed, added or removed. Covers and other files are left alone.
pub fn matters(event: &Event) -> bool {
    if matches!(event.kind, EventKind::Access(_)) {
        return false;
    }
    event.paths.iter().any(|p| match p.extension() {
        None => true,
        Some(e) => ["flac", "opus", "mp3"]
            .iter()
            .any(|x| e.eq_ignore_ascii_case(x)),
    })
}

/// Watches `folders` (those that exist) and calls `changed` on a worker thread after changes settle.
pub fn watch(folders: &[String], changed: impl Fn() + Send + 'static) -> Watch {
    let (tx, rx) = channel::<()>();
    let mut watchers = Vec::new();
    for folder in folders.iter().filter(|f| Path::new(f).is_dir()) {
        let tx = tx.clone();
        let handler = move |res: notify::Result<Event>| {
            if res.is_ok_and(|e| matters(&e)) {
                let _ = tx.send(());
            }
        };
        // A folder that can't be watched still gets scanned at startup and when folders change.
        if let Ok(mut w) = notify::recommended_watcher(handler)
            && w.watch(Path::new(folder), RecursiveMode::Recursive).is_ok()
        {
            watchers.push(w);
        }
    }
    drop(tx); // the watchers hold the only senders, so the thread ends when they're dropped
    std::thread::spawn(move || {
        while rx.recv().is_ok() {
            loop {
                match rx.recv_timeout(SETTLE) {
                    Ok(()) => continue,
                    Err(RecvTimeoutError::Timeout) => break,
                    Err(RecvTimeoutError::Disconnected) => return,
                }
            }
            changed();
        }
    });
    Watch {
        _watchers: watchers,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use notify::event::{AccessKind, CreateKind};
    use std::path::PathBuf;

    fn event(kind: EventKind, path: &str) -> Event {
        Event {
            kind,
            paths: vec![PathBuf::from(path)],
            attrs: Default::default(),
        }
    }

    #[test]
    fn only_music_files_and_folders_matter() {
        let create = EventKind::Create(CreateKind::Any);
        assert!(matters(&event(create, r"D:\Music\a.FLAC")));
        assert!(matters(&event(create, r"D:\Music\New Album")));
        assert!(!matters(&event(create, r"D:\Music\Album\cover.jpg")));
        assert!(!matters(&event(
            EventKind::Access(AccessKind::Any),
            r"D:\Music\a.mp3"
        )));
    }

    #[test]
    fn a_burst_of_changes_asks_for_one_rescan() {
        let dir = std::env::temp_dir().join(format!("melora-rs-watch-{}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("dir");
        let (tx, rx) = channel();
        let _w = watch(&[dir.to_string_lossy().into_owned()], move || {
            let _ = tx.send(());
        });
        for i in 0..5 {
            std::fs::write(dir.join(format!("{i}.mp3")), b"").expect("file");
        }
        assert!(rx.recv_timeout(Duration::from_secs(8)).is_ok()); // one, after things settle
        assert!(rx.recv_timeout(Duration::from_millis(2500)).is_err()); // and only one
        let _ = std::fs::remove_dir_all(&dir);
    }
}
