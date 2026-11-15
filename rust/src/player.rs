//! The two libraries (Music and Mixes), which one is on screen, and what's playing. Owned by the
//! UI thread.

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::rc::Rc;

use slint::{ComponentHandle, VecModel};

use crate::db::{Db, Kind};
use crate::engine::Engine;
use crate::library::Library;
use crate::queue::{Info, Queue, Repeat};
use crate::rows::{self, ListRow, RowKind};
use crate::{AppWindow, Row, covers};

/// One library as listed, with where its list was left.
pub struct View {
    pub kind: Kind,
    pub lib: Library,
    pub rows: Vec<ListRow>,
    /// The list row of each track.
    pub track_rows: Vec<usize>,
    pub selected: i32,
    pub scroll: f32,
}

impl View {
    pub fn new(kind: Kind) -> View {
        View {
            kind,
            lib: Library::default(),
            rows: Vec::new(),
            track_rows: Vec::new(),
            selected: -1,
            scroll: 0.0,
        }
    }

    fn path(&self, track: usize) -> Option<String> {
        self.lib.tracks.get(track).map(|t| t.path.clone())
    }

    /// What the shuffle needs to know about each track.
    fn queue_info(&self) -> Vec<Info> {
        self.lib
            .tracks
            .iter()
            .map(|t| Info {
                artist: t.artist.to_lowercase(),
                album: t.album,
            })
            .collect()
    }

    /// The track on a list row, if the row is a track.
    fn track_at(&self, row: i32) -> Option<usize> {
        usize::try_from(row)
            .ok()
            .and_then(|r| self.rows.get(r))
            .and_then(|r| r.track)
    }
}

pub struct Player {
    pub engine: Engine,
    pub views: [View; 2],
    /// The view on screen: 0 Music, 1 Mixes.
    pub tab: usize,
    /// What's playing (or paused): the view it's in and its track there.
    pub playing: Option<(usize, usize)>,
    /// The play order through the playing view.
    pub queue: Queue,
    /// Where settings are kept (None if the database couldn't be opened).
    pub db: Option<Db>,
    /// Only the rows in view (see `refresh_view`).
    pub model: Rc<VecModel<Row>>,
    /// Covers and DJ pictures near the view, by their sample track (None: there's none).
    pub covers: HashMap<String, Option<slint::Image>>,
    /// Pictures asked for and not back yet.
    pub pending: HashSet<String>,
    pub loader: covers::Loader,
}

fn to_row(r: &ListRow, index: usize, playing: bool) -> Row {
    Row {
        index: index as i32,
        kind: match r.kind {
            RowKind::Album => 0,
            RowKind::Disc => 1,
            RowKind::Track => 2,
            RowKind::Dj => 3,
            RowKind::Mix => 4,
        },
        title: r.title.as_str().into(),
        subtitle: r.subtitle.as_str().into(),
        number: r.number.as_str().into(),
        length: r.length.as_str().into(),
        y: r.y,
        height: r.height,
        playing,
        art: slint::Image::default(),
        has_art: false,
    }
}

impl Player {
    fn view(&self) -> &View {
        &self.views[self.tab]
    }

    /// Shows a freshly loaded library in its view, keeping what's playing and what's selected.
    pub fn set_library(&mut self, ui: &AppWindow, which: usize, lib: Library) {
        if which == self.tab {
            self.save_place(ui);
        }
        let view = &mut self.views[which];
        let playing = self
            .playing
            .filter(|p| p.0 == which)
            .and_then(|p| view.path(p.1));
        let selected = view.track_at(view.selected).and_then(|i| view.path(i));

        view.lib = lib;
        view.rows = rows::build(&view.lib, view.kind);
        view.track_rows = view
            .rows
            .iter()
            .enumerate()
            .filter_map(|(r, row)| row.track.map(|_| r))
            .collect();
        let find = |path: Option<String>| {
            path.and_then(|p| view.lib.tracks.iter().position(|t| t.path == p))
        };
        if let Some(path) = playing {
            self.playing = find(Some(path)).map(|i| (which, i));
            // The list changed, so the play order starts again from the playing track.
            let info = view.queue_info();
            self.queue.start(info, self.playing.map(|p| p.1));
        }
        view.selected = find(selected).map_or(-1, |i| view.track_rows[i] as i32);

        if which == self.tab {
            self.show_view(ui);
        }
        self.queue_next(); // the track after the playing one may have changed
    }

    /// Remembers where the list on screen was, before switching away or reloading it.
    fn save_place(&mut self, ui: &AppWindow) {
        let view = &mut self.views[self.tab];
        view.selected = ui.get_selected();
        view.scroll = ui.get_list_scroll();
    }

