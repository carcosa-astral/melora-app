//! Our own small bindings to BASS and BASSmix (DLLs shipped next to the exe), only the calls Melora uses.
//! Everything here is a thin, safe wrapper; the playback logic lives in `engine.rs`.

use std::ffi::c_void;
use std::path::Path;
use std::sync::OnceLock;

pub type Handle = u32;

/// The output's fill callback, called by BASS on its own thread.
pub type StreamProc = unsafe extern "system" fn(Handle, *mut c_void, u32, *mut c_void) -> u32;

const UNICODE: u32 = 0x8000_0000;
pub const SAMPLE_FLOAT: u32 = 0x100;
pub const STREAM_DECODE: u32 = 0x20_0000;
pub const STREAMPROC_END: u32 = 0x8000_0000;
pub const MIXER_END: u32 = 0x1_0000; // the mixer ends when its source does
pub const MIXER_CHAN_NORAMPIN: u32 = 0x80_0000; // no fade-in at the join
const CONFIG_DEV_DEFAULT: u32 = 36;
const CONFIG_NET_PLAYLIST: u32 = 21;
// Tag kinds for BASS_ChannelGetTags.
pub const TAGS_OGG: u32 = 2;
pub const TAGS_HTTP: u32 = 3;
pub const TAGS_ICY: u32 = 4;
pub const TAGS_META: u32 = 5;
const ATTRIB_VOL: u32 = 2;
const ATTRIB_BITRATE: u32 = 12;
const POS_BYTE: u32 = 0;
const ERROR_ALREADY: i32 = 14;
const ACTIVE_PLAYING: u32 = 1;
const DATA_ERROR: u32 = u32::MAX;

#[repr(C)]
#[derive(Default)]
struct ChannelInfo {
    freq: u32,
    chans: u32,
    flags: u32,
    ctype: u32,
    origres: u32,
    plugin: u32,
    sample: u32,
    filename: usize, // a pointer we never read
}

#[link(name = "bass", kind = "raw-dylib")]
unsafe extern "system" {
    fn BASS_SetConfig(option: u32, value: u32) -> i32;
    fn BASS_Init(device: i32, freq: u32, flags: u32, win: *mut c_void, clsid: *const c_void)
    -> i32;
    fn BASS_ErrorGetCode() -> i32;
    fn BASS_PluginLoad(file: *const u16, flags: u32) -> u32;
    fn BASS_StreamCreateFile(
        mem: i32,
        file: *const u16,
        offset: u64,
        length: u64,
        flags: u32,
    ) -> Handle;
    fn BASS_StreamCreate(
        freq: u32,
        chans: u32,
        flags: u32,
        proc_: StreamProc,
        user: *mut c_void,
    ) -> Handle;
    fn BASS_StreamFree(handle: Handle) -> i32;
    fn BASS_ChannelPlay(handle: Handle, restart: i32) -> i32;
    fn BASS_ChannelPause(handle: Handle) -> i32;
    fn BASS_ChannelIsActive(handle: Handle) -> u32;
    fn BASS_ChannelGetInfo(handle: Handle, info: *mut ChannelInfo) -> i32;
    fn BASS_ChannelGetLength(handle: Handle, mode: u32) -> u64;
    fn BASS_ChannelGetPosition(handle: Handle, mode: u32) -> u64;
    fn BASS_ChannelSetPosition(handle: Handle, pos: u64, mode: u32) -> i32;
    fn BASS_ChannelBytes2Seconds(handle: Handle, pos: u64) -> f64;
    fn BASS_ChannelSeconds2Bytes(handle: Handle, secs: f64) -> u64;
    fn BASS_ChannelSetAttribute(handle: Handle, attrib: u32, value: f32) -> i32;
    fn BASS_ChannelGetAttribute(handle: Handle, attrib: u32, value: *mut f32) -> i32;
    fn BASS_ChannelSlideAttribute(handle: Handle, attrib: u32, value: f32, time: u32) -> i32;
    fn BASS_ChannelGetData(handle: Handle, buffer: *mut c_void, length: u32) -> u32;
    fn BASS_StreamCreateURL(
        url: *const u16,
        offset: u32,
        flags: u32,
        proc_: *const c_void,
        user: *mut c_void,
    ) -> Handle;
    fn BASS_ChannelGetTags(handle: Handle, tags: u32) -> *const u8;
}

#[link(name = "bassmix", kind = "raw-dylib")]
unsafe extern "system" {
    fn BASS_Mixer_StreamCreate(freq: u32, chans: u32, flags: u32) -> Handle;
    fn BASS_Mixer_StreamAddChannel(handle: Handle, channel: Handle, flags: u32) -> i32;
}

