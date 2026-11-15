//! Sends plays, loves and "now playing" to Last.fm and ListenBrainz on a worker thread, as 2.x:
//! plays wait in the database queue (so nothing is lost offline) and go out in batches, oldest
//! first; anything that can't be sent is tried again every 5 minutes. Also connects the accounts
//! for Settings. Logins are 2.x's settings, so a 2.x data folder stays connected.

use std::collections::BTreeMap;
use std::path::PathBuf;
use std::sync::mpsc::{RecvTimeoutError, Sender, channel};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use serde_json::Value;

use crate::db::Db;
use crate::scrobble::{self, Love, Outcome, Scrobble};

const RETRY_EVERY: Duration = Duration::from_secs(5 * 60);
const BATCH: usize = 50;
const APPROVAL_POLL: Duration = Duration::from_secs(3);
const APPROVAL_TIMEOUT: Duration = Duration::from_secs(5 * 60);

pub const LASTFM: &str = "lastfm";
pub const LISTENBRAINZ: &str = "listenbrainz";

/// The services that are connected now (by 2.x's settings).
pub fn connected(db: &Db) -> Vec<&'static str> {
    let mut out = Vec::new();
    if ["lastfm.apiKey", "lastfm.secret", "lastfm.session"]
        .iter()
        .all(|k| db.setting(k).is_some())
    {
        out.push(LASTFM);
    }
    if db.setting("listenbrainz.token").is_some() {
        out.push(LISTENBRAINZ);
    }
    out
}

enum Job {
    NowPlaying(Scrobble),
    Flush,
    /// Ask Last.fm whether a track is loved there (it may have been loved on the website).
    CheckLoved(String, String),
}

/// What the worker reports back to the window.
#[derive(Debug, Clone, PartialEq)]
pub enum Report {
    /// The queue changed (plays sent or dropped).
    Sent,
    /// Last.fm said whether this track is loved; the database has the answer.
    LovedChecked { artist: String, title: String },
    /// A connect step in Settings: the service, a message to show, and whether it's done.
    Connect {
        service: &'static str,
        message: String,
        done: bool,
    },
}

pub struct Scrobbler {
    jobs: Sender<Job>,
    pub reports: Arc<Mutex<Vec<Report>>>,
    notify: Arc<dyn Fn() + Send + Sync>,
    db_file: PathBuf,
    base: String,
}

fn agent() -> ureq::Agent {
    // Errors come back with an error status but a useful body, so read it either way.
    ureq::Agent::config_builder()
        .http_status_as_error(false)
        .timeout_global(Some(Duration::from_secs(20)))
        .user_agent(concat!("Melora/", env!("CARGO_PKG_VERSION")))
        .build()
        .into()
}

enum CallError {
    /// Last.fm said no, with its error code.
    LastFm(i64, String),
    /// Couldn't reach it, or the answer wasn't readable.
    Net(String),
}

fn encode(s: &str) -> String {
    s.bytes()
        .map(|b| match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                (b as char).to_string()
            }
            _ => format!("%{b:02X}"),
        })
        .collect()
}

/// A signed Last.fm call with the saved key and secret.
fn lastfm_call(
    agent: &ureq::Agent,
    db: &Db,
    post: bool,
    mut params: BTreeMap<String, String>,
) -> Result<Value, CallError> {
    let key = db
        .setting("lastfm.apiKey")
        .ok_or(CallError::LastFm(10, "No Last.fm API key.".into()))?;
    let secret = db
        .setting("lastfm.secret")
        .ok_or(CallError::LastFm(10, "No Last.fm shared secret.".into()))?;
    params.insert("api_key".into(), key);
    let sig = scrobble::lastfm_sign(&params, &secret);
    params.insert("api_sig".into(), sig);
    params.insert("format".into(), "json".into());
    let response = if post {
        agent
            .post(scrobble::LASTFM_API)
            .send_form(params.iter().map(|(k, v)| (k.as_str(), v.as_str())))
    } else {
        let query: Vec<String> = params
            .iter()
            .map(|(k, v)| format!("{}={}", encode(k), encode(v)))
            .collect();
        agent
            .get(&format!("{}?{}", scrobble::LASTFM_API, query.join("&")))
            .call()
    };
    let mut response = response.map_err(|e| CallError::Net(e.to_string()))?;
    let ok = response.status().is_success();
    let json: Option<Value> = response
        .body_mut()
        .read_to_string()
        .ok()
        .and_then(|t| serde_json::from_str(&t).ok());
    match json {
        Some(j) if j.get("error").is_some() => Err(CallError::LastFm(
            j["error"].as_i64().unwrap_or(0),
            j["message"].as_str().unwrap_or("Last.fm error").to_string(),
        )),
        Some(j) if ok => Ok(j),
        _ => Err(CallError::Net(
            "Last.fm gave an answer Melora couldn't read.".into(),
        )),
    }
}

