//! Album covers, found as 2.x finds them: a picture in the album's folder (or the folder above a
//! CD1\, Disc 2\ folder), else the one embedded in its files. They're shrunk to the size they're
//! shown at, on a worker thread, so a big scan of a 3000 px cover never sits in memory.

use std::path::Path;
use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex};

use lofty::picture::PictureType;
use lofty::prelude::*;
use slint::{Rgba8Pixel, SharedPixelBuffer};

use crate::library::disc_folder_number;

const NAMES: [&str; 4] = ["cover", "folder", "front", "album"];
const EXTENSIONS: [&str; 3] = ["jpg", "jpeg", "png"];

/// The cover picture's bytes for an album, from one of its tracks; None when it has none.
pub fn find(sample_track: &Path) -> Option<Vec<u8>> {
    folder_cover(sample_track).or_else(|| embedded_cover(sample_track))
}

fn folder_cover(track: &Path) -> Option<Vec<u8>> {
    let dir = track.parent()?;
    let is_disc = dir
        .file_name()
        .is_some_and(|n| disc_folder_number(&n.to_string_lossy()) > 0);
    let dirs = if is_disc {
        vec![dir, dir.parent()?]
    } else {
        vec![dir]
    };
    for dir in dirs {
        // Names are matched ignoring case, so read the folder once rather than guessing spellings.
        let files: Vec<_> = std::fs::read_dir(dir)
            .ok()?
            .flatten()
            .map(|e| e.path())
            .collect();
        for name in NAMES {
            for ext in EXTENSIONS {
                let wanted = format!("{name}.{ext}");
                let hit = files.iter().find(|p| {
                    p.file_name()
                        .is_some_and(|f| f.to_string_lossy().eq_ignore_ascii_case(&wanted))
                });
                if let Some(bytes) = hit.and_then(|p| std::fs::read(p).ok()) {
                    return Some(bytes);
                }
            }
        }
    }
    None
}

fn embedded_cover(track: &Path) -> Option<Vec<u8>> {
    let file = lofty::read_from_path(track).ok()?;
    let tag = file.primary_tag().or_else(|| file.first_tag())?;
    let pictures = tag.pictures();
    let front = pictures
        .iter()
        .find(|p| p.pic_type() == PictureType::CoverFront)
        .or(pictures.first())?;
    Some(front.data().to_vec())
}

/// Decodes a picture, crops it to a square `px` wide (filling it, as 2.x does) and rounds its
/// corners by `radius` pixels (half of `px` makes a circle). The rounding is baked into the
/// picture because Slint's software renderer doesn't round a clipped image.
pub fn thumbnail(bytes: &[u8], px: u32, radius: f32) -> Option<SharedPixelBuffer<Rgba8Pixel>> {
    let image = image::load_from_memory(bytes).ok()?;
    let mut square = image
        .resize_to_fill(px, px, image::imageops::FilterType::Triangle)
        .to_rgba8();
    round_corners(&mut square, radius);
    Some(SharedPixelBuffer::clone_from_slice(
        square.as_raw(),
        square.width(),
        square.height(),
    ))
}

/// Makes the corners outside a rounded square see-through, with a one-pixel soft edge.
fn round_corners(img: &mut image::RgbaImage, radius: f32) {
    let (w, h) = (img.width() as f32, img.height() as f32);
    let r = radius.clamp(0.0, w.min(h) / 2.0);
    if r <= 0.0 {
        return;
    }
    for (x, y, p) in img.enumerate_pixels_mut() {
        // Distance from the pixel's centre to the nearest corner circle's centre, in a corner zone.
        let (cx, cy) = (x as f32 + 0.5, y as f32 + 0.5);
        let dx = if cx < r {
            r - cx
        } else if cx > w - r {
            cx - (w - r)
        } else {
            0.0
        };
        let dy = if cy < r {
            r - cy
        } else if cy > h - r {
            cy - (h - r)
        } else {
            0.0
        };
        let outside = (dx * dx + dy * dy).sqrt() - r; // > 0 outside the rounded edge
        let coverage = (0.5 - outside).clamp(0.0, 1.0);
        p.0[3] = (f32::from(p.0[3]) * coverage).round() as u8;
    }
}

