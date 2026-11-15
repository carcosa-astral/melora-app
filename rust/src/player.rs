//! The libraries (Music and Mixes) and the radio stations, which one is on screen, and what's
//! playing. Owned by the UI thread.

use std::collections::{HashMap, HashSet};
use std::path::Path;
use std::rc::Rc;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use slint::{ComponentHandle, VecModel};

use crate::bass::{self, Handle};
use crate::db::{Db, Kind};
use crate::discord::{Discord, Presence, Source};
use crate::engine::Engine;
use crate::library::Library;
use crate::media::{self, Cover, Media};
use crate::online::Query;
use crate::queue::{Info, Queue, Repeat};
use crate::rows::{self, ListRow, RowKind};
use crate::scrobble::{self, Love, Scrobble};
use crate::scrobbler::{self, Report, Scrobbler};
use crate::tray::Tray;
use crate::{AppWindow, Row, covers, library, logos, online, radio};

/// The Radio tab's place among the views.
pub const RADIO: usize = 2;

/// A saved radio station.
pub struct Station {
    pub id: i64,
    pub name: String,
    pub url: String,
}

/// A connection finished in the background: which attempt it was, and the stream or why not.
type Connected = Option<(u64, Result<Handle, String>)>;
/// A check of an address finished: which check, and the name it gives itself and its format.
type Checked = Option<(u64, Result<(Option<String>, String), String>)>;

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
    pub views: [View; 3],
    /// The view on screen: 0 Music, 1 Mixes, 2 Radio.
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
    pub stations: Vec<Station>,
    /// What the playing station says it's playing, or a status ("Connecting…").
    pub station_now: Option<String>,
    /// Bumped by each connection attempt, so a slow one that's been overtaken is dropped.
    pub connect_attempt: u64,
    pub connected: Arc<Mutex<Connected>>,
    pub check_attempt: u64,
    pub checked: Arc<Mutex<Checked>>,
    /// The station being edited (None: adding a new one).
    pub editing: Option<i64>,
    /// The station waiting for the delete to be confirmed.
    pub deleting: Option<usize>,
    /// Station logos by station address (None: it has none). Few stations, so all are kept.
    pub logos: HashMap<String, Option<slint::Image>>,
    pub logos_pending: HashSet<String>,
    /// None when looking things up online is turned off.
    pub logo_loader: Option<logos::Loader>,
    /// The notification-area icon (None if Windows refused it).
    pub tray: Option<Tray>,
    /// Minimizing hides Melora to the tray (2.x's setting).
    pub minimize_to_tray: bool,
    /// The Windows media overlay and media keys (None until the window exists, or if refused).
    pub media: Option<Media>,
    /// What the overlay was last told: None stopped, else playing or paused.
    pub media_state: Option<bool>,
    /// Sends plays and loves to Last.fm and ListenBrainz (None if the database couldn't open).
    pub scrobbler: Option<Scrobbler>,
    /// The track being listened to, and how long it's actually been heard, for scrobbling.
    pub listen: Option<Listen>,
    pub last_tick: Instant,
    /// "Listening to" on Discord (None if the database couldn't open).
    pub discord: Option<Discord>,
    /// When the playing station was tuned in to (Unix ms), for Discord's time.
    pub tuned_in_at: i64,
}

/// A play being listened to: it's scrobbled once enough has been heard (see scrobble.rs).
pub struct Listen {
    play: Scrobble,
    heard: Duration,
    queued: bool,
}