fn lastfm_send(agent: &ureq::Agent, db: &Db, params: BTreeMap<String, String>) -> Outcome {
    match lastfm_call(agent, db, true, params) {
        Ok(_) => Outcome::Sent,
        Err(CallError::LastFm(code, _)) => {
            let outcome = scrobble::lastfm_outcome(code);
            if outcome == Outcome::AuthFailed {
                let _ = db.set_setting("lastfm.reconnect", "1");
            }
            outcome
        }
        Err(CallError::Net(_)) => Outcome::RetryLater,
    }
}

fn session_params(db: &Db, method: &str) -> Option<BTreeMap<String, String>> {
    let mut p = BTreeMap::new();
    p.insert("method".to_string(), method.to_string());
    p.insert("sk".to_string(), db.setting("lastfm.session")?);
    Some(p)
}

/// A ListenBrainz request with the saved token: the HTTP status, and the body if it's JSON.
fn listenbrainz(
    agent: &ureq::Agent,
    db: &Db,
    path: &str,
    body: Option<&Value>,
) -> Result<(u16, Option<Value>), String> {
    let token = db
        .setting("listenbrainz.token")
        .ok_or("No ListenBrainz token.")?;
    let url = format!("{}{path}", scrobble::LISTENBRAINZ_API);
    let auth = format!("Token {token}");
    let response = match body {
        Some(b) => agent
            .post(&url)
            .header("Authorization", &auth)
            .header("Content-Type", "application/json")
            .send(b.to_string()),
        None => agent.get(&url).header("Authorization", &auth).call(),
    };
    let mut response = response.map_err(|e| e.to_string())?;
    let status = response.status().as_u16();
    let json = response
        .body_mut()
        .read_to_string()
        .ok()
        .and_then(|t| serde_json::from_str::<Value>(&t).ok());
    Ok((status, json))
}

fn submit(agent: &ureq::Agent, db: &Db, service: &str, plays: &[Scrobble]) -> Outcome {
    if service == LASTFM {
        let Some(mut p) = session_params(db, "track.scrobble") else {
            return Outcome::AuthFailed;
        };
        for (i, play) in plays.iter().enumerate() {
            scrobble::lastfm_add_play(&mut p, play, Some(i));
        }
        return lastfm_send(agent, db, p);
    }
    let kind = if plays.len() == 1 { "single" } else { "import" };
    match listenbrainz(
        agent,
        db,
        "submit-listens",
        Some(&scrobble::listenbrainz_payload(kind, plays)),
    ) {
        Ok((status, _)) => {
            let outcome = scrobble::listenbrainz_outcome(status);
            if outcome == Outcome::AuthFailed {
                let _ = db.set_setting("listenbrainz.reconnect", "1");
            }
            outcome
        }
        Err(_) => Outcome::RetryLater,
    }
}

fn now_playing(agent: &ureq::Agent, db: &Db, service: &str, play: &Scrobble) {
    // "Now playing" is best effort.
    if service == LASTFM {
        if let Some(mut p) = session_params(db, "track.updateNowPlaying") {
            scrobble::lastfm_add_play(&mut p, play, None);
            let _ = lastfm_call(agent, db, true, p);
        }
    } else {
        let _ = listenbrainz(
            agent,
            db,
            "submit-listens",
            Some(&scrobble::listenbrainz_payload(
                "playing_now",
                std::slice::from_ref(play),
            )),
        );
    }
}

/// ListenBrainz loves recordings by MusicBrainz ID: the one from the tags (Picard writes it),
/// else ListenBrainz is asked to match the artist and title; one it can't match is refused.
fn love(agent: &ureq::Agent, db: &Db, service: &str, love: &Love) -> Outcome {
    if service == LASTFM {
        let method = if love.loved {
            "track.love"
        } else {
            "track.unlove"
        };
        let Some(mut p) = session_params(db, method) else {
            return Outcome::AuthFailed;
        };
        p.insert("artist".into(), love.artist.clone());
        p.insert("track".into(), love.title.clone());
        return lastfm_send(agent, db, p);
    }
    let mbid = match &love.mbid {
        Some(m) => m.clone(),
        None => {
            let mut q = format!(
                "metadata/lookup/?artist_name={}&recording_name={}",
                encode(&love.artist),
                encode(&love.title)
            );
            if let Some(album) = &love.album {
                q.push_str(&format!("&release_name={}", encode(album)));
            }
            match listenbrainz(agent, db, &q, None) {
                Ok((200, json)) => {
                    match json.and_then(|j| j["recording_mbid"].as_str().map(str::to_string)) {
                        Some(m) => m,
                        None => return Outcome::Rejected, // ListenBrainz doesn't know the track
                    }
                }
                Ok((status, _)) => return scrobble::listenbrainz_outcome(status),
                Err(_) => return Outcome::RetryLater,
            }
        }
    };
    let body =
        serde_json::json!({ "recording_mbid": mbid, "score": if love.loved { 1 } else { 0 } });
    match listenbrainz(agent, db, "feedback/recording-feedback", Some(&body)) {
        Ok((status, _)) => scrobble::listenbrainz_outcome(status),
        Err(_) => Outcome::RetryLater,
    }
}

