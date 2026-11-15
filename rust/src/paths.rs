//! Melora is portable: everything it writes lives in a `data` folder next to the exe. Paths are
//! stored the way 2.x stores them, so 2.x's database works unchanged.

use std::path::{Path, PathBuf};

/// The folder Melora.exe is in.
pub fn base_dir() -> PathBuf {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf))
        .unwrap_or_default()
}

/// `data` next to the exe, created when missing.
pub fn data_dir() -> PathBuf {
    let dir = base_dir().join("data");
    // If it can't be made, opening the database will say so.
    let _ = std::fs::create_dir_all(&dir);
    dir
}

/// "C:\" for "C:\Music\a.flac"; empty for anything without a drive letter.
fn drive_root(path: &str) -> &str {
    let b = path.as_bytes();
    if b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\' {
        &path[..3]
    } else {
        ""
    }
}

fn with_slash(dir: &str) -> String {
    format!("{}\\", dir.trim_end_matches(['\\', '/']))
}

/// Paths on Melora's own drive are stored without the drive letter, so a portable copy keeps
/// working when the letter changes: relative to Melora for files inside its folder (they move
/// with it), and from the drive's root otherwise (they stay put when Melora's folder moves).
pub fn to_stored(full: &str, base: &str) -> String {
    let root = drive_root(full);
    if root.is_empty() || !root.eq_ignore_ascii_case(drive_root(base)) {
        return full.to_string();
    }
    let base = with_slash(base);
    match full.get(..base.len()) {
        Some(head) if head.eq_ignore_ascii_case(&base) => full[base.len()..].to_string(),
        _ => full[2..].to_string(), // "C:\Music\a.flac" → "\Music\a.flac"
    }
}

/// The full path for a stored one. Relative paths from 2.x before 1.3.2 still resolve as before.
pub fn from_stored(stored: &str, base: &str) -> String {
    if stored.starts_with('\\') && !stored.starts_with("\\\\") {
        return format!("{}{stored}", drive_root(base).trim_end_matches('\\'));
    }
    if !drive_root(stored).is_empty() || stored.starts_with("\\\\") {
        return stored.to_string();
    }
    // Relative to Melora's folder, with any ".." resolved.
    let mut parts: Vec<&str> = base.trim_end_matches('\\').split('\\').collect();
    for part in stored.split('\\') {
        match part {
            ".." if parts.len() > 1 => {
                parts.pop();
            }
            "." | "" | ".." => {}
            p => parts.push(p),
        }
    }
    parts.join("\\")
}

#[cfg(test)]
mod tests {
    use super::*;

    const BASE: &str = r"C:\_Apps\Melora\";

    #[test]
    fn stored_and_back_again() {
        for (full, stored) in [
            (r"C:\Users\me\Music\a.flac", r"\Users\me\Music\a.flac"), // same drive, outside Melora
            (r"C:\_Apps\Melora\Music\a.flac", r"Music\a.flac"),       // inside Melora's folder
            (r"D:\media\music\a.flac", r"D:\media\music\a.flac"),     // another drive
        ] {
            assert_eq!(to_stored(full, BASE), stored);
            assert_eq!(from_stored(stored, BASE), full);
        }
    }

    #[test]
    fn moving_melora_keeps_paths_working() {
        let outside = to_stored(r"C:\Users\me\Music\a.flac", BASE);
        let inside = to_stored(r"C:\_Apps\Melora\Music\a.flac", BASE);
        // Moved deeper, and onto a USB stick that came up as E:.
        assert_eq!(
            from_stored(&outside, r"C:\Apps\Portable\Melora\"),
            r"C:\Users\me\Music\a.flac"
        );
        assert_eq!(
            from_stored(&outside, r"E:\Melora\"),
            r"E:\Users\me\Music\a.flac"
        );
        assert_eq!(
            from_stored(&inside, r"C:\Apps\Portable\Melora\"),
            r"C:\Apps\Portable\Melora\Music\a.flac"
        );
    }

    #[test]
    fn older_relative_paths_still_resolve() {
        assert_eq!(
            from_stored(r"..\..\Users\me\Music\a.flac", BASE),
            r"C:\Users\me\Music\a.flac"
        );
    }
}
