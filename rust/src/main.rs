// Release builds have no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backdrop;
mod bass;
#[allow(dead_code)] // wired to the Music screen in the next step of phase 2
mod db;
mod engine;
mod folder;
#[allow(dead_code)]
mod library;
#[allow(dead_code)]
mod paths;

use std::cell::RefCell;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::time::{Duration, Instant};

use engine::Engine;
use slint::{ComponentHandle, Model, StandardListViewItem, Timer, TimerMode, VecModel};

slint::include_modules!();

/// Two clicks on the same row within this long play it.
const DOUBLE_CLICK: Duration = Duration::from_millis(450);

/// What's loaded and where we are in it. Owned by the UI thread.
struct Player {
    engine: Engine,
    tracks: Vec<PathBuf>,
    rows: Rc<VecModel<StandardListViewItem>>,
    index: Option<usize>,
    last_click: Option<(i32, Instant)>,
}

impl Player {
    fn load_folder(&mut self, ui: &AppWindow, dir: &Path, start: Option<&Path>) {
        self.engine.stop();
        self.index = None;
        self.tracks = folder::audio_files(dir);
        self.rows.set_vec(
            self.tracks
                .iter()
                .map(|p| row_text(p, false))
                .collect::<Vec<_>>(),
        );
        ui.set_selected(-1);
        if self.tracks.is_empty() {
            ui.set_track_title("Nothing playing".into());
            ui.set_track_detail(format!("No music found in {}", dir.display()).into());
            self.show_state(ui);
            return;
        }
        let first = start
            .and_then(|s| self.tracks.iter().position(|t| t == s))
            .unwrap_or(0);
        self.play_at(ui, first);
    }

    /// Plays the track at `i`, skipping any that can't be opened.
    fn play_at(&mut self, ui: &AppWindow, i: usize) {
        for j in i..self.tracks.len() {
            if self.engine.play(&self.tracks[j]) {
                self.set_index(ui, Some(j));
                self.queue_next();
                return;
            }
        }
        self.engine.stop();
        self.set_index(ui, None);
        ui.set_track_detail("Melora can't play the rest of this folder".into());
    }

    /// Lines up the following track so it starts without a gap. One that can't be opened is
    /// left out; the track after it is tried when this one ends.
    fn queue_next(&mut self) {
        let next = self.index.and_then(|i| self.tracks.get(i + 1));
        self.engine.set_next(next.map(PathBuf::as_path));
    }

    fn set_index(&mut self, ui: &AppWindow, index: Option<usize>) {
        if let Some(old) = self.index {
            self.rows
                .set_row_data(old, row_text(&self.tracks[old], false));
        }
        self.index = index;
        let Some(i) = index else {
            ui.set_track_title("Nothing playing".into());
            self.show_state(ui);
            return;
        };
        self.rows.set_row_data(i, row_text(&self.tracks[i], true));
        ui.set_selected(i as i32);
        ui.set_track_title(stem(&self.tracks[i]).into());
        let format = self.engine.format().map(|(codec, freq, chans)| {
            let layout = match chans {
                1 => "mono".to_string(),
                2 => "stereo".to_string(),
                n => format!("{n} channels"),
            };
            format!(
                "{codec}  ·  {:.1} kHz  ·  {layout}  ·  ",
                f64::from(freq) / 1000.0
            )
        });
        ui.set_track_detail(
            format!(
                "{}{} of {}",
                format.unwrap_or_default(),
                i + 1,
                self.tracks.len()
            )
            .into(),
        );
        ui.set_length(self.engine.length() as f32);
        self.show_state(ui);
    }

    fn show_state(&self, ui: &AppWindow) {
        let len = self.engine.length();
        ui.set_loaded(self.engine.is_loaded());
        ui.set_playing(self.engine.is_playing());
        // While the seek bar is dragged it leads, and the time shows where it would land.
        let pos = if ui.get_seeking() {
            f64::from(ui.get_position())
        } else {
            let pos = self.engine.position();
            ui.set_position(pos as f32);
            pos
        };
        let time = if self.engine.is_loaded() {
            format!("{} / {}", clock(pos), clock(len))
        } else {
            String::new()
        };
        ui.set_time_text(time.into());
    }

    /// Runs every few milliseconds: finishes fades, follows the gapless hand-over, notices the end.
    fn tick(&mut self, ui: &AppWindow) {
        if self.engine.tick() {
            let i = self.index.map_or(0, |i| i + 1);
            self.set_index(ui, Some(i));
            self.queue_next();
        } else if self.engine.has_ended() {
            // The last track, or the next one couldn't be opened: try what's after it.
            let after = self.index.map(|i| i + 1).filter(|&i| i < self.tracks.len());
            match after {
                Some(i) => self.play_at(ui, i),
                None => {
                    self.engine.stop();
                    self.set_index(ui, None);
                }
            }
        }
        self.show_state(ui);
    }

