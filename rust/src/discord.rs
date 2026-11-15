//! "Listening to" on your Discord profile, as 2.x: Melora talks to the Discord app on this PC
//! over its local pipe (discord-ipc-0 to 9) with your own application ID; no login or internet
//! for that part. Covers must be web addresses, so they're looked up online (online.rs) while
//! this is on. Updates are sent from a worker: combined (Discord allows one every few seconds),
//! and Discord is tried again every 15 seconds if it wasn't running.

use std::io::{Read, Write};
use std::path::PathBuf;
use std::sync::mpsc::{RecvTimeoutError, Sender, channel};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::{Value, json};

use crate::db::Db;
use crate::online::{self, Query};

const SETTLE: Duration = Duration::from_millis(750);
const MIN_INTERVAL: Duration = Duration::from_secs(2);
const RETRY_CONNECT: Duration = Duration::from_secs(15);
const MAX_TEXT: usize = 128;
pub const IDLE: &str = "Connects to Discord once something plays.";

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Source {
    Music,
    Mixes,
    Radio,
}

/// What Discord should show. `start` and `end` (Unix ms) drive its progress bar.
#[derive(Clone, Debug, PartialEq)]
pub struct Presence {
    pub source: Source,
    pub details: String,
    pub state: String,
    pub large_text: Option<String>,
    pub cover: Option<Query>,
    pub start: Option<i64>,
    pub end: Option<i64>,
}

/// Discord wants 2 to 128 characters in each text field.
pub fn fit(text: &str) -> String {
    let n = text.chars().count();
    if n > MAX_TEXT {
        let mut cut: String = text.chars().take(MAX_TEXT - 1).collect();
        cut.push('…');
        cut
    } else if n < 2 {
        format!("{text}{}", "⠀".repeat(2 - n)) // a blank braille cell, which Discord counts
    } else {
        text.to_string()
    }
}

/// The activity as Discord wants it: type 2 is "Listening to". Without an image, Discord shows
/// the application's own icon.
pub fn activity(p: &Presence, cover: Option<&str>) -> Value {
    let mut a = json!({ "type": 2, "details": fit(&p.details), "state": fit(&p.state) });
    if let Some(start) = p.start {
        a["timestamps"] = json!({ "start": start });
        if let Some(end) = p.end {
            a["timestamps"]["end"] = json!(end);
        }
    }
    if let Some(url) = cover {
        a["assets"] = json!({ "large_image": url });
        if let Some(text) = &p.large_text {
            a["assets"]["large_text"] = json!(fit(text));
        }
    }
    a
}

/// One frame on the pipe: a little-endian opcode and length, then JSON.
fn frame(op: u32, body: &Value) -> Vec<u8> {
    let json = body.to_string().into_bytes();
    let mut out = Vec::with_capacity(8 + json.len());
    out.extend(op.to_le_bytes());
    out.extend((json.len() as u32).to_le_bytes());
    out.extend(json);
    out
}

/// A different ID for each command, as Discord asks.
fn next_nonce() -> u64 {
    static NEXT: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
    NEXT.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
}

struct Pipe {
    file: std::fs::File,
}

impl Pipe {
    fn open() -> Option<Pipe> {
        (0..10).find_map(|i| {
            std::fs::OpenOptions::new()
                .read(true)
                .write(true)
                .open(format!(r"\\.\pipe\discord-ipc-{i}"))
                .ok()
                .map(|file| Pipe { file })
        })
    }

    fn send(&mut self, op: u32, body: &Value) -> std::io::Result<()> {
        self.file.write_all(&frame(op, body))?;
        self.file.flush()
    }

    fn read(&mut self) -> std::io::Result<(u32, Value)> {
        let mut header = [0u8; 8];
        self.file.read_exact(&mut header)?;
        let op = u32::from_le_bytes([header[0], header[1], header[2], header[3]]);
        let len = u32::from_le_bytes([header[4], header[5], header[6], header[7]]) as usize;
        let mut body = vec![0u8; len];
        self.file.read_exact(&mut body)?;
        Ok((op, serde_json::from_slice(&body).unwrap_or(Value::Null)))
    }

    /// Sends a command and reads Discord's answer. Reading after each write (rather than on
    /// another thread) is because Windows runs a pipe opened this way one operation at a time.
    fn command(&mut self, body: &Value) -> std::io::Result<Value> {
        self.send(1, body)?;
        loop {
            match self.read()? {
                (3, ping) => self.send(4, &ping)?, // ping: answer pong and read on
                (2, _) => return Err(std::io::Error::other("Discord closed the connection")),
                (_, reply) => return Ok(reply),
            }
        }
    }
}

enum Msg {
    Show(Option<Presence>),
    /// A Discord setting changed: reconnect and resend.
    Settings,
}

pub struct Discord {
    tx: Sender<Msg>,
    /// A line for Settings: connected, waiting for Discord, or what went wrong.
    pub status: Arc<Mutex<String>>,
}

