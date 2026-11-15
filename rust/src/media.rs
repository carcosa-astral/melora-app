//! The Windows media overlay (the panel with the cover and title that shows when you change the
//! volume or press a media key) and the media keys, which Windows sends to whoever owns that
//! overlay. As in 2.x: title, artist, album, cover, playing or paused with the position, and
//! Play/Pause, Next, Previous and seeking from the overlay. Through souvlaki, which does the same
//! on macOS and Linux.

use std::path::PathBuf;
use std::time::Duration;

use souvlaki::{
    MediaControlEvent, MediaControls, MediaMetadata, MediaPlayback, MediaPosition, PlatformConfig,
};

/// What the overlay or a media key asks for.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Command {
    Play,
    Pause,
    Toggle,
    Next,
    Previous,
    Stop,
    /// Seek to this many seconds.
    SeekTo(f64),
}

/// Where the overlay's picture comes from.
pub enum Cover {
    /// A picture's bytes (an album cover), written to a file for Windows to read.
    Bytes(Vec<u8>),
    /// A picture on the web (a station logo).
    Url(String),
    /// Melora's own icon, so a track without a cover doesn't keep the last one's.
    None,
}

pub struct Media {
    controls: MediaControls,
    /// Covers are written to two files in turn, so Windows never reads one while it's rewritten.
    flip: bool,
    data: PathBuf,
}

impl Media {
    /// Takes over the overlay for `window`. `on` gets each request (on a system thread).
    pub fn new(
        window: &slint::Window,
        data: PathBuf,
        on: impl Fn(Command) + Send + 'static,
    ) -> Option<Media> {
        let hwnd = crate::window::hwnd(window)?;
        let config = PlatformConfig {
            display_name: "Melora",
            dbus_name: "melora",
            hwnd: Some(hwnd as *mut _),
        };
        let mut controls = MediaControls::new(config).ok()?;
        controls
            .attach(move |e: MediaControlEvent| {
                let command = match e {
                    MediaControlEvent::Play => Command::Play,
                    MediaControlEvent::Pause => Command::Pause,
                    MediaControlEvent::Toggle => Command::Toggle,
                    MediaControlEvent::Next => Command::Next,
                    MediaControlEvent::Previous => Command::Previous,
                    MediaControlEvent::Stop => Command::Stop,
                    MediaControlEvent::SetPosition(MediaPosition(at)) => {
                        Command::SeekTo(at.as_secs_f64())
                    }
                    _ => return,
                };
                on(command);
            })
            .ok()?;
        let _ = controls.set_playback(MediaPlayback::Stopped);
        Some(Media {
            controls,
            flip: false,
            data,
        })
    }

    /// Shows a track (or a station) in the overlay.
    pub fn show(&mut self, title: &str, artist: &str, album: &str, cover: Cover, length: f64) {
        let file;
        let cover_url = match cover {
            Cover::Url(url) => Some(url),
            Cover::Bytes(bytes) => {
                self.flip = !self.flip;
                let path = self.data.join(if self.flip {
                    "now-playing-a.img"
                } else {
                    "now-playing-b.img"
                });
                file = std::fs::write(&path, bytes).ok().map(|()| path);
                file.as_ref().map(|p| format!("file://{}", p.display()))
            }
            Cover::None => {
                let path = self.data.join("now-playing-melora.png");
                if !path.exists() {
                    let _ = std::fs::write(&path, include_bytes!("../ui/icon-64.png"));
                }
                Some(format!("file://{}", path.display()))
            }
        };
        let metadata = MediaMetadata {
            title: Some(title),
            artist: Some(artist),
            album: Some(album),
            cover_url: cover_url.as_deref(),
            duration: (length > 0.0).then(|| Duration::from_secs_f64(length)),
        };
        let _ = self.controls.set_metadata(metadata);
    }

    /// Playing or paused, and where; None when nothing's loaded.
    pub fn set_state(&mut self, state: Option<(bool, f64)>) {
        let at = |s: f64| Some(MediaPosition(Duration::from_secs_f64(s.max(0.0))));
        let playback = match state {
            None => MediaPlayback::Stopped,
            Some((true, pos)) => MediaPlayback::Playing { progress: at(pos) },
            Some((false, pos)) => MediaPlayback::Paused { progress: at(pos) },
        };
        let _ = self.controls.set_playback(playback);
    }
}

/// A radio title "Artist - Title" as the overlay's (artist, title); the station's name for
/// whichever part is missing.
pub fn split_station_title(now: Option<&str>, station: &str) -> (String, String) {
    match now.map(|n| n.split_once(" - ").map_or((None, n), |(a, t)| (Some(a), t))) {
        Some((artist, title)) => (
            artist.unwrap_or(station).trim().to_string(),
            title.trim().to_string(),
        ),
        None => (station.to_string(), station.to_string()),
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn station_titles_split_into_artist_and_title() {
        let s = |n, st| super::split_station_title(n, st);
        assert_eq!(
            s(Some("Daft Punk - Veridis Quo"), "Groove Salad"),
            ("Daft Punk".into(), "Veridis Quo".into())
        );
        assert_eq!(
            s(Some("Just a title"), "Groove Salad"),
            ("Groove Salad".into(), "Just a title".into())
        );
        assert_eq!(
            s(None, "Groove Salad"),
            ("Groove Salad".into(), "Groove Salad".into())
        );
    }
}