    /// Puts the current view's list on screen, where it was left.
    fn show_view(&mut self, ui: &AppWindow) {
        let view = &self.views[self.tab];
        ui.set_tab(self.tab as i32);
        ui.set_row_count(view.rows.len() as i32);
        ui.set_list_height(rows::total_height(&view.rows));
        ui.set_selected(view.selected);
        ui.set_list_scroll(view.scroll);
        ui.set_summary(rows::summary(&view.lib, view.kind).into());
        self.refresh_view(ui);
    }

    pub fn show_tab(&mut self, ui: &AppWindow, tab: usize) {
        if tab == self.tab || tab >= self.views.len() {
            return;
        }
        self.save_place(ui);
        self.tab = tab;
        self.show_view(ui);
    }

    /// Starts a run through a view's list at track `i` (in order or shuffled).
    pub fn play_at(&mut self, ui: &AppWindow, which: usize, i: usize) {
        let info = self.views[which].queue_info();
        self.queue.start(info, Some(i));
        self.play_current(ui, which);
    }

    /// Plays the queue's current track, moving on past any that can't be opened.
    fn play_current(&mut self, ui: &AppWindow, which: usize) {
        let mut track = self.queue.current();
        for _ in 0..self.views[which].lib.tracks.len() {
            let Some(t) = track else { break };
            if self
                .engine
                .play(Path::new(&self.views[which].lib.tracks[t].path))
            {
                self.set_playing(ui, Some((which, t)));
                self.queue_next();
                return;
            }
            track = self.queue.next(true);
        }
        self.engine.stop();
        self.set_playing(ui, None);
        ui.set_track_detail("Melora can't play the rest of the list".into());
    }

    /// Lines up the following track so it starts without a gap. One that can't be opened is
    /// left out; the track after it is tried when this one ends.
    fn queue_next(&mut self) {
        if !self.engine.is_loaded() {
            return;
        }
        let next = self.playing.and_then(|(v, _)| {
            self.queue
                .peek_next()
                .and_then(|i| self.views[v].lib.tracks.get(i))
        });
        self.engine.set_next(next.map(|t| Path::new(&t.path)));
    }

    /// Hands the list the rows now in view, with the playing one marked and their pictures.
    pub fn refresh_view(&mut self, ui: &AppWindow) {
        let view = &self.views[self.tab];
        let range = rows::in_view(&view.rows, ui.get_list_scroll(), ui.get_list_view_height());
        let playing = self
            .playing
            .filter(|p| p.0 == self.tab)
            .and_then(|p| view.track_rows.get(p.1).copied());
        let scale = ui.window().scale_factor();
        let mut shown = Vec::with_capacity(range.len());
        let mut keys = Vec::new();
        for r in range {
            let mut row = to_row(&view.rows[r], r, Some(r) == playing);
            if let Some(a) = view.rows[r].album {
                // Pictures are made at the size they're shown, in real pixels.
                // DJ pictures are round, covers have 6 px corners, as in 2.x.
                let (size, radius) = if view.rows[r].kind == RowKind::Dj {
                    (rows::DJ_PICTURE_SIZE, rows::DJ_PICTURE_SIZE / 2.0)
                } else {
                    (rows::COVER_SIZE, 6.0)
                };
                let key = &view.lib.albums[a].sample_path;
                keys.push(key.clone());
                match self.covers.get(key) {
                    Some(Some(art)) => (row.art, row.has_art) = (art.clone(), true),
                    Some(None) => {}
                    None => {
                        if self.pending.insert(key.clone()) {
                            let px = (size * scale).round() as u32;
                            self.loader.request(key.clone(), px, radius * scale);
                        }
                    }
                }
            }
            shown.push(row);
        }
        self.model.set_vec(shown);
        // Let pictures scrolled far away go, as 2.x does, so memory doesn't grow with the library.
        if self.covers.len() > 40 {
            self.covers.retain(|k, _| keys.contains(k));
        }
    }

    /// Takes in the pictures the loader has finished and shows them.
    pub fn covers_ready(&mut self, ui: &AppWindow) {
        let done = std::mem::take(&mut *self.loader.done.lock().unwrap_or_else(|e| e.into_inner()));
        for (key, cover) in done {
            self.pending.remove(&key);
            self.covers.insert(key, cover.map(slint::Image::from_rgba8));
        }
        self.refresh_view(ui);
    }