/// Sends everything queued, per service, oldest first. Stops for a service that can't be reached
/// or whose login was refused (its plays wait); drops what's sent or refused for good.
fn flush(agent: &ureq::Agent, db: &mut Db) {
    // shortcut: what's refused for good isn't logged yet, as 2.x does; the log comes in phase 5.
    for service in connected(db) {
        while let Ok(batch) = db.scrobbles(service, BATCH) {
            if batch.is_empty() {
                break;
            }
            let plays: Vec<Scrobble> = batch.iter().map(|(_, p)| p.clone()).collect();
            match submit(agent, db, service, &plays) {
                Outcome::Sent | Outcome::Rejected => {
                    let ids: Vec<i64> = batch.iter().map(|(id, _)| *id).collect();
                    if db.delete_scrobbles(&ids).is_err() {
                        break;
                    }
                }
                Outcome::RetryLater | Outcome::AuthFailed => break,
            }
        }
        for l in db.pending_loves(service).unwrap_or_default() {
            match love(agent, db, service, &l) {
                Outcome::Sent | Outcome::Rejected => {
                    let _ = db.clear_pending_love(service, &l.artist, &l.title);
                }
                Outcome::RetryLater | Outcome::AuthFailed => break,
            }
        }
    }
}

/// Whether the connected user has loved this track on Last.fm; None if unknown or unreachable.
fn lastfm_loved(agent: &ureq::Agent, db: &Db, artist: &str, title: &str) -> Option<bool> {
    let user = db.setting("lastfm.user")?;
    db.setting("lastfm.session")?;
    let mut p = BTreeMap::new();
    for (k, v) in [
        ("method", "track.getInfo"),
        ("artist", artist),
        ("track", title),
        ("username", &user),
    ] {
        p.insert(k.to_string(), v.to_string());
    }
    let json = lastfm_call(agent, db, false, p).ok()?;
    match json["track"]["userloved"].as_str()? {
        "1" => Some(true),
        "0" => Some(false),
        _ => None,
    }
}

impl Scrobbler {
    /// Starts the worker. `notify` is called (on the worker) after each report.
    pub fn new(
        db_file: PathBuf,
        base: String,
        notify: impl Fn() + Send + Sync + 'static,
    ) -> Scrobbler {
        let (jobs, rx) = channel::<Job>();
        let reports: Arc<Mutex<Vec<Report>>> = Arc::default();
        let notify: Arc<dyn Fn() + Send + Sync> = Arc::new(notify);
        let (out, ping, file, b) = (
            reports.clone(),
            notify.clone(),
            db_file.clone(),
            base.clone(),
        );
        std::thread::spawn(move || {
            let Ok(mut db) = Db::open(&file, &b) else {
                return;
            };
            let agent = agent();
            let report = |r: Report| {
                out.lock().unwrap_or_else(|e| e.into_inner()).push(r);
                ping();
            };
            flush(&agent, &mut db); // whatever waited from last time
            report(Report::Sent);
            loop {
                match rx.recv_timeout(RETRY_EVERY) {
                    Ok(Job::NowPlaying(play)) => {
                        for service in connected(&db) {
                            now_playing(&agent, &db, service, &play);
                        }
                    }
                    Ok(Job::Flush) | Err(RecvTimeoutError::Timeout) => {
                        flush(&agent, &mut db);
                        report(Report::Sent);
                    }
                    Ok(Job::CheckLoved(artist, title)) => {
                        if let Some(loved) = lastfm_loved(&agent, &db, &artist, &title) {
                            let _ = db.sync_loved(&artist, &title, loved);
                            report(Report::LovedChecked { artist, title });
                        }
                    }
                    Err(RecvTimeoutError::Disconnected) => return,
                }
            }
        });
        Scrobbler {
            jobs,
            reports,
            notify,
            db_file,
            base,
        }
    }

    pub fn now_playing(&self, play: Scrobble) {
        let _ = self.jobs.send(Job::NowPlaying(play));
    }

    pub fn flush(&self) {
        let _ = self.jobs.send(Job::Flush);
    }

