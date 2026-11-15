// Release builds have no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backdrop;
mod bass;

use std::cell::RefCell;
use std::path::Path;
use std::rc::Rc;
use std::time::Duration;

use bass::Stream;
use slint::{ComponentHandle, Timer, TimerMode};

slint::include_modules!();

const FADE_MS: u32 = 150;

fn main() -> Result<(), slint::PlatformError> {
    let dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf))
        .unwrap_or_default();
    let ui = AppWindow::new()?;
    if !bass::init(&dir) {
        ui.set_track_detail("Sound couldn't start: is bass.dll next to Melora.exe?".into());
    }

    let stream: Rc<RefCell<Option<Stream>>> = Rc::default();

    let (weak, s) = (ui.as_weak(), stream.clone());
    ui.on_open_file(move || {
        let picked = rfd::FileDialog::new()
            .add_filter(
                "Music",
                &["mp3", "flac", "opus", "ogg", "m4a", "aac", "wav"],
            )
            .pick_file();
        if let (Some(path), Some(ui)) = (picked, weak.upgrade()) {
            load(&ui, &s, &path);
        }
    });

    let (weak, s) = (ui.as_weak(), stream.clone());
    ui.on_toggle_play(move || {
        let Some(ui) = weak.upgrade() else { return };
        let guard = s.borrow();
        let Some(st) = guard.as_ref() else { return };
        if st.is_playing() {
            // Fade out, then pause once the fade is done (2.x does the same).
            st.slide_volume(0.0, FADE_MS);
            ui.set_playing(false);
            let s = s.clone();
            Timer::single_shot(Duration::from_millis(FADE_MS.into()), move || {
                if let Some(st) = s.borrow().as_ref() {
                    st.pause();
                }
            });
        } else {
            st.set_volume(0.0);
            st.play();
            st.slide_volume(1.0, FADE_MS);
            ui.set_playing(true);
        }
    });

    let s = stream.clone();
    ui.on_seek(move |seconds| {
        if let Some(st) = s.borrow().as_ref() {
            st.seek(seconds.into());
        }
    });

    // Keep the seek bar and time moving.
    let ticker = Timer::default();
    let (weak, s) = (ui.as_weak(), stream.clone());
    ticker.start(TimerMode::Repeated, Duration::from_millis(250), move || {
        let (Some(ui), Some(st)) = (
            weak.upgrade(),
            s.borrow()
                .as_ref()
                .map(|st| (st.position(), st.length(), st.is_playing())),
        ) else {
            return;
        };
        let (pos, len, playing) = st;
        ui.set_position(pos as f32);
        ui.set_time_text(format!("{} / {}", clock(pos), clock(len)).into());
        if !playing && ui.get_playing() && pos >= len - 0.3 {
            ui.set_playing(false); // reached the end
        }
    });

    if let Some(path) = std::env::args_os().nth(1) {
        load(&ui, &stream, Path::new(&path));
    }

    ui.show()?;
    // The native window exists once the event loop has started.
    let weak = ui.as_weak();
    Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = weak.upgrade() {
            ui.set_mica(backdrop::apply(ui.window()));
        }
    });
    slint::run_event_loop()
}

fn load(ui: &AppWindow, stream: &Rc<RefCell<Option<Stream>>>, path: &Path) {
    let name = path
        .file_stem()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default();
    stream.borrow_mut().take(); // free the old one first
    let Some(st) = Stream::open(path) else {
        ui.set_track_title(name.into());
        ui.set_track_detail("Melora can't play this file".into());
        ui.set_loaded(false);
        ui.set_playing(false);
        return;
    };
    let (freq, chans) = st.info();
    let ext = path
        .extension()
        .map(|e| e.to_string_lossy().to_uppercase())
        .unwrap_or_default();
    let layout = match chans {
        1 => "mono".to_string(),
        2 => "stereo".to_string(),
        n => format!("{n} channels"),
    };
    ui.set_track_title(name.into());
    ui.set_track_detail(
        format!("{ext}  ·  {:.1} kHz  ·  {layout}", f64::from(freq) / 1000.0).into(),
    );
    ui.set_length(st.length() as f32);
    ui.set_position(0.0);
    ui.set_loaded(true);
    st.play();
    ui.set_playing(true);
    *stream.borrow_mut() = Some(st);
}

/// 3:07, or 1:02:45 past an hour.
fn clock(seconds: f64) -> String {
    let t = seconds.max(0.0) as u64;
    let (h, m, s) = (t / 3600, t / 60 % 60, t % 60);
    if h > 0 {
        format!("{h}:{m:02}:{s:02}")
    } else {
        format!("{m}:{s:02}")
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn clock_formats() {
        assert_eq!(super::clock(0.0), "0:00");
        assert_eq!(super::clock(187.9), "3:07");
        assert_eq!(super::clock(3765.0), "1:02:45");
    }
}