    fn set_playing(&mut self, ui: &AppWindow, playing: Option<(usize, usize)>) {
        self.playing = playing;
        self.refresh_view(ui);
        let Some((v, i)) = playing else {
            ui.set_track_title("Nothing playing".into());
            ui.set_track_detail("".into());
            self.show_state(ui);
            return;
        };
        let view = &self.views[v];
        let t = &view.lib.tracks[i];
        // Music: artist and album. Mixes: artist and the DJ they're filed under.
        let from = if view.kind == Kind::Mixes {
            &t.collection
        } else {
            &view.lib.albums[t.album].title
        };
        let format = self.engine.format().map(|(codec, freq, chans)| {
            let layout = match chans {
                1 => "mono".to_string(),
                2 => "stereo".to_string(),
                n => format!("{n} channels"),
            };
            format!(
                "  ·  {codec}  ·  {:.1} kHz  ·  {layout}",
                f64::from(freq) / 1000.0
            )
        });
        ui.set_track_title(t.title.as_str().into());
        ui.set_track_detail(
            format!("{}  ·  {from}{}", t.artist, format.unwrap_or_default()).into(),
        );
        ui.set_length(self.engine.length() as f32);
        self.show_state(ui);
    }

    pub fn show_state(&self, ui: &AppWindow) {
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
            format!("{} / {}", rows::clock(pos), rows::clock(len))
        } else {
            String::new()
        };
        ui.set_time_text(time.into());
    }

    /// Runs every few milliseconds: finishes fades, follows the gapless hand-over, notices the end.
    pub fn tick(&mut self, ui: &AppWindow) {
        if self.engine.tick() {
            // The gapless hand-over happened: the queue moves on to what was lined up.
            let next = self
                .playing
                .and_then(|(v, _)| self.queue.next(false).map(|t| (v, t)));
            self.set_playing(ui, next);
            self.queue_next();
        } else if self.engine.has_ended() {
            // The last track, or the next one couldn't be opened: try what's after it.
            match self.playing.zip(self.queue.next(false)) {
                Some(((v, _), _)) => self.play_current(ui, v),
                None => {
                    self.engine.stop();
                    self.set_playing(ui, None);
                }
            }
        }
        self.show_state(ui);
    }

    pub fn toggle_play(&mut self, ui: &AppWindow) {
        if self.engine.is_playing() {
            self.engine.pause();
        } else if self.engine.is_loaded() {
            self.engine.resume();
        } else if !self.view().lib.tracks.is_empty() {
            // Nothing loaded: play the selected track on screen, or the first.
            let from = self.view().track_at(ui.get_selected()).unwrap_or(0);
            self.play_at(ui, self.tab, from);
        }
        self.show_state(ui);
    }

    pub fn next(&mut self, ui: &AppWindow) {
        if let Some((v, _)) = self.playing
            && self.queue.next(true).is_some()
        {
            self.play_current(ui, v);
        }
    }

    /// Back a track, or to the start of this one when it's the first.
    pub fn previous(&mut self, ui: &AppWindow) {
        let Some((v, i)) = self.playing else { return };
        if self.queue.previous() == Some(i) {
            self.engine.seek(0.0);
        } else {
            self.play_current(ui, v);
        }
    }

    fn save(&self, key: &str, value: &str) {
        // A setting that can't be saved just isn't remembered next time.
        if let Some(db) = &self.db {
            let _ = db.set_setting(key, value);
        }
    }

    /// Shows the shuffle, repeat and volume settings (read at startup).
    pub fn show_settings(&self, ui: &AppWindow) {
        ui.set_shuffle(self.queue.shuffle);
        ui.set_repeat(match self.queue.repeat {
            Repeat::Off => 0,
            Repeat::All => 1,
            Repeat::One => 2,
        });
    }

    pub fn toggle_shuffle(&mut self, ui: &AppWindow) {
        self.queue.set_shuffle(!self.queue.shuffle);
        self.save("shuffle", if self.queue.shuffle { "1" } else { "0" });
        self.show_settings(ui);
        self.queue_next();
    }

    pub fn cycle_repeat(&mut self, ui: &AppWindow) {
        self.queue.repeat = self.queue.repeat.cycled();
        self.save("repeat", self.queue.repeat.name());
        self.show_settings(ui);
        self.queue_next();
    }

    /// Saved when Melora closes rather than on every step of the slider (as 2.x does).
    pub fn save_volume(&self, ui: &AppWindow) {
        self.save("volume", &format!("{}", ui.get_volume() / 100.0));
    }

    pub fn move_selection(&mut self, ui: &AppWindow, steps: i32) {
        let view = self.view();
        let from = usize::try_from(ui.get_selected()).ok();
        if let Some(r) = rows::step(&view.rows, from, steps) {
            ui.set_selected(r as i32);
            ui.set_list_scroll(rows::scroll_to(
                &view.rows,
                r,
                ui.get_list_scroll(),
                ui.get_list_view_height(),
            ));
            self.refresh_view(ui);
        }
    }

    pub fn activate(&mut self, ui: &AppWindow, row: i32) {
        if let Some(i) = self.view().track_at(row) {
            self.play_at(ui, self.tab, i);
        }
    }
}
