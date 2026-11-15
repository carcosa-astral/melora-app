//! Station logos, found online (see online.rs) and fetched on a worker thread, drawn round.

use std::path::PathBuf;
use std::sync::mpsc::{Sender, channel};
use std::sync::{Arc, Mutex};

use slint::{Rgba8Pixel, SharedPixelBuffer};

use crate::covers::thumbnail;
use crate::db::Db;
use crate::online::{self, Query};

fn download(agent: &ureq::Agent, url: &str) -> Option<Vec<u8>> {
    let mut response = agent.get(url).call().ok()?;
    response
        .body_mut()
        .with_config()
        .limit(5 * 1024 * 1024)
        .read_to_vec()
        .ok()
}

type Done = Vec<(String, Option<SharedPixelBuffer<Rgba8Pixel>>)>;

/// Finds and fetches logos on its own thread, one station at a time.
pub struct Loader {
    requests: Sender<(String, String, u32)>,
    /// Finished logos (None: there's none), by station address.
    pub done: Arc<Mutex<Done>>,
}

impl Loader {
    /// `db_file` is opened on the worker (a second connection, as the scanner does). `notify` is
    /// called after each logo.
    pub fn new(db_file: PathBuf, base: String, notify: impl Fn() + Send + 'static) -> Loader {
        let (requests, rx) = channel::<(String, String, u32)>();
        let done: Arc<Mutex<Done>> = Arc::default();
        let out = done.clone();
        std::thread::spawn(move || {
            let Ok(db) = Db::open(&db_file, &base) else {
                return;
            };
            let agent = online::agent();
            for (url, name, px) in rx {
                let query = Query::Station {
                    url: url.clone(),
                    name,
                };
                let logo = online::find(&db, &agent, &query, online::now())
                    .and_then(|l| download(&agent, &l))
                    .and_then(|bytes| thumbnail(&bytes, px, px as f32 / 2.0));
                out.lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .push((url, logo));
                notify();
            }
        });
        Loader { requests, done }
    }

    /// Asks for a station's logo, `px` wide and round.
    pub fn request(&self, url: &str, name: &str, px: u32) {
        let _ = self.requests.send((url.to_string(), name.to_string(), px));
    }
}

/// 2.x's cache key for a station's logo.
pub fn key(url: &str) -> String {
    online::key(&Query::Station {
        url: url.to_string(),
        name: String::new(),
    })
}
