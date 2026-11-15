//! What gets scrobbled and how it's sent, ported from 2.x: the rules for when a play counts, and
//! the exact requests Last.fm and ListenBrainz take. No network here (see scrobbler.rs), so it's
//! all testable.

use std::collections::BTreeMap;
use std::time::Duration;

use serde_json::{Value, json};

/// One play to report: what it was and when it started.
#[derive(Clone, Debug, PartialEq)]
pub struct Scrobble {
    pub artist: String,
    pub title: String,
    pub album: Option<String>,
    pub album_artist: Option<String>,
    pub track_no: i32,
    pub duration: Duration,
    /// Unix seconds.
    pub started_at: i64,
}

/// Loving (or un-loving) a track. The MusicBrainz recording ID is from the file's tags, if any.
#[derive(Clone, Debug, PartialEq)]
pub struct Love {
    pub artist: String,
    pub title: String,
    pub album: Option<String>,
    pub mbid: Option<String>,
    pub loved: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Outcome {
    /// Accepted (or deliberately ignored by the service): drop from the queue.
    Sent,
    /// Network trouble, rate limit or server error: keep and try again later.
    RetryLater,
    /// Refused for good: drop.
    Rejected,
    /// The login is no longer valid: keep until the user reconnects.
    AuthFailed,
}

// ---- When a play counts (the usual rules, shared by Last.fm and ListenBrainz) ----

const MINIMUM_LENGTH: Duration = Duration::from_secs(30);
const MAXIMUM_WAIT: Duration = Duration::from_secs(4 * 60);

/// Tracks of 30 seconds or less are never scrobbled.
pub fn is_long_enough(duration: Duration) -> bool {
    duration > MINIMUM_LENGTH
}

/// How long you have to actually listen: half the track, or 4 minutes, whichever comes first.
pub fn listen_needed(duration: Duration) -> Duration {
    (duration / 2).min(MAXIMUM_WAIT)
}

pub fn is_due(duration: Duration, listened: Duration) -> bool {
    is_long_enough(duration) && listened >= listen_needed(duration)
}

// ---- Last.fm (API 2.0) ----

pub const LASTFM_API: &str = "https://ws.audioscrobbler.com/2.0/";
pub const LASTFM_AUTH: &str = "https://www.last.fm/api/auth/";
pub const LASTFM_NOT_AUTHORIZED_YET: i64 = 14;

/// What to do with queued plays after Last.fm returned an error code
/// (https://www.last.fm/api/errorcodes).
pub fn lastfm_outcome(code: i64) -> Outcome {
    match code {
        8 | 11 | 16 | 29 => Outcome::RetryLater,
        4 | 9 | 10 | 26 => Outcome::AuthFailed,
        _ => Outcome::Rejected,
    }
}

/// Adds a play's fields; batches use indexed names like `artist[0]`.
pub fn lastfm_add_play(
    params: &mut BTreeMap<String, String>,
    play: &Scrobble,
    index: Option<usize>,
) {
    let key = |name: &str| index.map_or_else(|| name.to_string(), |i| format!("{name}[{i}]"));
    params.insert(key("artist"), play.artist.clone());
    params.insert(key("track"), play.title.clone());
    if index.is_some() {
        params.insert(key("timestamp"), play.started_at.to_string());
    }
    if let Some(album) = &play.album {
        params.insert(key("album"), album.clone());
    }
    if let Some(aa) = play.album_artist.as_ref().filter(|aa| **aa != play.artist) {
        params.insert(key("albumArtist"), aa.clone());
    }
    if play.track_no > 0 {
        params.insert(key("trackNumber"), play.track_no.to_string());
    }
    if !play.duration.is_zero() {
        params.insert(key("duration"), play.duration.as_secs().to_string());
    }
}

/// Last.fm's signature: the MD5 of every parameter (except format and callback) as name+value,
/// sorted by name, then the shared secret. A BTreeMap is already sorted (by byte, as Last.fm wants).
pub fn lastfm_sign(params: &BTreeMap<String, String>, secret: &str) -> String {
    let mut text = String::new();
    for (name, value) in params
        .iter()
        .filter(|(k, _)| *k != "format" && *k != "callback")
    {
        text.push_str(name);
        text.push_str(value);
    }
    text.push_str(secret);
    format!("{:x}", md5::compute(text.as_bytes()))
}

// ---- ListenBrainz ----

pub const LISTENBRAINZ_API: &str = "https://api.listenbrainz.org/1/";

pub fn listenbrainz_outcome(status: u16) -> Outcome {
    match status {
        200 => Outcome::Sent,
        401 => Outcome::AuthFailed,
        429 => Outcome::RetryLater,
        s if s >= 500 => Outcome::RetryLater,
        _ => Outcome::Rejected,
    }
}

/// The submit-listens body. "playing_now" listens have no timestamp.
pub fn listenbrainz_payload(listen_type: &str, plays: &[Scrobble]) -> Value {
    let payload: Vec<Value> = plays
        .iter()
        .map(|play| {
            let mut info = json!({
                "media_player": "Melora",
                "submission_client": "Melora",
                "submission_client_version": env!("CARGO_PKG_VERSION"),
            });
            if !play.duration.is_zero() {
                info["duration_ms"] = json!(play.duration.as_millis() as u64);
            }
            if play.track_no > 0 {
                info["tracknumber"] = json!(play.track_no);
            }
            let mut metadata = json!({ "artist_name": play.artist, "track_name": play.title });
            if let Some(album) = &play.album {
                metadata["release_name"] = json!(album);
            }
            metadata["additional_info"] = info;
            let mut listen = json!({});
            if listen_type != "playing_now" {
                listen["listened_at"] = json!(play.started_at);
            }
            listen["track_metadata"] = metadata;
            listen
        })
        .collect();
    json!({ "listen_type": listen_type, "payload": payload })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn play() -> Scrobble {
        Scrobble {
            artist: "Deftones".into(),
            title: "Digital Bath".into(),
            album: Some("White Pony".into()),
            album_artist: Some("Deftones".into()),
            track_no: 2,
            duration: Duration::from_secs(255),
            started_at: 1_700_000_000,
        }
    }

    #[test]
    fn a_play_counts_after_half_or_four_minutes() {
        let s = Duration::from_secs;
        assert!(!is_long_enough(s(30)));
        assert!(is_long_enough(s(31)));
        assert_eq!(listen_needed(s(200)), s(100));
        assert_eq!(listen_needed(s(3600)), s(240));
        assert!(!is_due(s(200), s(99)));
        assert!(is_due(s(200), s(100)));
        assert!(!is_due(s(20), s(20)));
    }

    #[test]
    fn lastfm_fields_and_signature() {
        let mut p = BTreeMap::new();
        p.insert("method".to_string(), "track.scrobble".to_string());
        lastfm_add_play(&mut p, &play(), Some(0));
        assert_eq!(p["artist[0]"], "Deftones");
        assert_eq!(p["timestamp[0]"], "1700000000");
        assert_eq!(p["duration[0]"], "255");
        assert!(!p.contains_key("albumArtist[0]")); // same as the artist
        // MD5 of "artist[0]Deftones…trackNumber[0]2" + "secret", in Last.fm's order.
        let base: String = p.iter().map(|(k, v)| format!("{k}{v}")).collect::<String>() + "secret";
        assert_eq!(
            lastfm_sign(&p, "secret"),
            format!("{:x}", md5::compute(base))
        );
        p.insert("format".into(), "json".into());
        assert_eq!(
            lastfm_sign(&p, "secret"),
            format!(
                "{:x}",
                md5::compute(
                    p.iter()
                        .filter(|(k, _)| *k != "format")
                        .map(|(k, v)| format!("{k}{v}"))
                        .collect::<String>()
                        + "secret"
                )
            )
        );
    }

    #[test]
    fn lastfm_and_listenbrainz_outcomes() {
        assert_eq!(lastfm_outcome(9), Outcome::AuthFailed);
        assert_eq!(lastfm_outcome(29), Outcome::RetryLater);
        assert_eq!(lastfm_outcome(6), Outcome::Rejected);
        assert_eq!(listenbrainz_outcome(200), Outcome::Sent);
        assert_eq!(listenbrainz_outcome(503), Outcome::RetryLater);
        assert_eq!(listenbrainz_outcome(400), Outcome::Rejected);
    }

    #[test]
    fn listenbrainz_payloads() {
        let single = listenbrainz_payload("single", &[play()]);
        assert_eq!(single["payload"][0]["listened_at"], 1_700_000_000);
        assert_eq!(
            single["payload"][0]["track_metadata"]["release_name"],
            "White Pony"
        );
        assert_eq!(
            single["payload"][0]["track_metadata"]["additional_info"]["duration_ms"],
            255_000
        );
        let now = listenbrainz_payload("playing_now", &[play()]);
        assert!(now["payload"][0].get("listened_at").is_none());
    }
}