fn take<T>(slot: &Mutex<Option<T>>) -> Option<T> {
    slot.lock().unwrap_or_else(|e| e.into_inner()).take()
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
            RowKind::Station => 5,
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
        let summary = if view.kind == Kind::Radio {
            rows::station_summary(self.stations.len())
        } else {
            rows::summary(&view.lib, view.kind)
        };
        ui.set_summary(summary.into());
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
        ui.set_now_playing("Melora can't play the rest of the list".into());
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
            if view.rows[r].kind == RowKind::Station
                && let Some(station) = view.rows[r].track.and_then(|i| self.stations.get(i))
            {
                match self.logos.get(&station.url) {
                    Some(Some(logo)) => (row.art, row.has_art) = (logo.clone(), true),
                    Some(None) => {}
                    None => {
                        if let Some(loader) = &self.logo_loader
                            && self.logos_pending.insert(station.url.clone())
                        {
                            let px = (rows::LOGO_SIZE * scale).round() as u32;
                            loader.request(&station.url, &station.name, px);
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

    /// Takes in the station logos found online and shows them.
    pub fn logos_ready(&mut self, ui: &AppWindow) {
        let Some(loader) = &self.logo_loader else {
            return;
        };
        let done = std::mem::take(&mut *loader.done.lock().unwrap_or_else(|e| e.into_inner()));
        for (url, logo) in done {
            self.logos_pending.remove(&url);
            self.logos.insert(url, logo.map(slint::Image::from_rgba8));
        }
        self.refresh_view(ui);
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
        // The status bar and the window title say what's playing, as in 2.x.
        let now = match playing {
            None => String::new(),
            Some((RADIO, i)) => {
                // Radio: the song the station says it's playing, else its name and how it's going.
                let name = self.stations.get(i).map_or("", |s| s.name.as_str());
                match (&self.station_now, self.engine.is_live()) {
                    (Some(song), true) => song.clone(),
                    (Some(status), false) => format!("{name}  ·  {status}"),
                    // Playing, but the station hasn't said what: "Live", as in 2.x.
                    (None, true) => format!("{name}  ·  Live"),
                    (None, false) => name.to_string(),
                }
            }
            Some((v, i)) => {
                let t = &self.views[v].lib.tracks[i];
                format!("{} – {}", t.artist, t.title)
            }
        };
        let format = self
            .engine
            .format()
            .filter(|_| self.engine.is_loaded())
            .map(|(codec, freq, chans, kbps)| {
                let layout = match chans {
                    1 => "mono".to_string(),
                    2 => "stereo".to_string(),
                    n => format!("{n} ch"),
                };
                let parts = [
                    (!codec.is_empty()).then(|| codec.to_string()),
                    (freq > 0).then(|| format!("{} kHz", f64::from(freq) / 1000.0)),
                    (kbps > 0).then(|| format!("{} kbps", rows::count(kbps as usize))),
                    Some(layout),
                ];
                parts
                    .into_iter()
                    .flatten()
                    .collect::<Vec<_>>()
                    .join("  ·  ")
            });
        ui.set_window_title(
            if now.is_empty() {
                "Melora".to_string()
            } else {
                format!("{now}  ·  Melora")
            }
            .into(),
        );
        if let Some(tray) = &self.tray {
            tray.set_now_playing(&now);
        }
        ui.set_now_playing(now.into());
        self.update_media();
        self.update_discord();
        self.track_started(ui);
        ui.set_format_text(format.unwrap_or_default().into());
        ui.set_length(self.engine.length() as f32);
        self.show_state(ui);
    }

    /// Tells the overlay what's playing, with its cover (an album's, or a station's logo).
    pub fn update_media(&mut self) {
        if self.media.is_none() {
            return;
        }
        let shown = match self.playing {
            None => None,
            Some((RADIO, i)) => self.stations.get(i).map(|st| {
                let now = self
                    .engine
                    .is_live()
                    .then_some(self.station_now.as_deref())
                    .flatten();
                let (artist, title) = media::split_station_title(now, &st.name);
                let logo = self
                    .logo_loader
                    .as_ref()
                    .and(self.db.as_ref())
                    .and_then(|d| d.cover(&logos::key(&st.url)))
                    .and_then(|(url, _)| url);
                (
                    title,
                    artist,
                    st.name.clone(),
                    logo.map_or(Cover::None, Cover::Url),
                    0.0,
                )
            }),
            Some((v, i)) => {
                let view = &self.views[v];
                view.lib.tracks.get(i).map(|t| {
                    let album = &view.lib.albums[t.album];
                    let from = if view.kind == Kind::Mixes {
                        t.collection.clone()
                    } else {
                        album.title.clone()
                    };
                    // shortcut: the cover is read on the UI thread (a folder picture or a tag);
                    // move it to the cover loader if a slow drive makes track changes stutter.
                    let cover = covers::find(Path::new(&album.sample_path))
                        .map_or(Cover::None, Cover::Bytes);
                    (
                        t.title.clone(),
                        t.artist.clone(),
                        from,
                        cover,
                        self.engine.length(),
                    )
                })
            }
        };
        let Some(media) = &mut self.media else { return };
        match shown {
            Some((title, artist, album, cover, length)) => {
                media.show(&title, &artist, &album, cover, length)
            }
            None => {
                media.set_state(None);
                self.media_state = None;
            }
        }
    }

    /// Tells Discord what's playing, as 2.x does. Paused or stopped shows nothing.
    pub fn update_discord(&self) {
        let Some(d) = &self.discord else { return };
        let now_ms = online::now() * 1000;
        let shown = match self.playing.filter(|_| self.engine.is_playing()) {
            None => None,
            Some((RADIO, i)) => self.stations.get(i).map(|st| {
                let song = self
                    .station_now
                    .as_deref()
                    .filter(|_| self.engine.is_live());
                let (artist, title) = match song.map(|s| {
                    s.split_once(" - ")
                        .map_or((None, s), |(a, t)| (Some(a.trim()), t.trim()))
                }) {
                    Some((a, t)) => (a, Some(t)),
                    None => (None, None),
                };
                Presence {
                    source: Source::Radio,
                    details: title.unwrap_or(&st.name).to_string(),
                    state: artist.unwrap_or(&st.name).to_string(),
                    large_text: Some(st.name.clone()),
                    cover: artist.zip(title).map(|(a, t)| Query::Song {
                        artist: a.into(),
                        title: t.into(),
                    }),
                    start: Some(self.tuned_in_at),
                    end: None,
                }
            }),
            Some((v, i)) => self.views[v].lib.tracks.get(i).map(|t| {
                let start = now_ms - (self.engine.position() * 1000.0) as i64;
                let end = start + (self.engine.length() * 1000.0) as i64;
                if self.views[v].kind == Kind::Mixes {
                    Presence {
                        source: Source::Mixes,
                        details: t.title.clone(),
                        state: t.artist.clone(),
                        // The DJ's folder, unless it just repeats the artist line.
                        large_text: (!t.collection.eq_ignore_ascii_case(&t.artist))
                            .then(|| t.collection.clone()),
                        cover: Some(Query::Artist(t.collection.clone())),
                        start: Some(start),
                        end: Some(end),
                    }
                } else {
                    Presence {
                        source: Source::Music,
                        details: t.title.clone(),
                        state: t.artist.clone(),
                        large_text: t.album_tag.clone(),
                        cover: t.album_tag.clone().map(|album| Query::Album {
                            artist: t
                                .album_artist_tag
                                .clone()
                                .unwrap_or_else(|| t.artist.clone()),
                            album,
                            sample: Some(t.path.clone()),
                        }),
                        start: Some(start),
                        end: Some(end),
                    }
                }
            }),
        };
        d.show(shown);
    }

    /// Settings' Discord section, from 2.x's settings.
    pub fn show_discord(&self, ui: &AppWindow) {
        let Some(db) = &self.db else { return };
        ui.set_discord_enabled(db.setting("discord.enabled").as_deref() == Some("1"));
        ui.set_discord_app_id(db.setting("discord.appId").unwrap_or_default().into());
        ui.set_discord_mixes(db.setting("discord.mixes").as_deref() != Some("0"));
        ui.set_discord_radio(db.setting("discord.radio").as_deref() != Some("0"));
        if let Some(d) = &self.discord {
            ui.set_discord_status(
                d.status
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .clone()
                    .into(),
            );
        }
    }

    /// A Discord setting changed: save it (an ID keeps only its digits) and have Discord catch up.
    pub fn set_discord(&self, key: &str, value: &str) {
        let value = if key == "discord.appId" {
            value.chars().filter(char::is_ascii_digit).collect()
        } else {
            value.to_string()
        };
        self.save(key, &value);
        if let Some(d) = &self.discord {
            d.settings_changed();
        }
        self.update_discord();
    }

    /// Tells the overlay the position after a jump.
    pub fn media_moved(&mut self) {
        self.update_discord();
        let state = (self.engine.is_playing(), self.engine.position());
        if let Some(media) = &mut self.media {
            media.set_state(self.engine.is_loaded().then_some(state));
        }
    }

    pub fn show_state(&mut self, ui: &AppWindow) {
        // The overlay hears about playing and pausing as it happens; it moves the time on itself.
        let state = self.engine.is_loaded().then(|| self.engine.is_playing());
        if state != self.media_state {
            self.media_state = state;
            let pos = self.engine.position();
            if let Some(media) = &mut self.media {
                media.set_state(state.map(|p| (p, pos)));
            }
            self.update_discord(); // paused shows nothing; playing again starts the bar from here
        }
        if let Some(tray) = &mut self.tray {
            tray.set_playing(self.engine.is_playing(), self.engine.is_live());
        }
        let len = self.engine.length();
        ui.set_loaded(self.engine.is_loaded());
        ui.set_playing(self.engine.is_playing());
        ui.set_live(self.engine.is_live());
        // While the seek bar is dragged it leads, and the time shows where it would land.
        let pos = if ui.get_seeking() {
            f64::from(ui.get_position())
        } else {
            let pos = self.engine.position();
            ui.set_position(pos as f32);
            pos
        };
        // Radio has no length: just the time listened.
        let time = match (self.engine.is_loaded(), self.engine.is_live()) {
            (true, false) => format!("{} / {}", rows::clock(pos), rows::clock(len)),
            (true, true) => rows::clock(pos),
            (false, _) => String::new(),
        };
        ui.set_time_text(time.into());
    }

    /// A track (not radio) started: it may be scrobbled, and the heart shows whether it's loved.
    fn track_started(&mut self, ui: &AppWindow) {
        self.listen = None;
        let (Some(db), Some((v, i))) = (&self.db, self.playing.filter(|p| p.0 != RADIO)) else {
            ui.set_can_love(false);
            return;
        };
        let services = scrobbler::connected(db);
        let view = &self.views[v];
        let Some(t) = view.lib.tracks.get(i) else {
            return;
        };
        ui.set_can_love(!services.is_empty());
        ui.set_loved(db.loved(&t.artist, &t.title) == Some(true));
        if services.is_empty() {
            return;
        }
        if let Some(s) = &self.scrobbler {
            s.check_loved(&t.artist, &t.title);
        }
        if view.kind == Kind::Mixes && db.setting("scrobbleMixes").as_deref() == Some("0") {
            return;
        }
        let duration = if t.duration_ms > 0 {
            Duration::from_millis(t.duration_ms as u64)
        } else {
            Duration::from_secs_f64(self.engine.length().max(0.0))
        };
        if !scrobble::is_long_enough(duration) {
            return;
        }
        let started_at = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_or(0, |d| d.as_secs() as i64);
        let play = Scrobble {
            artist: t.artist.clone(),
            title: t.title.clone(),
            album: t.album_tag.clone(),
            album_artist: t.album_artist_tag.clone(),
            track_no: t.track_no,
            duration,
            started_at,
        };
        if let Some(s) = &self.scrobbler {
            s.now_playing(play.clone());
        }
        self.listen = Some(Listen {
            play,
            heard: Duration::ZERO,
            queued: false,
        });
    }

    /// Counts time actually heard (not paused, not skipped), and queues the play once it's due.
    fn count_listening(&mut self) {
        let now = Instant::now();
        // At most a second per tick, so a stall (a sleeping PC) doesn't count as listening.
        let step = (now - self.last_tick).min(Duration::from_secs(1));
        self.last_tick = now;
        if !self.engine.is_playing() {
            return;
        }
        let Some(listen) = self.listen.as_mut().filter(|l| !l.queued) else {
            return;
        };
        listen.heard += step;
        if !scrobble::is_due(listen.play.duration, listen.heard) {
            return;
        }
        listen.queued = true;
        let Some(db) = &self.db else { return };
        for service in scrobbler::connected(db) {
            let _ = db.add_scrobble(service, &listen.play);
        }
        if let Some(s) = &self.scrobbler {
            s.flush();
        }
    }

    /// The heart (or Ctrl+L): loves or un-loves the playing track, saved now and sent to every
    /// connected service (now, or once they can be reached).
    pub fn toggle_love(&mut self, ui: &AppWindow) {
        let Some((v, i)) = self.playing.filter(|p| p.0 != RADIO) else {
            return;
        };
        let Some(t) = self.views[v].lib.tracks.get(i) else {
            return;
        };
        let Some(db) = &mut self.db else { return };
        let services = scrobbler::connected(db);
        if services.is_empty() {
            return;
        }
        let loved = db.loved(&t.artist, &t.title) != Some(true);
        let love = Love {
            artist: t.artist.clone(),
            title: t.title.clone(),
            album: t.album_tag.clone(),
            mbid: library::recording_mbid(Path::new(&t.path)),
            loved,
        };
        if db.set_loved(&love, &services).is_ok() {
            ui.set_loved(loved);
            if let Some(s) = &self.scrobbler {
                s.flush();
            }
        }
    }

    /// News from the scrobbler: plays sent, a love checked on Last.fm, a step in connecting.
    pub fn scrobbler_report(&mut self, ui: &AppWindow) {
        let Some(s) = &self.scrobbler else { return };
        let reports = std::mem::take(&mut *s.reports.lock().unwrap_or_else(|e| e.into_inner()));
        for report in reports {
            match report {
                Report::Sent => {}
                Report::LovedChecked { artist, title } => {
                    let playing = self
                        .playing
                        .filter(|p| p.0 != RADIO)
                        .and_then(|(v, i)| self.views[v].lib.tracks.get(i));
                    if let (Some(t), Some(db)) = (playing, &self.db)
                        && t.artist == artist
                        && t.title == title
                    {
                        ui.set_loved(db.loved(&artist, &title) == Some(true));
                    }
                }
                Report::Connect {
                    service,
                    message,
                    done,
                } => {
                    let mut account = if service == scrobbler::LASTFM {
                        ui.get_lastfm()
                    } else {
                        ui.get_listenbrainz()
                    };
                    account.message = message.into();
                    account.busy = !done;
                    if service == scrobbler::LASTFM {
                        ui.set_lastfm(account)
                    } else {
                        ui.set_listenbrainz(account)
                    }
                }
            }
        }
        self.show_scrobbling(ui);
    }

    /// Fills in Settings' Scrobbling section from the saved logins, as 2.x shows them.
    pub fn show_scrobbling(&self, ui: &AppWindow) {
        let Some(db) = &self.db else { return };
        let pending = match db.count_scrobbles() {
            0 => String::new(),
            1 => "1 play is waiting to be sent.".into(),
            n => format!("{} plays are waiting to be sent.", rows::count(n)),
        };
        ui.set_pending(pending.into());
        ui.set_scrobble_mixes(db.setting("scrobbleMixes").as_deref() != Some("0"));
        let connected = scrobbler::connected(db);
        for (service, name, what) in [
            (scrobbler::LASTFM, "Last.fm", "login"),
            (scrobbler::LISTENBRAINZ, "ListenBrainz", "token"),
        ] {
            let on = connected.contains(&service);
            let user = db.setting(&format!("{service}.user")).unwrap_or_default();
            let status = match (
                on,
                db.setting(&format!("{service}.reconnect")).as_deref() == Some("1"),
            ) {
                (true, true) => format!(
                    "{name} stopped accepting the {what} for {user}. Disconnect and connect again."
                ),
                (true, false) => format!("Connected as {user}"),
                (false, _) => String::new(),
            };
            let mut account = if service == scrobbler::LASTFM {
                ui.get_lastfm()
            } else {
                ui.get_listenbrainz()
            };
            (account.connected, account.status) = (on, status.into());
            if service == scrobbler::LASTFM {
                ui.set_lastfm(account)
            } else {
                ui.set_listenbrainz(account)
            }
        }
    }

    pub fn set_scrobble_mixes(&self, on: bool) {
        self.save("scrobbleMixes", if on { "1" } else { "0" });
    }

    pub fn lastfm_connect(&self, ui: &AppWindow) {
        let (key, secret) = (
            ui.get_lastfm_key().trim().to_string(),
            ui.get_lastfm_secret().trim().to_string(),
        );
        let mut account = ui.get_lastfm();
        if key.is_empty() || secret.is_empty() {
            account.message = "Paste both the API key and the shared secret first.".into();
            ui.set_lastfm(account);
            return;
        }
        if let Some(s) = &self.scrobbler {
            (account.message, account.busy) = ("Contacting Last.fm…".into(), true);
            ui.set_lastfm(account);
            s.connect_lastfm(key, secret, crate::window::open_url);
        }
    }

    pub fn listenbrainz_connect(&self, ui: &AppWindow) {
        let token = ui.get_listenbrainz_token().trim().to_string();
        let mut account = ui.get_listenbrainz();
        if token.is_empty() {
            account.message = "Paste your user token first.".into();
            ui.set_listenbrainz(account);
            return;
        }
        if let Some(s) = &self.scrobbler {
            (account.message, account.busy) = ("Checking…".into(), true);
            ui.set_listenbrainz(account);
            s.connect_listenbrainz(token);
        }
    }

    /// Forgets a service's login (the key and secret stay, as in 2.x, for connecting again).
    pub fn disconnect(&mut self, ui: &AppWindow, service: &str) {
        let Some(db) = &self.db else { return };
        let keys: &[&str] = if service == scrobbler::LASTFM {
            &["lastfm.session", "lastfm.user"]
        } else {
            &["listenbrainz.token", "listenbrainz.user"]
        };
        for key in keys {
            let _ = db.delete_setting(key);
        }
        let _ = db.set_setting(&format!("{service}.reconnect"), "0");
        self.show_scrobbling(ui);
        self.track_started_heart(ui);
    }

    /// The heart after connecting or disconnecting, without starting the play again.
    pub fn track_started_heart(&self, ui: &AppWindow) {
        let connected = self
            .db
            .as_ref()
            .is_some_and(|d| !scrobbler::connected(d).is_empty());
        ui.set_can_love(connected && self.playing.is_some_and(|p| p.0 != RADIO));
    }

    /// Runs every few milliseconds: finishes fades, follows the gapless hand-over, notices the end.
    pub fn tick(&mut self, ui: &AppWindow) {
        self.count_listening();
        if self.playing.is_some_and(|p| p.0 == RADIO) {
            self.tick_radio(ui);
            self.show_state(ui);
            return;
        }
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
        let radio = self.playing.filter(|p| p.0 == RADIO);
        if self.engine.is_live() && self.engine.is_playing() {
            // Radio can't pause; it stops (fading out), and Play tunes in again.
            self.engine.fade_out_and_stop();
            self.station_now = None;
            self.after_station_change(ui);
        } else if self.engine.is_playing() {
            self.engine.pause();
        } else if self.engine.is_loaded() {
            self.engine.resume();
        } else if let Some((_, i)) = radio {
            self.connect(ui, i);
        } else if self.tab == RADIO && !self.stations.is_empty() {
            let from = self.view().track_at(ui.get_selected()).unwrap_or(0);
            self.connect(ui, from);
        } else if !self.view().lib.tracks.is_empty() {
            // Nothing loaded: play the selected track on screen, or the first.
            let from = self.view().track_at(ui.get_selected()).unwrap_or(0);
            self.play_at(ui, self.tab, from);
        }
        self.show_state(ui);
    }

    pub fn next(&mut self, ui: &AppWindow) {
        if let Some((v, _)) = self.playing.filter(|p| p.0 != RADIO)
            && self.queue.next(true).is_some()
        {
            self.play_current(ui, v);
        }
    }

    /// Back a track, or to the start of this one when it's the first.
    pub fn previous(&mut self, ui: &AppWindow) {
        let Some((v, i)) = self.playing.filter(|p| p.0 != RADIO) else {
            return;
        };
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

    /// Settings: minimizing hides to the tray, or not.
    pub fn set_minimize_to_tray(&mut self, on: bool) {
        self.minimize_to_tray = on;
        self.save("minimizeToTray", if on { "1" } else { "0" });
    }

    /// Settings: looking up logos (and later covers) online. Off also hides those already found,
    /// as in 2.x. `loader` makes a new logo loader when it's turned on.
    pub fn set_online_art(
        &mut self,
        ui: &AppWindow,
        on: bool,
        loader: impl FnOnce() -> logos::Loader,
    ) {
        self.save("onlineArt", if on { "1" } else { "0" });
        self.logos.clear();
        self.logos_pending.clear();
        self.logo_loader = on.then(loader);
        self.refresh_view(ui);
    }

    /// The first time Melora hides in the tray, a balloon says where it went (once ever, as 2.x).
    pub fn first_time_in_tray(&self) {
        let shown = self.db.as_ref().and_then(|d| d.setting("trayHintShown"));
        if shown.as_deref() == Some("1") {
            return;
        }
        if let Some(tray) = &self.tray
            && tray.show_balloon(
                "Melora is in the system tray",
                "Click the tray icon to bring it back.",
            )
        {
            self.save("trayHintShown", "1");
        }
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
        match self.view().track_at(row) {
            Some(i) if self.tab == RADIO => self.connect(ui, i),
            Some(i) => self.play_at(ui, self.tab, i),
            None => {}
        }
    }

    // ---- Radio ----

    /// Shows the stations as saved (at startup).
    pub fn set_stations(&mut self, ui: &AppWindow, stations: Vec<Station>) {
        self.stations = stations;
        self.rebuild_radio(ui);
    }

    /// Rebuilds the station rows (names, and what the playing one says) and shows them if open.
    fn rebuild_radio(&mut self, ui: &AppWindow) {
        let playing = self.playing.filter(|p| p.0 == RADIO).map(|p| p.1);
        let now = self.station_now.as_deref();
        let view = &mut self.views[RADIO];
        view.rows = rows::build_stations(self.stations.iter().enumerate().map(|(i, s)| {
            (
                s.name.as_str(),
                s.url.as_str(),
                if Some(i) == playing { now } else { None },
            )
        }));
        view.track_rows = (0..view.rows.len()).collect();
        if self.tab == RADIO {
            ui.set_row_count(view.rows.len() as i32);
            ui.set_list_height(rows::total_height(&view.rows));
            ui.set_summary(rows::station_summary(self.stations.len()).into());
            self.refresh_view(ui);
        }
    }

    /// The station rows and the now-playing line after the playing station changed or said something.
    fn after_station_change(&mut self, ui: &AppWindow) {
        self.rebuild_radio(ui);
        self.set_playing(ui, self.playing);
    }

    /// Tunes in to station `i`. Connecting can take seconds, so it happens on a worker thread;
    /// `station_connected` picks up the result.
    pub fn connect(&mut self, ui: &AppWindow, i: usize) {
        let Some(url) = self.stations.get(i).map(|s| s.url.clone()) else {
            return;
        };
        self.engine.stop();
        self.connect_attempt += 1;
        self.playing = Some((RADIO, i));
        self.station_now = Some("Connecting…".into());
        self.after_station_change(ui);

        let (attempt, out, weak) = (self.connect_attempt, self.connected.clone(), ui.as_weak());
        std::thread::spawn(move || {
            let result = bass::open_url(&url, false);
            *out.lock().unwrap_or_else(|e| e.into_inner()) = Some((attempt, result));
            let _ = weak.upgrade_in_event_loop(|ui| ui.invoke_station_connected());
        });
    }

    pub fn station_connected(&mut self, ui: &AppWindow) {
        let Some((attempt, result)) = take(&self.connected) else {
            return;
        };
        if attempt != self.connect_attempt || !self.playing.is_some_and(|p| p.0 == RADIO) {
            // Something else started meanwhile: drop this connection.
            if let Ok(h) = result {
                bass::free(h);
            }
            return;
        }
        match result {
            Ok(h) => {
                self.engine.play_live(h);
                self.tuned_in_at = online::now() * 1000;
                self.station_now = self.engine.stream_title();
            }
            Err(why) => self.station_now = Some(why),
        }
        self.after_station_change(ui);
    }

    /// Follows what the playing station says, and notices when it drops.
    fn tick_radio(&mut self, ui: &AppWindow) {
        self.engine.tick(); // finishes a fade-out
        if self.engine.has_ended() {
            self.engine.stop();
            self.station_now = Some("The stream stopped.".into());
            self.after_station_change(ui);
        } else if self.engine.is_live() {
            let title = self.engine.stream_title();
            if title.is_some() && title != self.station_now {
                self.station_now = title;
                self.after_station_change(ui);
            }
        }
    }

    /// Opens the station panel: for a new station, or to edit the one on list row `row`.
    pub fn open_editor(&mut self, ui: &AppWindow, row: Option<i32>) {
        let station = row
            .and_then(|r| self.views[RADIO].track_at(r))
            .and_then(|i| self.stations.get(i));
        self.editing = station.map(|s| s.id);
        ui.set_editor_heading(
            if station.is_some() {
                "Edit station"
            } else {
                "Add station"
            }
            .into(),
        );
        ui.set_editor_url(station.map_or("", |s| s.url.as_str()).into());
        ui.set_editor_name(station.map_or("", |s| s.name.as_str()).into());
        ui.set_editor_status("".into());
        ui.set_editor_error(false);
        ui.set_editor_busy(false);
        ui.set_editor_open(true);
    }

    /// Checks the pasted address works (without playing it) and reads the name it gives itself.
    pub fn editor_check(&mut self, ui: &AppWindow) {
        let Some(url) = radio::normalize_url(&ui.get_editor_url()) else {
            ui.set_editor_status(
                "That isn't a web address. It should start with http:// or https://.".into(),
            );
            ui.set_editor_error(true);
            return;
        };
        ui.set_editor_url(url.as_str().into());
        ui.set_editor_status("Checking…".into());
        ui.set_editor_error(false);
        ui.set_editor_busy(true);
        self.check_attempt += 1;
        let (attempt, out, weak) = (self.check_attempt, self.checked.clone(), ui.as_weak());
        std::thread::spawn(move || {
            let result = bass::open_url(&url, true).map(|h| {
                let mut headers = bass::tags(h, bass::TAGS_ICY);
                headers.extend(bass::tags(h, bass::TAGS_HTTP));
                let lines = || headers.iter().map(String::as_str);
                let name = radio::header_value(lines(), "icy-name");
                let kbps = radio::header_value(lines(), "icy-br")
                    .and_then(|b| b.split(',').next().map(str::to_string));
                let codec = bass::codec_name(bass::info(h).2);
                bass::free(h);
                let format: Vec<String> = [
                    (!codec.is_empty()).then(|| codec.to_string()),
                    kbps.map(|k| format!("{k} kbps")),
                ]
                .into_iter()
                .flatten()
                .collect();
                (name, format.join("  ·  "))
            });
            *out.lock().unwrap_or_else(|e| e.into_inner()) = Some((attempt, result));
            let _ = weak.upgrade_in_event_loop(|ui| ui.invoke_probe_done());
        });
    }

    pub fn probe_done(&mut self, ui: &AppWindow) {
        let Some((attempt, result)) = take(&self.checked) else {
            return;
        };
        if attempt != self.check_attempt {
            return;
        }
        ui.set_editor_busy(false);
        match result {
            Ok((name, format)) => {
                let works = if format.is_empty() {
                    "It works.".to_string()
                } else {
                    format!("It works:  {format}")
                };
                ui.set_editor_status(works.into());
                ui.set_editor_error(false);
                if ui.get_editor_name().trim().is_empty()
                    && let Some(n) = name
                {
                    ui.set_editor_name(n.into());
                }
            }
            Err(why) => {
                ui.set_editor_status(why.into());
                ui.set_editor_error(true);
            }
        }
    }

    pub fn editor_save(&mut self, ui: &AppWindow) {
        let Some(url) = radio::normalize_url(&ui.get_editor_url()) else {
            ui.set_editor_status(
                "That isn't a web address. It should start with http:// or https://.".into(),
            );
            ui.set_editor_error(true);
            return;
        };
        let name = ui.get_editor_name().trim().to_string();
        if name.is_empty() {
            ui.set_editor_status("Give the station a name.".into());
            ui.set_editor_error(true);
            return;
        }
        let Some(db) = &self.db else { return };
        let saved = match self.editing {
            Some(id) => db.update_station(id, &name, &url).map(|()| {
                if let Some(s) = self.stations.iter_mut().find(|s| s.id == id) {
                    (s.name, s.url) = (name.clone(), url.clone());
                }
            }),
            None => db
                .add_station(&name, &url)
                .map(|id| self.stations.push(Station { id, name, url })),
        };
        match saved {
            Ok(()) => {
                ui.set_editor_open(false);
                self.after_station_change(ui);
            }
            Err(e) => {
                ui.set_editor_status(format!("Couldn't save the station ({e}).").into());
                ui.set_editor_error(true);
            }
        }
    }

    /// Asks before deleting the station on list row `row`.
    pub fn ask_delete(&mut self, ui: &AppWindow, row: i32) {
        let Some(i) = self.views[RADIO].track_at(row) else {
            return;
        };
        self.deleting = Some(i);
        ui.set_confirm_message(format!("Delete {}?", self.stations[i].name).into());
        ui.set_confirm_open(true);
    }

    pub fn confirm_delete(&mut self, ui: &AppWindow) {
        ui.set_confirm_open(false);
        let Some(i) = self.deleting.take().filter(|&i| i < self.stations.len()) else {
            return;
        };
        if let Some(db) = &self.db
            && db.delete_station(self.stations[i].id).is_err()
        {
            return;
        }
        self.stations.remove(i);
        match self.playing {
            Some((RADIO, p)) if p == i => {
                self.engine.stop();
                self.connect_attempt += 1;
                self.playing = None;
                self.station_now = None;
            }
            Some((RADIO, p)) if p > i => self.playing = Some((RADIO, p - 1)),
            _ => {}
        }
        self.after_station_change(ui);
    }

    /// Adds the stations in an .m3u file, skipping addresses already saved. Returns how many.
    pub fn import_stations(&mut self, ui: &AppWindow, text: &str) -> usize {
        let Some(db) = &self.db else { return 0 };
        let mut added = 0;
        for (name, url) in radio::read_m3u(text) {
            if self
                .stations
                .iter()
                .any(|s| s.url.eq_ignore_ascii_case(&url))
            {
                continue;
            }
            if let Ok(id) = db.add_station(&name, &url) {
                self.stations.push(Station { id, name, url });
                added += 1;
            }
        }
        self.after_station_change(ui);
        added
    }

    pub fn export_stations(&self) -> String {
        radio::write_m3u(
            self.stations
                .iter()
                .map(|s| (s.name.as_str(), s.url.as_str())),
        )
    }
}