fn wide(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    path.as_os_str().encode_wide().chain([0]).collect()
}

fn error_code() -> i32 {
    // SAFETY: no arguments; reads BASS's per-thread error code.
    unsafe { BASS_ErrorGetCode() }
}

/// Starts BASS once per process: `device` -1 is Windows' default output (followed when it
/// changes), 0 is BASS's "no sound" device that tests use. Loads the format plugins from `dir`.
pub fn init(device: i32, dir: &Path) -> Result<(), String> {
    static STARTED: OnceLock<Result<(), String>> = OnceLock::new();
    STARTED
        .get_or_init(|| {
            // SAFETY: plain calls with valid arguments; the wide strings live through each call.
            unsafe {
                BASS_SetConfig(CONFIG_DEV_DEFAULT, 1); // must come before Init
                if BASS_Init(device, 44_100, 0, std::ptr::null_mut(), std::ptr::null()) == 0
                    && error_code() != ERROR_ALREADY
                {
                    return Err(format!(
                        "couldn't start the sound output (BASS error {})",
                        error_code()
                    ));
                }
                // bass_aac decodes AAC itself; without it BASS falls back to Media Foundation, which is slow.
                // Let pasted .pls / .m3u station links resolve to the stream inside them.
                BASS_SetConfig(CONFIG_NET_PLAYLIST, 1);
                // basshls plays HLS (.m3u8) radio such as the BBC's.
                for plugin in [
                    "bassflac.dll",
                    "bassopus.dll",
                    "bass_aac.dll",
                    "basshls.dll",
                ] {
                    if BASS_PluginLoad(wide(&dir.join(plugin)).as_ptr(), UNICODE) == 0
                        && error_code() != ERROR_ALREADY
                    {
                        return Err(format!(
                            "couldn't load {plugin} (BASS error {})",
                            error_code()
                        ));
                    }
                }
            }
            Ok(())
        })
        .clone()
}

/// Opens a file as a float decoder (it doesn't play by itself). 0 if BASS can't read it.
pub fn open_decoder(path: &Path) -> Handle {
    // SAFETY: the path is a valid NUL-terminated wide string for the duration of the call.
    unsafe {
        BASS_StreamCreateFile(
            0,
            wide(path).as_ptr(),
            0,
            0,
            UNICODE | STREAM_DECODE | SAMPLE_FLOAT,
        )
    }
}

/// An output stream that BASS fills by calling `fill` with `user`.
pub fn create_output(freq: u32, chans: u32, fill: StreamProc, user: *mut c_void) -> Handle {
    // SAFETY: the caller keeps `user` alive until the stream is freed.
    unsafe { BASS_StreamCreate(freq, chans, SAMPLE_FLOAT, fill, user) }
}

/// A decoding mixer in the given format with just `decoder` in it: converts sample rate and channels.
pub fn converter(decoder: Handle, freq: u32, chans: u32) -> Handle {
    // SAFETY: plain calls on handles we own.
    unsafe {
        let mixer = BASS_Mixer_StreamCreate(freq, chans, STREAM_DECODE | SAMPLE_FLOAT | MIXER_END);
        if mixer == 0 {
            return 0;
        }
        if BASS_Mixer_StreamAddChannel(mixer, decoder, MIXER_CHAN_NORAMPIN) != 0 {
            return mixer;
        }
        BASS_StreamFree(mixer);
        0
    }
}

pub fn free(h: Handle) {
    // SAFETY: freeing an unknown or already freed handle only returns an error.
    unsafe { BASS_StreamFree(h) };
}

pub fn play(h: Handle) -> bool {
    // SAFETY: plain call.
    unsafe { BASS_ChannelPlay(h, 0) != 0 }
}

pub fn pause(h: Handle) {
    // SAFETY: plain call.
    unsafe { BASS_ChannelPause(h) };
}

pub fn is_playing(h: Handle) -> bool {
    // SAFETY: plain call.
    unsafe { BASS_ChannelIsActive(h) == ACTIVE_PLAYING }
}

/// True once a channel has stopped by itself (played to its end).
pub fn is_stopped(h: Handle) -> bool {
    // SAFETY: plain call. 0 is BASS_ACTIVE_STOPPED.
    unsafe { BASS_ChannelIsActive(h) == 0 }
}

/// Sample rate, channel count and BASS's codec type of a channel.
pub fn info(h: Handle) -> (u32, u32, u32) {
    let mut i = ChannelInfo::default();
    // SAFETY: `i` is a correctly laid out BASS_CHANNELINFO.
    unsafe { BASS_ChannelGetInfo(h, &mut i) };
    (i.freq, i.chans, i.ctype)
}