impl Discord {
    /// Starts the worker. `notify` is called when the status line changes.
    pub fn new(db_file: PathBuf, base: String, notify: impl Fn() + Send + 'static) -> Discord {
        let (tx, rx) = channel::<Msg>();
        let status = Arc::new(Mutex::new(String::new()));
        let shared = status.clone();
        std::thread::spawn(move || {
            let Ok(db) = Db::open(&db_file, &base) else {
                return;
            };
            let agent = online::agent();
            let set_status = |s: &str| {
                let mut st = shared.lock().unwrap_or_else(|e| e.into_inner());
                if *st != s {
                    *st = s.to_string();
                    drop(st);
                    notify();
                }
            };
            let (mut wanted, mut shown): (Option<Presence>, Option<Presence>) = (None, None);
            let (mut pipe, mut connected_id): (Option<Pipe>, String) = (None, String::new());
            let mut last_attempt: Option<Instant> = None;
            let mut last_send = Instant::now() - MIN_INTERVAL;
            loop {
                // Wake on a change (and let a burst settle), or now and then to retry Discord.
                match rx.recv_timeout(RETRY_CONNECT) {
                    Ok(first) => {
                        let mut msgs = vec![first];
                        std::thread::sleep(SETTLE);
                        msgs.extend(rx.try_iter());
                        for m in msgs {
                            match m {
                                Msg::Show(p) => wanted = p,
                                Msg::Settings => {
                                    pipe = None; // dropping the connection also clears the status
                                    shown = None;
                                    last_attempt = None;
                                }
                            }
                        }
                    }
                    Err(RecvTimeoutError::Timeout) => {}
                    Err(RecvTimeoutError::Disconnected) => return,
                }

                let setting = |k: &str| db.setting(k);
                let enabled = setting("discord.enabled").as_deref() == Some("1");
                let id = setting("discord.appId").unwrap_or_default();
                let filtered = wanted.clone().filter(|p| match p.source {
                    Source::Mixes => setting("discord.mixes").as_deref() != Some("0"),
                    Source::Radio => setting("discord.radio").as_deref() != Some("0"),
                    Source::Music => true,
                });
                if !enabled {
                    pipe = None;
                    shown = None;
                    set_status("Off");
                    continue;
                }
                if id.is_empty() {
                    pipe = None;
                    set_status("Paste your application ID to turn this on.");
                    continue;
                }
                if pipe.is_some() && id != connected_id {
                    pipe = None;
                    shown = None;
                }
                if pipe.is_none() {
                    shown = None;
                    if filtered.is_none() {
                        set_status(IDLE);
                        continue;
                    }
                    if last_attempt.is_some_and(|t| t.elapsed() < RETRY_CONNECT) {
                        continue;
                    }
                    last_attempt = Some(Instant::now());
                    let Some(mut p) = Pipe::open() else {
                        set_status("Waiting for Discord to start.");
                        continue;
                    };
                    let ready = p
                        .send(0, &json!({ "v": 1, "client_id": id }))
                        .and_then(|()| p.read());
                    match ready {
                        Ok((1, reply)) if reply["evt"] == "READY" => {
                            pipe = Some(p);
                            connected_id = id.clone();
                            set_status("Connected to Discord.");
                        }
                        Ok((_, reply)) => {
                            let why = reply["message"]
                                .as_str()
                                .unwrap_or("Discord closed the connection.");
                            set_status(&format!(
                                "Discord didn't accept that application ID ({why})."
                            ));
                            continue;
                        }
                        Err(_) => {
                            set_status("Waiting for Discord to start.");
                            continue;
                        }
                    }
                }
                if filtered == shown {
                    continue;
                }
                let wait = MIN_INTERVAL.saturating_sub(last_send.elapsed());
                std::thread::sleep(wait);
                let cover = filtered
                    .as_ref()
                    .and_then(|p| p.cover.as_ref())
                    .and_then(|q| online::find(&db, &agent, q, online::now()));
                let body = json!({
                    "cmd": "SET_ACTIVITY",
                    "args": {
                        "pid": std::process::id(),
                        "activity": filtered.as_ref().map(|p| activity(p, cover.as_deref())),
                    },
                    "nonce": format!("melora-{}", next_nonce()),
                });
                let sent = pipe.as_mut().map(|p| p.command(&body));
                last_send = Instant::now();
                match sent {
                    Some(Ok(_)) => shown = filtered,
                    _ => {
                        // Discord quit or restarted: try again later.
                        pipe = None;
                        shown = None;
                        set_status("Waiting for Discord to start.");
                    }
                }
            }
        });
        Discord { tx, status }
    }

    /// What's playing now (None for nothing, or paused). Cheap; call on every change.
    pub fn show(&self, p: Option<Presence>) {
        let _ = self.tx.send(Msg::Show(p));
    }

    pub fn settings_changed(&self) {
        let _ = self.tx.send(Msg::Settings);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn presence() -> Presence {
        Presence {
            source: Source::Music,
            details: "Digital Bath".into(),
            state: "Deftones".into(),
            large_text: Some("White Pony".into()),
            cover: None,
            start: Some(1_000),
            end: Some(256_000),
        }
    }

    #[test]
    fn text_fits_discords_limits() {
        assert_eq!(fit("ok"), "ok");
        assert_eq!(fit("x").chars().count(), 2);
        let long = fit(&"y".repeat(300));
        assert_eq!(long.chars().count(), 128);
        assert!(long.ends_with('…'));
    }

    #[test]
    fn the_activity_is_listening_with_progress_and_cover() {
        let a = activity(&presence(), Some("https://img/cover.jpg"));
        assert_eq!(a["type"], 2);
        assert_eq!(
            (
                a["timestamps"]["start"].as_i64(),
                a["timestamps"]["end"].as_i64()
            ),
            (Some(1_000), Some(256_000))
        );
        assert_eq!(a["assets"]["large_image"], "https://img/cover.jpg");
        assert_eq!(a["assets"]["large_text"], "White Pony");
        assert!(activity(&presence(), None).get("assets").is_none()); // Discord shows the app's icon
    }

    #[test]
    fn frames_are_opcode_length_and_json() {
        let f = frame(1, &json!({ "a": 1 }));
        assert_eq!(&f[..4], &1u32.to_le_bytes());
        assert_eq!(
            u32::from_le_bytes([f[4], f[5], f[6], f[7]]) as usize,
            f.len() - 8
        );
        assert_eq!(&f[8..], br#"{"a":1}"#);
    }
}