    fn toggle_play(&mut self, ui: &AppWindow) {
        if self.engine.is_playing() {
            self.engine.pause();
        } else if self.engine.is_loaded() {
            self.engine.resume();
        } else if !self.tracks.is_empty() {
            let i = usize::try_from(ui.get_selected()).unwrap_or(0);
            self.play_at(ui, i);
        }
        self.show_state(ui);
    }

    fn next(&mut self, ui: &AppWindow) {
        if let Some(i) = self.index.filter(|i| i + 1 < self.tracks.len()) {
            self.play_at(ui, i + 1);
        }
    }

    /// Back a track, or to the start of this one when there's no track before it.
    fn previous(&mut self, ui: &AppWindow) {
        match self.index {
            Some(i) if i > 0 => self.play_at(ui, i - 1),
            Some(_) => self.engine.seek(0.0),
            None => {}
        }
    }

    fn row_clicked(&mut self, ui: &AppWindow, row: i32) {
        let now = Instant::now();
        let double = self
            .last_click
            .is_some_and(|(r, at)| r == row && now - at < DOUBLE_CLICK);
        self.last_click = if double { None } else { Some((row, now)) };
        if let (true, Ok(i)) = (double, usize::try_from(row)) {
            self.play_at(ui, i);
        }
    }
}

fn stem(path: &Path) -> String {
    path.file_stem()
        .map(|n| n.to_string_lossy().into_owned())
        .unwrap_or_default()
}

fn row_text(path: &Path, playing: bool) -> StandardListViewItem {
    let name = stem(path);
    StandardListViewItem::from(
        if playing {
            format!("▶  {name}")
        } else {
            name
        }
        .as_str(),
    )
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

fn main() -> Result<(), slint::PlatformError> {
    let dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(Path::to_path_buf))
        .unwrap_or_default();
    let ui = AppWindow::new()?;
    let rows = Rc::new(VecModel::default());
    ui.set_tracks(rows.clone().into());
    let player = Rc::new(RefCell::new(Player {
        engine: Engine::new(),
        tracks: Vec::new(),
        rows,
        index: None,
        last_click: None,
    }));

    if let Err(e) = bass::init(-1, &dir) {
        ui.set_track_detail(format!("Sound couldn't start: {e}").into());
    }

    // Wires a UI callback to the player, with the window passed in.
    macro_rules! on {
        ($set:ident, |$p:ident, $ui:ident $(, $arg:ident)*| $body:expr) => {{
            let (weak, player) = (ui.as_weak(), player.clone());
            ui.$set(move |$($arg),*| {
                if let Some($ui) = weak.upgrade() {
                    let $p = &mut *player.borrow_mut();
                    $body;
                }
            });
        }};
    }

    on!(
        on_open_folder,
        |p, ui| if let Some(dir) = rfd::FileDialog::new().pick_folder() {
            p.load_folder(&ui, &dir, None)
        }
    );
    on!(on_toggle_play, |p, ui| p.toggle_play(&ui));
    on!(on_next, |p, ui| p.next(&ui));
    on!(on_previous, |p, ui| p.previous(&ui));
    on!(on_seek, |p, ui, seconds| {
        p.engine.seek(seconds.into());
        p.show_state(&ui)
    });
    on!(
        on_play_index,
        |p, ui, row| if let Ok(i) = usize::try_from(row) {
            p.play_at(&ui, i)
        }
    );
    on!(on_row_clicked, |p, ui, row| p.row_clicked(&ui, row));

    // Short enough that a pause lands right after its 150 ms fade.
    let ticker = Timer::default();
    let (weak, p) = (ui.as_weak(), player.clone());
    ticker.start(TimerMode::Repeated, Duration::from_millis(40), move || {
        if let Some(ui) = weak.upgrade() {
            p.borrow_mut().tick(&ui);
        }
    });

    // A folder, or a file (its folder loads and it plays first), can be passed on the command line.
    if let Some(arg) = std::env::args_os().nth(1).map(PathBuf::from) {
        let (dir, start) = if arg.is_dir() {
            (arg, None)
        } else {
            (
                arg.parent().map(Path::to_path_buf).unwrap_or_default(),
                Some(arg),
            )
        };
        player.borrow_mut().load_folder(&ui, &dir, start.as_deref());
    }

    ui.show()?;
    // The native window exists once the event loop has started.
    let weak = ui.as_weak();
    Timer::single_shot(Duration::ZERO, move || {
        if let Some(ui) = weak.upgrade() {
            backdrop::apply(ui.window());
        }
    });
    slint::run_event_loop()
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
