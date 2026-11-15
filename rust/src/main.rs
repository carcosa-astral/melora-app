// Release builds have no console window.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod backdrop;
mod bass;
mod covers;
mod db;
mod engine;
mod folder;
mod library;
mod paths;
mod rows;

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use db::{Db, Kind};
use engine::Engine;
use library::Library;
use rows::ListRow;
use slint::{ComponentHandle, Timer, TimerMode, VecModel};

slint::include_modules!();

/// What's in the library and what's playing. Owned by the UI thread.
struct Player {
    engine: Engine,
    lib: Library,
    rows: Vec<ListRow>,
    /// Only the rows in view (see `refresh_view`).
    model: Rc<VecModel<Row>>,
    /// The list row of each track.
    track_rows: Vec<usize>,
    /// The track playing (or paused), as an index into `lib.tracks`.
    index: Option<usize>,
    /// Covers for albums near the view, by the album's sample track (None: it has none).
    covers: HashMap<String, Option<slint::Image>>,
    /// Covers asked for and not back yet.
    pending: HashSet<String>,
    loader: covers::Loader,
}

fn to_row(r: &ListRow, index: usize, playing: bool) -> Row {
    Row {
        index: index as i32,
        kind: match r.kind {
            rows::RowKind::Album => 0,
            rows::RowKind::Disc => 1,
            rows::RowKind::Track => 2,
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
    /// Shows a freshly loaded library, keeping what's playing and what's selected.
    fn set_library(&mut self, ui: &AppWindow, lib: Library) {
        let path_of = |lib: &Library, i: usize| lib.tracks.get(i).map(|t| t.path.clone());
        let playing = self.index.and_then(|i| path_of(&self.lib, i));
        let selected = usize::try_from(ui.get_selected())
            .ok()
            .and_then(|r| self.rows.get(r))
            .and_then(|r| r.track)
            .and_then(|i| path_of(&self.lib, i));

        self.lib = lib;
        self.rows = rows::build(&self.lib);
        self.track_rows = self
            .rows
            .iter()
            .enumerate()
            .filter_map(|(r, row)| row.track.map(|_| r))
            .collect();
        let find = |path: Option<String>| {
            path.and_then(|p| self.lib.tracks.iter().position(|t| t.path == p))
        };
        self.index = find(playing);
        let selected = find(selected).map(|i| self.track_rows[i]);

        ui.set_row_count(self.rows.len() as i32);
        ui.set_list_height(rows::total_height(&self.rows));
        ui.set_selected(selected.map_or(-1, |r| r as i32));
        self.refresh_view(ui);
        ui.set_summary(rows::summary(&self.lib).into());
        self.queue_next(); // the track after the playing one may have changed
    }

    /// Plays the track at `i`, skipping any that can't be opened.
    fn play_at(&mut self, ui: &AppWindow, i: usize) {
        for j in i..self.lib.tracks.len() {
            if self.engine.play(Path::new(&self.lib.tracks[j].path)) {
                self.set_index(ui, Some(j));
                self.queue_next();
                return;
            }
        }
        self.engine.stop();
        self.set_index(ui, None);
        ui.set_track_detail("Melora can't play the rest of the list".into());
    }

    /// Lines up the following track so it starts without a gap. One that can't be opened is
    /// left out; the track after it is tried when this one ends.
    fn queue_next(&mut self) {
        if !self.engine.is_loaded() {
            return;
        }
        let next = self.index.and_then(|i| self.lib.tracks.get(i + 1));
        self.engine.set_next(next.map(|t| Path::new(&t.path)));
    }

    /// Hands the list the rows now in view, with the playing one marked.
    fn refresh_view(&mut self, ui: &AppWindow) {
        let range = rows::in_view(&self.rows, ui.get_list_scroll(), ui.get_list_view_height());
        let playing = self.index.and_then(|i| self.track_rows.get(i).copied());
        // Covers are made at the size they're shown, in real pixels.
        let px = (rows::COVER_SIZE * ui.window().scale_factor()).round() as u32;
        let mut shown = Vec::with_capacity(range.len());
        let mut keys = Vec::new();
        for r in range {
            let mut row = to_row(&self.rows[r], r, Some(r) == playing);
            if let Some(a) = self.rows[r].album {
                let key = &self.lib.albums[a].sample_path;
                keys.push(key.clone());
                match self.covers.get(key) {
                    Some(Some(art)) => (row.art, row.has_art) = (art.clone(), true),
                    Some(None) => {}
                    None => {
                        if self.pending.insert(key.clone()) {
                            self.loader.request(key.clone(), px);
                        }
                    }
                }
            }
            shown.push(row);
        }
        self.model.set_vec(shown);
        // Let covers scrolled far away go, as 2.x does, so memory doesn't grow with the library.
        if self.covers.len() > 40 {
            self.covers.retain(|k, _| keys.contains(k));
        }
    }

    /// Takes in the covers the loader has finished and shows them.
    fn covers_ready(&mut self, ui: &AppWindow) {
        let done = std::mem::take(&mut *self.loader.done.lock().unwrap_or_else(|e| e.into_inner()));
        for (key, cover) in done {
            self.pending.remove(&key);
            self.covers.insert(key, cover.map(slint::Image::from_rgba8));
        }
        self.refresh_view(ui);
    }

    fn set_index(&mut self, ui: &AppWindow, index: Option<usize>) {
        self.index = index;
        self.refresh_view(ui);
        let Some(i) = index else {
            ui.set_track_title("Nothing playing".into());
            ui.set_track_detail("".into());
            self.show_state(ui);
            return;
        };
        let t = &self.lib.tracks[i];
        let album = &self.lib.albums[t.album];
        ui.set_track_title(t.title.as_str().into());
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
        ui.set_track_detail(
            format!(
                "{}  ·  {}{}",
                t.artist,
                album.title,
                format.unwrap_or_default()
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
            format!("{} / {}", rows::clock(pos), rows::clock(len))
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
            match self
                .index
                .map(|i| i + 1)
                .filter(|&i| i < self.lib.tracks.len())
            {
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
        } else {
            // Nothing loaded: play the selected track, or the first.
            let from = usize::try_from(ui.get_selected())
                .ok()
                .and_then(|r| self.rows.get(r))
                .and_then(|r| r.track);
            if !self.lib.tracks.is_empty() {
                self.play_at(ui, from.unwrap_or(0));
            }
        }
        self.show_state(ui);
    }

    fn next(&mut self, ui: &AppWindow) {
        if let Some(i) = self.index.filter(|i| i + 1 < self.lib.tracks.len()) {
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

    fn move_selection(&mut self, ui: &AppWindow, steps: i32) {
        let from = usize::try_from(ui.get_selected()).ok();
        if let Some(r) = rows::step(&self.rows, from, steps) {
            ui.set_selected(r as i32);
            let top = rows::scroll_to(
                &self.rows,
                r,
                ui.get_list_scroll(),
                ui.get_list_view_height(),
            );
            ui.set_list_scroll(top);
            self.refresh_view(ui);
        }
    }

    fn activate(&mut self, ui: &AppWindow, row: i32) {
        if let Some(i) = usize::try_from(row)
            .ok()
            .and_then(|r| self.rows.get(r))
            .and_then(|r| r.track)
        {
            self.play_at(ui, i);
        }
    }
}

/// Runs library scans on a worker thread, one at a time; a scan asked for while one runs
/// happens straight after it. The result waits in `ready` for the UI thread.
#[derive(Default)]
struct Scanner {
    busy: AtomicBool,
    again: AtomicBool,
    ready: Mutex<Option<Result<Library, String>>>,
}

fn scan_once(db_file: &Path, base: &str) -> Result<Library, String> {
    let mut db = Db::open(db_file, base).map_err(|e| e.to_string())?;
    let mixes = db.folders(Kind::Mixes).map_err(|e| e.to_string())?;
    library::scan(&mut db, Kind::Music, &mixes, base).map_err(|e| e.to_string())?;
    let rows = db.tracks(Kind::Music).map_err(|e| e.to_string())?;
    Ok(library::load(&rows, Kind::Music, &[], base))
}

fn start_scan(scanner: &Arc<Scanner>, ui: &AppWindow, db_file: PathBuf, base: String) {
    if scanner.busy.swap(true, Ordering::SeqCst) {
        scanner.again.store(true, Ordering::SeqCst);
        return;
    }
    ui.set_summary("Scanning…".into());
    let (scanner, weak) = (scanner.clone(), ui.as_weak());
    std::thread::spawn(move || {
        loop {
            let result = scan_once(&db_file, &base);
            *scanner.ready.lock().unwrap_or_else(|e| e.into_inner()) = Some(result);
            let _ = weak.upgrade_in_event_loop(|ui| ui.invoke_library_ready());
            scanner.busy.store(false, Ordering::SeqCst);
            // Go again only if asked meanwhile, and nobody else has started one since.
            if !scanner.again.swap(false, Ordering::SeqCst)
                || scanner.busy.swap(true, Ordering::SeqCst)
            {
                break;
            }
        }
    });
}

fn main() -> Result<(), slint::PlatformError> {
    let base = paths::base_dir().to_string_lossy().into_owned();
    let db_file = paths::data_dir().join("melora.db");
    let ui = AppWindow::new()?;
    let model = Rc::new(VecModel::default());
    ui.set_rows(model.clone().into());

    let player = Rc::new(RefCell::new(Player {
        engine: Engine::new(),
        lib: Library::default(),
        rows: Vec::new(),
        model,
        track_rows: Vec::new(),
        index: None,
        covers: HashMap::new(),
        pending: HashSet::new(),
        loader: covers::Loader::new({
            let weak = ui.as_weak();
            move || {
                let _ = weak.upgrade_in_event_loop(|ui| ui.invoke_covers_ready());
            }
        }),
    }));

    if let Err(e) = bass::init(-1, &paths::base_dir()) {
        ui.set_track_detail(format!("Sound couldn't start: {e}").into());
    }

    // What's already in the database shows at once; the scan then catches up with the disk.
    let db = match Db::open(&db_file, &base) {
        Ok(db) => {
            if let Ok(rows) = db.tracks(Kind::Music) {
                player
                    .borrow_mut()
                    .set_library(&ui, library::load(&rows, Kind::Music, &[], &base));
            }
            Some(db)
        }
        Err(e) => {
            ui.set_summary(format!("Couldn't open the library ({e})").into());
            None
        }
    };
    let scanner = Arc::new(Scanner::default());
    if db.is_some() {
        start_scan(&scanner, &ui, db_file.clone(), base.clone());
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

    on!(on_toggle_play, |p, ui| p.toggle_play(&ui));
    on!(on_next, |p, ui| p.next(&ui));
    on!(on_previous, |p, ui| p.previous(&ui));
    on!(on_seek, |p, ui, seconds| {
        p.engine.seek(seconds.into());
        p.show_state(&ui)
    });
    on!(on_activate, |p, ui, row| p.activate(&ui, row));
    on!(on_covers_ready, |p, ui| p.covers_ready(&ui));

    // Scrolling and resizing: show the rows now in view. Skipped while the player is busy, as it
    // refreshes the view itself after anything it scrolls.
    let (weak, p) = (ui.as_weak(), player.clone());
    ui.on_list_view_changed(move || {
        if let (Some(ui), Ok(mut p)) = (weak.upgrade(), p.try_borrow_mut()) {
            p.refresh_view(&ui);
        }
    });
    on!(on_move_selection, |p, ui, steps| p
        .move_selection(&ui, steps));

    let s = scanner.clone();
    on!(on_library_ready, |p, ui| {
        match s.ready.lock().unwrap_or_else(|e| e.into_inner()).take() {
            Some(Ok(lib)) => p.set_library(&ui, lib),
            Some(Err(e)) => ui.set_summary(format!("Couldn't scan the library ({e})").into()),
            None => {}
        }
    });

    let (weak, s, file, b) = (ui.as_weak(), scanner.clone(), db_file.clone(), base.clone());
    ui.on_add_folder(move || {
        let (Some(ui), Some(db)) = (weak.upgrade(), db.as_ref()) else {
            return;
        };
        let Some(dir) = rfd::FileDialog::new()
            .set_title("Add a music folder")
            .pick_folder()
        else {
            return;
        };
        match db.add_folder(Kind::Music, &dir.to_string_lossy()) {
            Ok(()) => start_scan(&s, &ui, file.clone(), b.clone()),
            Err(e) => ui.set_summary(format!("Couldn't add the folder ({e})").into()),
        }
    });

    // Short enough that a pause lands right after its 150 ms fade.
    let ticker = Timer::default();
    let (weak, p) = (ui.as_weak(), player.clone());
    ticker.start(TimerMode::Repeated, Duration::from_millis(40), move || {
        if let Some(ui) = weak.upgrade() {
            p.borrow_mut().tick(&ui);
        }
    });

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
