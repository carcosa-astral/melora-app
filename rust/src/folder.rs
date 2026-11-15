//! The tracks in a folder, for phase 1 (the real library comes in phase 2).

use std::path::{Path, PathBuf};

const AUDIO: [&str; 7] = ["mp3", "flac", "opus", "ogg", "m4a", "aac", "wav"];

/// Every audio file in `dir` and its subfolders, sorted by path (A–Z, ignoring case), so albums
/// play in track order when their files are numbered.
pub fn audio_files(dir: &Path) -> Vec<PathBuf> {
    let mut found = Vec::new();
    collect(dir, &mut found);
    found.sort_by_key(|p| p.to_string_lossy().to_lowercase());
    found
}

fn collect(dir: &Path, found: &mut Vec<PathBuf>) {
    // An unreadable folder is skipped, like a missing file.
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect(&path, found);
        } else if is_audio(&path) {
            found.push(path);
        }
    }
}

pub fn is_audio(path: &Path) -> bool {
    path.extension()
        .is_some_and(|e| AUDIO.iter().any(|a| e.eq_ignore_ascii_case(a)))
}

#[cfg(test)]
mod tests {
    #[test]
    fn finds_audio_in_subfolders_sorted_ignoring_case() {
        let dir = std::env::temp_dir().join(format!("melora-rs-folder-{}", std::process::id()));
        let sub = dir.join("Disc 2");
        std::fs::create_dir_all(&sub).expect("dirs");
        for f in [
            dir.join("b.MP3"),
            dir.join("A.flac"),
            dir.join("notes.txt"),
            sub.join("01.opus"),
        ] {
            std::fs::write(f, b"").expect("file");
        }
        let names: Vec<String> = super::audio_files(&dir)
            .iter()
            .map(|p| {
                p.strip_prefix(&dir)
                    .expect("inside")
                    .to_string_lossy()
                    .into_owned()
            })
            .collect();
        let _ = std::fs::remove_dir_all(&dir);
        assert_eq!(names, ["A.flac", "b.MP3", r"Disc 2\01.opus"]);
    }
}
