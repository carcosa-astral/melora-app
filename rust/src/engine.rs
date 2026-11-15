//! Gapless file playback, ported from Melora 2.x's AudioEngine.
//!
//! Files don't play directly. Each one is opened as a decoder, and a single output stream pulls
//! samples from it. When a decoder runs dry and the next track has been queued with `set_next`,
//! the output carries straight on into it on the very next sample, which is what makes playback
//! gapless. The output keeps a list of where in its own stream each track starts, so the position
//! and "now playing" follow what you hear rather than what's been decoded.
//!
//! The engine is owned by the UI thread and calls nothing back: the UI polls `tick`.

use std::ffi::c_void;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use crate::bass::{self, Handle};
use crate::radio;

/// Pausing and stopping fade out over this long, and resuming fades back in, so neither clicks.
pub const FADE_MS: u32 = 150;

/// A track feeding the output.
struct Segment {
    decoder: Handle,
    /// What the output reads: the decoder, or a BASSmix converter around it when its format differs.
    source: Handle,
    path: PathBuf,
    /// Output byte at which this track starts.
    output_start: u64,
    /// Seconds into the track at `output_start` (non-zero after a seek).
    start_seconds: f64,
}

impl Segment {
    fn is_converted(&self) -> bool {
        self.source != self.decoder
    }
}

impl Drop for Segment {
    fn drop(&mut self) {
        if self.is_converted() {
            bass::free(self.source);
        }
        bass::free(self.decoder);
    }
}

/// What the fill callback (on a BASS thread) shares with the engine. Never call BASS on the
/// output stream while holding its lock: freeing the output waits for the callback, which waits
/// for the lock.
#[derive(Default)]
struct Feed {
    /// [0] is what's heard; the callback reads the last one.
    segments: Vec<Segment>,
    next: Option<Segment>,
    written: u64,
    drained: bool,
}

enum Pending {
    Pause,
    #[cfg_attr(not(test), allow(dead_code))]
    Stop,
}

pub struct Engine {
    feed: Arc<Mutex<Feed>>,
    stream: Handle,
    freq: u32,
    chans: u32,
    volume: f32,
    /// A pause or stop that's fading out already counts as done for everyone asking.
    pending: Option<(Instant, Pending)>,
    /// True when `stream` is an internet stream (radio) rather than the file output.
    live: bool,
}

fn lock(feed: &Mutex<Feed>) -> MutexGuard<'_, Feed> {
    // A poisoned lock only means a panic elsewhere; the data is still usable.
    feed.lock().unwrap_or_else(|e| e.into_inner())
}

/// The output's fill callback: copies from the decoders, moving on to the next track when one runs dry.
unsafe extern "system" fn fill(
    _: Handle,
    buffer: *mut c_void,
    length: u32,
    user: *mut c_void,
) -> u32 {
    // SAFETY: `user` is the engine's Feed, which outlives the output stream (freed first), and
    // BASS hands us a writable buffer of `length` bytes.
    let (feed, out) = unsafe {
        (
            &*(user as *const Mutex<Feed>),
            std::slice::from_raw_parts_mut(buffer.cast::<u8>(), length as usize),
        )
    };
    let mut f = lock(feed);
    let mut done = 0;
    while done < out.len() {
        let Some(last) = f.segments.last() else { break };
        let got = bass::read(last.source, &mut out[done..]);
        if got > 0 {
            done += got;
            continue;
        }
        // This track is used up: carry on into the queued one, or finish.
        match f.next.take() {
            Some(mut next) => {
                next.output_start = f.written + done as u64;
                f.segments.push(next);
            }
            None => {
                f.drained = true;
                break;
            }
        }
    }
    f.written += done as u64;
    if f.drained {
        done as u32 | bass::STREAMPROC_END
    } else {
        done as u32
    }
}

impl Engine {
    pub fn new() -> Engine {
        Engine {
            feed: Arc::default(),
            stream: 0,
            freq: 0,
            chans: 0,
            volume: 1.0,
            pending: None,
            live: false,
        }
    }