type Done = Vec<(String, Option<SharedPixelBuffer<Rgba8Pixel>>)>;

/// Finds and shrinks covers on its own thread, one at a time, in the order asked.
pub struct Loader {
    requests: Sender<(String, u32, f32)>,
    /// Finished covers (None: the album has none), keyed by its sample track's path.
    pub done: Arc<Mutex<Done>>,
}

impl Loader {
    /// `notify` is called (on the worker) after each cover, to get the UI to collect it.
    pub fn new(notify: impl Fn() + Send + 'static) -> Loader {
        let (requests, rx) = channel::<(String, u32, f32)>();
        let done: Arc<Mutex<Done>> = Arc::default();
        let out = done.clone();
        std::thread::spawn(move || {
            // shortcut: covers asked for and then scrolled past are still made, in order; skip
            // stale ones if fast scrolling through a big library feels slow.
            for (key, px, radius) in rx {
                let cover = find(Path::new(&key)).and_then(|b| thumbnail(&b, px, radius));
                out.lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .push((key, cover));
                notify();
            }
        });
        Loader { requests, done }
    }

    /// Asks for a picture `px` wide with corners rounded by `radius` pixels.
    pub fn request(&self, key: String, px: u32, radius: f32) {
        // Only fails once the worker is gone, when there's nobody left to show covers to.
        let _ = self.requests.send((key, px, radius));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn png(w: u32, h: u32) -> Vec<u8> {
        let mut out = std::io::Cursor::new(Vec::new());
        image::RgbaImage::from_pixel(w, h, image::Rgba([10, 20, 30, 255]))
            .write_to(&mut out, image::ImageFormat::Png)
            .expect("png");
        out.into_inner()
    }

    #[test]
    fn finds_a_folder_cover_ignoring_case_and_above_disc_folders() {
        let dir = std::env::temp_dir().join(format!("melora-rs-covers-{}", std::process::id()));
        let disc = dir.join("CD2");
        std::fs::create_dir_all(&disc).expect("dirs");
        std::fs::write(dir.join("Folder.PNG"), png(2, 2)).expect("cover");
        std::fs::write(disc.join("01.flac"), b"").expect("track");
        std::fs::write(dir.join("02.flac"), b"").expect("track");

        assert!(find(&disc.join("01.flac")).is_some()); // from the folder above CD2
        assert!(find(&dir.join("02.flac")).is_some());
        std::fs::remove_file(dir.join("Folder.PNG")).expect("rm");
        assert!(find(&dir.join("02.flac")).is_none()); // no picture, and the file has no tags
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn thumbnails_are_square_and_the_asked_size() {
        let t = thumbnail(&png(40, 20), 8, 0.0).expect("thumbnail");
        assert_eq!((t.width(), t.height()), (8, 8));
        assert!(thumbnail(b"not a picture", 8, 0.0).is_none());
    }

    #[test]
    fn corners_are_rounded_into_the_picture() {
        let alpha = |t: &SharedPixelBuffer<Rgba8Pixel>, x: usize, y: usize| {
            t.as_slice()[y * t.width() as usize + x].a
        };
        let circle = thumbnail(&png(40, 40), 40, 20.0).expect("thumbnail");
        assert_eq!(alpha(&circle, 0, 0), 0); // the corner is gone
        assert_eq!(alpha(&circle, 20, 20), 255); // the middle stays
        assert!(alpha(&circle, 20, 0) > 240); // the top edge's middle stays (bar a soft edge)
        let square = thumbnail(&png(40, 40), 40, 0.0).expect("thumbnail");
        assert_eq!(alpha(&square, 0, 0), 255); // no rounding asked
        let rounded = thumbnail(&png(40, 40), 40, 6.0).expect("thumbnail");
        assert_eq!(alpha(&rounded, 0, 0), 0);
        assert_eq!(alpha(&rounded, 6, 0), 255); // just past the corner
    }
}