pub fn length_seconds(h: Handle) -> f64 {
    // SAFETY: plain calls.
    unsafe { BASS_ChannelBytes2Seconds(h, BASS_ChannelGetLength(h, POS_BYTE)) }
}

pub fn position_bytes(h: Handle) -> u64 {
    // SAFETY: plain call.
    unsafe { BASS_ChannelGetPosition(h, POS_BYTE) }
}

pub fn bytes_to_seconds(h: Handle, bytes: u64) -> f64 {
    // SAFETY: plain call.
    unsafe { BASS_ChannelBytes2Seconds(h, bytes) }
}

/// Moves a decoder to `seconds` and returns where it actually landed.
pub fn seek_seconds(h: Handle, seconds: f64) -> f64 {
    // SAFETY: plain calls.
    unsafe {
        BASS_ChannelSetPosition(h, BASS_ChannelSeconds2Bytes(h, seconds), POS_BYTE);
        BASS_ChannelBytes2Seconds(h, BASS_ChannelGetPosition(h, POS_BYTE))
    }
}

pub fn set_volume(h: Handle, v: f32) {
    // SAFETY: plain call.
    unsafe { BASS_ChannelSetAttribute(h, ATTRIB_VOL, v) };
}

pub fn slide_volume(h: Handle, v: f32, ms: u32) {
    // SAFETY: plain call.
    unsafe { BASS_ChannelSlideAttribute(h, ATTRIB_VOL, v, ms) };
}

/// Reads up to `buffer.len()` bytes of decoded samples; 0 once the channel has ended.
pub fn read(h: Handle, buffer: &mut [u8]) -> usize {
    // SAFETY: BASS writes at most `len` bytes into the buffer we pass.
    let got = unsafe { BASS_ChannelGetData(h, buffer.as_mut_ptr().cast(), buffer.len() as u32) };
    if got == DATA_ERROR { 0 } else { got as usize }
}

/// Connects to an internet stream (blocking, so call it off the UI thread). The stream starts
/// buffering but not playing. On failure, a sentence saying why.
pub fn open_url(url: &str, decode_only: bool) -> Result<Handle, String> {
    let wide: Vec<u16> = url.encode_utf16().chain([0]).collect();
    let flags = UNICODE | SAMPLE_FLOAT | if decode_only { STREAM_DECODE } else { 0 };
    // SAFETY: the URL is a valid NUL-terminated wide string for the call; no download callback.
    let h = unsafe {
        BASS_StreamCreateURL(
            wide.as_ptr(),
            0,
            flags,
            std::ptr::null(),
            std::ptr::null_mut(),
        )
    };
    if h != 0 {
        return Ok(h);
    }
    // The same sentences as 2.x.
    Err(match error_code() {
        40 => "The station didn't answer in time.".into(),
        2 => "Couldn't connect to that address.".into(),
        41 | 44 => "That address isn't an audio stream Melora can play.".into(),
        32 => "No internet connection.".into(),
        10 => "The secure connection failed.".into(),
        47 => "That stream can't be played live.".into(),
        e => format!("Couldn't play the stream (BASS error {e})."),
    })
}

/// A channel's tags of one kind, as lines. ICY, HTTP and Ogg tags are a list; META is one line.
pub fn tags(h: Handle, kind: u32) -> Vec<String> {
    // SAFETY: BASS returns a pointer to tags it owns (or null), valid until the channel changes;
    // we copy them out at once, reading each NUL-terminated string and stopping at an empty one.
    unsafe {
        let mut p = BASS_ChannelGetTags(h, kind);
        let mut out = Vec::new();
        while !p.is_null() && *p != 0 {
            let s = std::ffi::CStr::from_ptr(p.cast());
            out.push(String::from_utf8_lossy(s.to_bytes()).into_owned());
            if kind == TAGS_META {
                break;
            }
            p = p.add(s.to_bytes().len() + 1);
        }
        out
    }
}

/// The bitrate BASS reports for a channel, in kbps; 0 when it doesn't know.
pub fn bitrate(h: Handle) -> u32 {
    let mut kbps = 0.0f32;
    // SAFETY: BASS writes one float to the pointer we pass.
    unsafe { BASS_ChannelGetAttribute(h, ATTRIB_BITRATE, &mut kbps) };
    kbps.round().max(0.0) as u32
}

/// A short codec name for BASS's channel type.
pub fn codec_name(ctype: u32) -> &'static str {
    match ctype {
        0x10003..=0x10005 => "MP3",
        0x10b00 | 0x10b01 | 0x10008 => "AAC",
        0x10002 => "Vorbis",
        0x11200 => "Opus",
        0x10900 | 0x10901 => "FLAC",
        0x50001 | 0x50003 => "WAV",
        _ => "",
    }
}