    /// Plays an internet stream already connected with `bass::open_url`; the engine owns it now.
    pub fn play_live(&mut self, handle: Handle) -> bool {
        self.free_stream();
        self.stream = handle;
        self.live = true;
        bass::set_volume(handle, self.volume);
        bass::play(handle)
    }

    pub fn is_live(&self) -> bool {
        self.live && self.stream != 0
    }

    /// What a live stream says is playing: its ICY title, or its Ogg comments.
    pub fn stream_title(&self) -> Option<String> {
        if !self.is_live() {
            return None;
        }
        let meta = bass::tags(self.stream, bass::TAGS_META);
        meta.first().and_then(|m| radio::icy_title(m)).or_else(|| {
            radio::from_vorbis_comments(
                bass::tags(self.stream, bass::TAGS_OGG)
                    .iter()
                    .map(String::as_str),
            )
        })
    }

    /// Starts playing a file. False if BASS can't open it.
    pub fn play(&mut self, path: &Path) -> bool {
        self.free_stream();
        let decoder = bass::open_decoder(path);
        if decoder == 0 {
            return false;
        }
        (self.freq, self.chans, _) = bass::info(decoder);
        {
            let mut f = lock(&self.feed);
            f.segments.push(Segment {
                decoder,
                source: decoder,
                path: path.to_path_buf(),
                output_start: 0,
                start_seconds: 0.0,
            });
            f.written = 0;
            f.drained = false;
        }
        self.create_output() && bass::play(self.stream)
    }

    /// Queues a file to play straight after the current one, without a gap; None clears it. The
    /// output can't change format mid-stream, so a file in another sample rate or channel layout is
    /// converted to the output's as it plays. False if the file can't be opened.
    pub fn set_next(&mut self, path: Option<&Path>) -> bool {
        if self.stream == 0 || self.live {
            return false;
        }
        let mut next = path.and_then(|path| {
            let decoder = bass::open_decoder(path);
            if decoder == 0 {
                return None;
            }
            let (freq, chans, _) = bass::info(decoder);
            let source = if (freq, chans) == (self.freq, self.chans) {
                decoder
            } else {
                bass::converter(decoder, self.freq, self.chans)
            };
            if source == 0 {
                bass::free(decoder);
                return None;
            }
            Some(Segment {
                decoder,
                source,
                path: path.to_path_buf(),
                output_start: 0,
                start_seconds: 0.0,
            })
        });
        let queued = next.is_some();
        let replaced = {
            let mut f = lock(&self.feed);
            // Too late once the output has already finished with the last track.
            let late = f.drained.then(|| next.take()).flatten();
            (std::mem::replace(&mut f.next, next), late)
        };
        let late = replaced.1.is_some();
        drop(replaced); // frees outside the lock
        queued && !late
    }

    /// Call regularly: completes fades that are due, and returns true once playback has moved on
    /// into the queued track, which from then on is the current one.
    pub fn tick(&mut self) -> bool {
        if self
            .pending
            .as_ref()
            .is_some_and(|(due, _)| Instant::now() >= *due)
        {
            match self.pending.take() {
                Some((_, Pending::Pause)) => bass::pause(self.stream),
                Some((_, Pending::Stop)) => self.free_stream(),
                None => {}
            }
        }
        if self.stream == 0 || self.pending.is_some() || self.live {
            return false;
        }
        let heard = bass::position_bytes(self.stream);
        let finished = {
            let mut f = lock(&self.feed);
            (f.segments.len() > 1 && heard >= f.segments[1].output_start)
                .then(|| f.segments.remove(0))
        };
        finished.is_some()
    }

    /// True once the last track has played to its end with nothing queued after it, or a live
    /// stream has dropped.
    pub fn has_ended(&self) -> bool {
        self.stream != 0
            && self.pending.is_none()
            && (self.live || lock(&self.feed).drained)
            && bass::is_stopped(self.stream)
    }

