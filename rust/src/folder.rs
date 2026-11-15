//! Finding files in a folder and its subfolders.

use std::path::{Path, PathBuf};

/// Every file in `dir` and its subfolders with one of these extensions, sorted the same way.
pub fn files_with(dir: &Path, extensions: &[&str]) -> Vec<PathBuf> {
    let mut found = Vec::new();
    collect(dir, extensions, &mut found);
    found.sort_by_key(|p| p.to_string_lossy().to_lowercase());
    found
}

fn collect(dir: &Path, extensions: &[&str], found: &mut Vec<PathBuf>) {
    // An unreadable folder is skipped, like a missing file.
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    for entry in entries.flatten() {
        let path = entry.path();
        if path.is_dir() {
            collect(&path, extensions, found);
        } else if has_extension(&path, extensions) {
            found.push(path);
        }
    }
}

fn has_extension(path: &Path, extensions: &[&str]) -> bool {
    path.extension()
        .is_some_and(|e| extensions.iter().any(|a| e.eq_ignore_ascii_case(a)))
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
        let names: Vec<String> = super::files_with(&dir, &["mp3", "flac", "opus"])
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
