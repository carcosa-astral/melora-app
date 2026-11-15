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
mod player;
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
use player::{Player, View};
use slint::{ComponentHandle, Timer, TimerMode, VecModel};

slint::include_modules!();

/// The libraries in tab order.
const KINDS: [Kind; 2] = [Kind::Music, Kind::Mixes];

/// Runs library scans on a worker thread, one at a time; a scan asked for while one runs
/// happens straight after it. The result waits in `ready` for the UI thread.
#[derive(Default)]
struct Scanner {
    busy: AtomicBool,
    again: AtomicBool,
    ready: Mutex<Option<Result<[Library; 2], String>>>,
}

/// Loads one library from the database. Mixes are grouped by the mixes folder they're under.
fn load(db: &Db, kind: Kind, base: &str) -> rusqlite::Result<Library> {
    let roots = if kind == Kind::Mixes {
        db.folders(kind)?
    } else {
        Vec::new()
    };
    Ok(library::load(&db.tracks(kind)?, kind, &roots, base))
}

/// Scans both libraries, each leaving out what's under the other's deeper folders.
fn scan_once(db_file: &Path, base: &str) -> Result<[Library; 2], String> {
    let run = || -> rusqlite::Result<[Library; 2]> {
        let mut db = Db::open(db_file, base)?;
        for kind in KINDS {
            let other = if kind == Kind::Music {
                Kind::Mixes
            } else {
                Kind::Music
            };
            let other_folders = db.folders(other)?;
            library::scan(&mut db, kind, &other_folders, base)?;
        }
        Ok([load(&db, Kind::Music, base)?, load(&db, Kind::Mixes, base)?])
    };
    run().map_err(|e| e.to_string())
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
        views: KINDS.map(View::new),
        tab: 0,
        playing: None,
        model,
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
            for (i, kind) in KINDS.into_iter().enumerate() {
                if let Ok(lib) = load(&db, kind, &base) {
                    player.borrow_mut().set_library(&ui, i, lib);
                }
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
    on!(on_move_selection, |p, ui, steps| p
        .move_selection(&ui, steps));
    on!(on_covers_ready, |p, ui| p.covers_ready(&ui));
    on!(
        on_show_tab,
        |p, ui, tab| if let Ok(t) = usize::try_from(tab) {
            p.show_tab(&ui, t)
        }
    );

    // Scrolling and resizing: show the rows now in view. Skipped while the player is busy, as it
    // refreshes the view itself after anything it scrolls.
    let (weak, p) = (ui.as_weak(), player.clone());
    ui.on_list_view_changed(move || {
        if let (Some(ui), Ok(mut p)) = (weak.upgrade(), p.try_borrow_mut()) {
            p.refresh_view(&ui);
        }
    });

    let s = scanner.clone();
    on!(on_library_ready, |p, ui| {
        match s.ready.lock().unwrap_or_else(|e| e.into_inner()).take() {
            Some(Ok(libs)) => {
                for (i, lib) in libs.into_iter().enumerate() {
                    p.set_library(&ui, i, lib);
                }
            }
            Some(Err(e)) => ui.set_summary(format!("Couldn't scan the library ({e})").into()),
            None => {}
        }
    });

    let (weak, p, s, file, b) = (
        ui.as_weak(),
        player.clone(),
        scanner.clone(),
        db_file.clone(),
        base.clone(),
    );
    ui.on_add_folder(move || {
        let (Some(ui), Some(db)) = (weak.upgrade(), db.as_ref()) else {
            return;
        };
        let kind = KINDS[p.borrow().tab];
        let title = if kind == Kind::Music {
            "Add a music folder"
        } else {
            "Add a mixes folder"
        };
        let Some(dir) = rfd::FileDialog::new().set_title(title).pick_folder() else {
            return;
        };
        match db.add_folder(kind, &dir.to_string_lossy()) {
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