    /// The file being heard right now.
    #[cfg_attr(not(test), allow(dead_code))] // wired to the UI with the full transport in phase 2
    pub fn playing_path(&self) -> Option<PathBuf> {
        lock(&self.feed).segments.first().map(|s| s.path.clone())
    }

    pub fn is_loaded(&self) -> bool {
        self.stream != 0 && !matches!(self.pending, Some((_, Pending::Stop)))
    }

    pub fn is_playing(&self) -> bool {
        self.is_loaded() && self.pending.is_none() && bass::is_playing(self.stream)
    }

    /// Fades out, then pauses (on the next `tick` after the fade). `is_playing` is false at once.
    pub fn pause(&mut self) {
        if !self.is_playing() {
            return;
        }
        bass::slide_volume(self.stream, 0.0, FADE_MS);
        self.pending = Some((
            Instant::now() + Duration::from_millis(FADE_MS.into()),
            Pending::Pause,
        ));
    }

    /// Carries on after a pause (or one still fading out), fading back in.
    pub fn resume(&mut self) {
        if !self.is_loaded() {
            return;
        }
        self.pending = None;
        if !bass::is_playing(self.stream) {
            bass::play(self.stream);
        }
        bass::slide_volume(self.stream, self.volume, FADE_MS);
    }

    /// Stops at once.
    pub fn stop(&mut self) {
        self.free_stream();
    }

    /// Stops after a short fade-out; it counts as stopped at once (`is_loaded` is false).
    pub fn fade_out_and_stop(&mut self) {
        if !self.is_playing() {
            self.stop();
            return;
        }
        bass::slide_volume(self.stream, 0.0, FADE_MS);
        self.pending = Some((
            Instant::now() + Duration::from_millis(FADE_MS.into()),
            Pending::Stop,
        ));
    }

    /// Seconds into the track being heard (for a live stream: time listened).
    pub fn position(&self) -> f64 {
        if self.stream == 0 {
            return 0.0;
        }
        let heard = bass::position_bytes(self.stream);
        if self.live {
            return bass::bytes_to_seconds(self.stream, heard);
        }
        let f = lock(&self.feed);
        f.segments.first().map_or(0.0, |s| {
            s.start_seconds
                + bass::bytes_to_seconds(self.stream, heard.saturating_sub(s.output_start))
        })
    }

    /// Length of the track being heard, in seconds; 0 for a live stream.
    pub fn length(&self) -> f64 {
        if self.live {
            return 0.0;
        }
        lock(&self.feed)
            .segments
            .first()
            .map_or(0.0, |s| bass::length_seconds(s.decoder))
    }

    /// Codec, sample rate and channels of the track being heard (its own, not the output's).
    pub fn format(&self) -> Option<(&'static str, u32, u32)> {
        let handle = if self.is_live() {
            self.stream
        } else {
            lock(&self.feed).segments.first()?.decoder
        };
        let (freq, chans, ctype) = bass::info(handle);
        Some((bass::codec_name(ctype), freq, chans))
    }

    /// Volume from 0 to 1, kept across tracks.
    pub fn set_volume(&mut self, v: f32) {
        self.volume = v.clamp(0.0, 1.0);
        // Not while paused or fading: the volume is faded to 0 then, and `resume` fades in to the new level.
        if self.is_playing() {
            bass::set_volume(self.stream, self.volume);
        }
    }

    /// Jumps within the track being heard. A user stream can't seek, so the output is rebuilt
    /// (dropping what it had buffered), in the track's own format if it was being converted, and
    /// the queued track is reopened from its start.
    pub fn seek(&mut self, seconds: f64) {
        if self.stream == 0 || self.live {
            return;
        }
        let was_playing = self.is_playing();
        bass::free(self.stream); // also stops the fill callback
        self.stream = 0;

        let (requeue, dropped) = {
            let mut f = lock(&self.feed);
            let mut dropped: Vec<Segment> = f.segments.drain(1..).collect();
            dropped.extend(f.next.take());
            let requeue = dropped.first().map(|s| s.path.clone());
            let Some(current) = f.segments.first_mut() else {
                return;
            };
            if current.is_converted() {
                bass::free(current.source);
                current.source = current.decoder;
                (self.freq, self.chans, _) = bass::info(current.decoder);
            }
            current.start_seconds = bass::seek_seconds(current.decoder, seconds.max(0.0));
            current.output_start = 0;
            f.written = 0;
            f.drained = false;
            (requeue, dropped)
        };
        drop(dropped);

        if !self.create_output() {
            return;
        }
        if let Some(path) = requeue {
            self.set_next(Some(&path));
        }
        if was_playing {
            bass::play(self.stream);
        }
    }

