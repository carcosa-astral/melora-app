//! Our own small bindings to BASS (bass.dll, shipped next to the exe), only the calls Melora uses.

use std::ffi::{c_char, c_void};
use std::os::windows::ffi::OsStrExt;
use std::path::Path;

pub type Handle = u32;

const UNICODE: u32 = 0x8000_0000;
const CONFIG_DEV_DEFAULT: u32 = 36;
const ATTRIB_VOL: u32 = 2;
const POS_BYTE: u32 = 0;
pub const ACTIVE_PLAYING: u32 = 1;

#[repr(C)]
pub struct ChannelInfo {
    pub freq: u32,
    pub chans: u32,
    flags: u32,
    ctype: u32,
    origres: u32,
    plugin: u32,
    sample: u32,
    filename: *const c_char,
}

#[link(name = "bass", kind = "raw-dylib")]
unsafe extern "system" {
    fn BASS_SetConfig(option: u32, value: u32) -> i32;
    fn BASS_Init(device: i32, freq: u32, flags: u32, win: *mut c_void, clsid: *const c_void)
    -> i32;
    fn BASS_PluginLoad(file: *const u16, flags: u32) -> u32;
    fn BASS_StreamCreateFile(
        mem: i32,
        file: *const u16,
        offset: u64,
        length: u64,
        flags: u32,
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
    fn BASS_ChannelSlideAttribute(handle: Handle, attrib: u32, value: f32, time: u32) -> i32;
}

fn wide(path: &Path) -> Vec<u16> {
    path.as_os_str().encode_wide().chain([0]).collect()
}

/// Starts the default output device (following Windows when it changes) and loads the format plugins.
pub fn init(dir: &Path) -> bool {
    // SAFETY: plain calls into bass.dll with valid, NUL-terminated wide strings.
    unsafe {
        BASS_SetConfig(CONFIG_DEV_DEFAULT, 1);
        if BASS_Init(-1, 44_100, 0, std::ptr::null_mut(), std::ptr::null()) == 0 {
            return false;
        }
        for plugin in ["bassflac.dll", "bassopus.dll", "bass_aac.dll"] {
            BASS_PluginLoad(wide(&dir.join(plugin)).as_ptr(), UNICODE);
        }
    }
    true
}

/// A playing file. Freed when dropped.
pub struct Stream(Handle);

impl Stream {
    pub fn open(path: &Path) -> Option<Stream> {
        // SAFETY: the path is a valid NUL-terminated wide string for the duration of the call.
        let h = unsafe { BASS_StreamCreateFile(0, wide(path).as_ptr(), 0, 0, UNICODE) };
        (h != 0).then_some(Stream(h))
    }

    pub fn info(&self) -> (u32, u32) {
        let mut info = ChannelInfo {
            freq: 0,
            chans: 0,
            flags: 0,
            ctype: 0,
            origres: 0,
            plugin: 0,
            sample: 0,
            filename: std::ptr::null(),
        };
        // SAFETY: info is a correctly laid out BASS_CHANNELINFO.
        unsafe { BASS_ChannelGetInfo(self.0, &mut info) };
        (info.freq, info.chans)
    }

    pub fn play(&self) {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelPlay(self.0, 0) };
    }

    pub fn pause(&self) {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelPause(self.0) };
    }

    pub fn is_playing(&self) -> bool {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelIsActive(self.0) == ACTIVE_PLAYING }
    }

    pub fn length(&self) -> f64 {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelBytes2Seconds(self.0, BASS_ChannelGetLength(self.0, POS_BYTE)) }
    }

    pub fn position(&self) -> f64 {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelBytes2Seconds(self.0, BASS_ChannelGetPosition(self.0, POS_BYTE)) }
    }

    pub fn seek(&self, seconds: f64) {
        // SAFETY: valid stream handle.
        unsafe {
            BASS_ChannelSetPosition(self.0, BASS_ChannelSeconds2Bytes(self.0, seconds), POS_BYTE)
        };
    }

    pub fn set_volume(&self, v: f32) {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelSetAttribute(self.0, ATTRIB_VOL, v) };
    }

    pub fn slide_volume(&self, v: f32, ms: u32) {
        // SAFETY: valid stream handle.
        unsafe { BASS_ChannelSlideAttribute(self.0, ATTRIB_VOL, v, ms) };
    }
}

impl Drop for Stream {
    fn drop(&mut self) {
        // SAFETY: we own the handle and free it once.
        unsafe { BASS_StreamFree(self.0) };
    }
}