    pub fn check_loved(&self, artist: &str, title: &str) {
        let _ = self
            .jobs
            .send(Job::CheckLoved(artist.to_string(), title.to_string()));
    }

    fn report_from(&self) -> impl Fn(Report) + Send + 'static {
        let (out, ping) = (self.reports.clone(), self.notify.clone());
        move |r| {
            out.lock().unwrap_or_else(|e| e.into_inner()).push(r);
            ping();
        }
    }

    /// Settings' Connect for Last.fm: saves the key and secret, opens the approval page in the
    /// browser (through `open`), then waits for the approval, as 2.x.
    pub fn connect_lastfm(
        &self,
        key: String,
        secret: String,
        open: impl Fn(&str) + Send + 'static,
    ) {
        let (file, base, report) = (self.db_file.clone(), self.base.clone(), self.report_from());
        std::thread::spawn(move || {
            let say = |message: &str, done: bool| {
                report(Report::Connect {
                    service: LASTFM,
                    message: message.into(),
                    done,
                })
            };
            let Ok(db) = Db::open(&file, &base) else {
                return;
            };
            let _ = db.set_setting("lastfm.apiKey", key.trim());
            let _ = db.set_setting("lastfm.secret", secret.trim());
            let agent = agent();
            say("Contacting Last.fm…", false);
            let mut p = BTreeMap::new();
            p.insert("method".to_string(), "auth.getToken".to_string());
            let token = match lastfm_call(&agent, &db, false, p) {
                Ok(j) => match j["token"].as_str() {
                    Some(t) => t.to_string(),
                    None => return say("Last.fm didn't return a token.", true),
                },
                Err(CallError::LastFm(10 | 26, _)) => {
                    return say(
                        "Last.fm didn't accept that API key. Check it and the secret.",
                        true,
                    );
                }
                Err(CallError::LastFm(_, m) | CallError::Net(m)) => {
                    return say(&format!("Couldn't reach Last.fm: {m}"), true);
                }
            };
            open(&format!(
                "{}?api_key={}&token={}",
                scrobble::LASTFM_AUTH,
                encode(key.trim()),
                encode(&token)
            ));
            say(
                "Approve Melora in your browser. This will update by itself.",
                false,
            );
            let mut waited = Duration::ZERO;
            while waited < APPROVAL_TIMEOUT {
                std::thread::sleep(APPROVAL_POLL);
                waited += APPROVAL_POLL;
                let mut p = BTreeMap::new();
                p.insert("method".to_string(), "auth.getSession".to_string());
                p.insert("token".to_string(), token.clone());
                match lastfm_call(&agent, &db, false, p) {
                    Ok(j) => {
                        let (Some(k), Some(name)) =
                            (j["session"]["key"].as_str(), j["session"]["name"].as_str())
                        else {
                            return say("Last.fm didn't return a session.", true);
                        };
                        let _ = db.set_setting("lastfm.session", k);
                        let _ = db.set_setting("lastfm.user", name);
                        let _ = db.set_setting("lastfm.reconnect", "0");
                        return say("", true);
                    }
                    Err(CallError::LastFm(scrobble::LASTFM_NOT_AUTHORIZED_YET, _)) => {}
                    Err(CallError::LastFm(_, m) | CallError::Net(m)) => {
                        return say(&format!("Couldn't reach Last.fm: {m}"), true);
                    }
                }
            }
            say("Didn't hear back from Last.fm. Try Connect again.", true);
        });
    }

    /// Settings' Connect for ListenBrainz: checks the token and saves it if it's valid.
    pub fn connect_listenbrainz(&self, token: String) {
        let (file, base, report) = (self.db_file.clone(), self.base.clone(), self.report_from());
        std::thread::spawn(move || {
            let say = |message: &str| {
                report(Report::Connect {
                    service: LISTENBRAINZ,
                    message: message.into(),
                    done: true,
                })
            };
            let Ok(db) = Db::open(&file, &base) else {
                return;
            };
            let token = token.trim().to_string();
            let answer = agent()
                .get(&format!("{}validate-token", scrobble::LISTENBRAINZ_API))
                .header("Authorization", &format!("Token {token}"))
                .call()
                .ok()
                .and_then(|mut r| r.body_mut().read_to_string().ok())
                .and_then(|t| serde_json::from_str::<Value>(&t).ok());
            match answer {
                Some(j) if j["valid"].as_bool() == Some(true) => {
                    let _ = db.set_setting("listenbrainz.token", &token);
                    let _ =
                        db.set_setting("listenbrainz.user", j["user_name"].as_str().unwrap_or(""));
                    let _ = db.set_setting("listenbrainz.reconnect", "0");
                    say("");
                }
                Some(_) => say("ListenBrainz didn't accept that token."),
                None => say("Couldn't reach ListenBrainz."),
            }
        });
    }
}