    fn create_output(&mut self) -> bool {
        let user = Arc::as_ptr(&self.feed).cast_mut().cast::<c_void>();
        self.stream = bass::create_output(self.freq, self.chans, fill, user);
        if self.stream == 0 {
            return false;
        }
        bass::set_volume(self.stream, self.volume);
        true
    }

    fn free_stream(&mut self) {
        if self.stream != 0 {
            bass::free(self.stream); // first, so the fill callback has stopped
        }
        self.stream = 0;
        self.pending = None;
        self.live = false;
        let old = std::mem::take(&mut *lock(&self.feed));
        drop(old); // frees the decoders outside the lock
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.free_stream();
    }
}

#[cfg(test)]
mod tests {
    //! The gapless hand-over, seeking and fades, on BASS's "no sound" device: it plays in real
    //! time like a sound card, so no audio hardware is needed.

    use super::*;
    use std::thread::sleep;

    struct TempDir(PathBuf);

    impl TempDir {
        fn new() -> TempDir {
            static COUNT: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);
            let n = COUNT.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
            let dir = std::env::temp_dir().join(format!("melora-rs-{}-{n}", std::process::id()));
            std::fs::create_dir_all(&dir).expect("temp dir");
            TempDir(dir)
        }

        /// A WAV file of silence, 16-bit.
        fn wav(&self, name: &str, seconds: f64, rate: u32, channels: u16) -> PathBuf {
            let frame = u32::from(channels) * 2;
            let bytes = (seconds * f64::from(rate)) as u32 * frame;
            let mut w = Vec::new();
            w.extend(b"RIFF");
            w.extend((36 + bytes).to_le_bytes());
            w.extend(b"WAVEfmt ");
            w.extend(16u32.to_le_bytes());
            w.extend(1u16.to_le_bytes());
            w.extend(channels.to_le_bytes());
            w.extend(rate.to_le_bytes());
            w.extend((rate * frame).to_le_bytes());
            w.extend((frame as u16).to_le_bytes());
            w.extend(16u16.to_le_bytes());
            w.extend(b"data");
            w.extend(bytes.to_le_bytes());
            w.resize(w.len() + bytes as usize, 0);
            let path = self.0.join(name);
            std::fs::write(&path, w).expect("write wav");
            path
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn engine() -> Engine {
        // The plugins load by full path; bass.dll itself is found through cargo's PATH.
        init_or_panic(&Path::new(env!("CARGO_MANIFEST_DIR")).join("../lib/bass"));
        Engine::new()
    }

    fn init_or_panic(dir: &Path) {
        if let Err(e) = bass::init(0, dir) {
            panic!("{e}");
        }
    }

    /// Waits (ticking, as the UI timer does) for a condition, up to 5 s.
    fn eventually(e: &mut Engine, mut done: impl FnMut(&mut Engine) -> bool) -> bool {
        let start = Instant::now();
        while start.elapsed() < Duration::from_secs(5) {
            if done(e) {
                return true;
            }
            sleep(Duration::from_millis(20));
        }
        false
    }

    /// The position stays put for a moment, i.e. the fade-out has ended in a pause.
    fn holds(e: &mut Engine) -> bool {
        e.tick();
        let before = e.position();
        sleep(Duration::from_millis(150));
        (e.position() - before).abs() < 0.005
    }

    #[test]
    fn queued_track_takes_over_when_the_first_ends() {
        let (dir, mut e) = (TempDir::new(), engine());
        let (a, b) = (
            dir.wav("a.wav", 0.5, 44_100, 2),
            dir.wav("b.wav", 2.0, 44_100, 2),
        );
        assert!(e.play(&a));
        assert!(e.set_next(Some(&b)));
        assert!(eventually(&mut e, Engine::tick));
        assert_eq!(e.playing_path(), Some(b));
        assert!((1.9..2.1).contains(&e.length()));
        assert!((0.0..1.0).contains(&e.position())); // counted from the second track's start
    }

    #[test]
    fn seeking_near_the_end_keeps_the_next_track_queued() {
        let (dir, mut e) = (TempDir::new(), engine());
        let (a, b) = (
            dir.wav("a.wav", 3.0, 44_100, 2),
            dir.wav("b.wav", 2.0, 44_100, 2),
        );
        assert!(e.play(&a));
        assert!(e.set_next(Some(&b)));
        e.seek(2.6);
        assert!((2.5..2.8).contains(&e.position()));
        assert!(eventually(&mut e, Engine::tick));
        assert_eq!(e.playing_path(), Some(b));
    }

    #[test]
    fn a_track_in_another_format_is_converted_and_follows_on() {
        for (rate, channels) in [(48_000, 2), (44_100, 1)] {
            let (dir, mut e) = (TempDir::new(), engine());
            let (a, b) = (
                dir.wav("a.wav", 0.5, 44_100, 2),
                dir.wav("b.wav", 3.0, rate, channels),
            );
            assert!(e.play(&a));
            assert!(e.set_next(Some(&b)));
            assert!(eventually(&mut e, Engine::tick), "{rate} Hz, {channels} ch");
            assert_eq!(e.playing_path(), Some(b.clone()));
            assert!((2.9..3.1).contains(&e.length()));
            assert_eq!(
                e.format().map(|f| (f.1, f.2)),
                Some((rate, u32::from(channels)))
            );

            // Seeking in a converted track rebuilds the output in its own format; the position holds.
            e.seek(2.0);
            assert!((1.9..2.3).contains(&e.position()));
            assert_eq!(e.playing_path(), Some(b));
        }
    }

    #[test]
    fn the_last_track_ends() {
        let (dir, mut e) = (TempDir::new(), engine());
        assert!(e.play(&dir.wav("a.wav", 0.3, 44_100, 2)));
        assert!(eventually(&mut e, |e| e.has_ended()));
    }

    #[test]
    fn pause_fades_out_then_holds_and_resume_carries_on() {
        let (dir, mut e) = (TempDir::new(), engine());
        assert!(e.play(&dir.wav("a.wav", 5.0, 44_100, 2)));
        sleep(Duration::from_millis(300));
        e.pause();
        assert!(!e.is_playing()); // straight away, while it fades
        assert!(eventually(&mut e, holds));
        let held = e.position();
        e.resume();
        assert!(e.is_playing());
        assert!(eventually(&mut e, |e| e.position() > held + 0.1));
    }

    #[test]
    fn resuming_during_the_fade_keeps_playing() {
        let (dir, mut e) = (TempDir::new(), engine());
        assert!(e.play(&dir.wav("a.wav", 5.0, 44_100, 2)));
        e.pause();
        e.resume();
        sleep(Duration::from_millis(400)); // past the fade, which must not pause it now
        e.tick();
        assert!(e.is_playing());
        assert!(!holds(&mut e));
    }

    #[test]
    fn fade_out_and_stop_counts_as_stopped_at_once() {
        let (dir, mut e) = (TempDir::new(), engine());
        assert!(e.play(&dir.wav("a.wav", 5.0, 44_100, 2)));
        e.fade_out_and_stop();
        assert!(!e.is_loaded());
        assert!(!e.is_playing());
        assert!(eventually(&mut e, |e| {
            e.tick();
            e.playing_path().is_none() // freed once faded
        }));
    }
}
