//! What Melora asks of the operating system's window: the dark title bar (as in 2.x), and hiding
//! to and coming back from the tray. Windows only for now; elsewhere these do nothing.
//! 3.0 uses solid colours only, so there's no Mica (see Design in rust/README.md).

use raw_window_handle::{HasWindowHandle, RawWindowHandle};

/// The native window behind a Slint window, once it exists.
pub fn hwnd(window: &slint::Window) -> Option<isize> {
    let slint_handle = window.window_handle();
    let handle = slint_handle.window_handle().ok()?;
    match handle.as_raw() {
        RawWindowHandle::Win32(h) => Some(h.hwnd.get()),
        _ => None,
    }
}

pub fn apply(window: &slint::Window) {
    if let Some(h) = hwnd(window) {
        win::dark_title_bar(h);
    }
}

/// Minimized to the taskbar (and so due to go to the tray).
pub fn is_minimized(window: &slint::Window) -> bool {
    hwnd(window).is_some_and(win::is_minimized)
}

/// On screen, not minimized, and the window in front: what a click on the tray icon hides.
pub fn is_in_front(window: &slint::Window) -> bool {
    hwnd(window)
        .is_some_and(|h| win::is_visible(h) && !win::is_minimized(h) && win::is_foreground(h))
}

pub fn is_visible(window: &slint::Window) -> bool {
    hwnd(window).is_some_and(win::is_visible)
}

/// Hides the window, leaving only the tray icon.
pub fn hide(window: &slint::Window) {
    if let Some(h) = hwnd(window) {
        win::hide(h);
    }
}

/// Shows a balloon on Melora's tray icon: the one in this process whose hidden window has class
/// `class`, with icon number `id`. False if it couldn't be found.
pub fn tray_balloon(class: &str, id: u32, title: &str, text: &str) -> bool {
    win::tray_balloon(class, id, title, text)
}

/// Brings the window back from the tray (or the taskbar) and to the front.
pub fn show(window: &slint::Window) {
    if let Some(h) = hwnd(window) {
        win::show(h);
    }
}

#[cfg(windows)]
mod win {
    use std::ffi::c_void;

    const DWMWA_USE_IMMERSIVE_DARK_MODE: u32 = 20;
    const SW_HIDE: i32 = 0;
    const SW_SHOW: i32 = 5;
    const SW_RESTORE: i32 = 9;

    #[link(name = "dwmapi", kind = "raw-dylib")]
    unsafe extern "system" {
        fn DwmSetWindowAttribute(
            hwnd: isize,
            attribute: u32,
            value: *const c_void,
            size: u32,
        ) -> i32;
    }

    #[link(name = "user32", kind = "raw-dylib")]
    unsafe extern "system" {
        fn IsIconic(hwnd: isize) -> i32;
        fn IsWindowVisible(hwnd: isize) -> i32;
        fn GetForegroundWindow() -> isize;
        fn ShowWindow(hwnd: isize, cmd: i32) -> i32;
        fn SetForegroundWindow(hwnd: isize) -> i32;
        fn FindWindowExW(
            parent: isize,
            after: isize,
            class: *const u16,
            title: *const u16,
        ) -> isize;
        fn GetWindowThreadProcessId(hwnd: isize, pid: *mut u32) -> u32;
    }

    #[link(name = "shell32", kind = "raw-dylib")]
    unsafe extern "system" {
        fn Shell_NotifyIconW(message: u32, data: *const NotifyIconData) -> i32;
    }

    /// Windows' NOTIFYICONDATAW (the full, current layout).
    #[repr(C)]
    struct NotifyIconData {
        size: u32,
        hwnd: isize,
        id: u32,
        flags: u32,
        callback_message: u32,
        icon: isize,
        tip: [u16; 128],
        state: u32,
        state_mask: u32,
        info: [u16; 256],
        timeout_or_version: u32,
        info_title: [u16; 64],
        info_flags: u32,
        guid: [u8; 16],
        balloon_icon: isize,
    }

    const NIM_MODIFY: u32 = 1;
    const NIF_INFO: u32 = 0x10;

    fn copy_into(dst: &mut [u16], text: &str) {
        let room = dst.len() - 1; // keep the last one as the end marker
        for (d, c) in dst.iter_mut().take(room).zip(text.encode_utf16()) {
            *d = c;
        }
    }

    pub fn tray_balloon(class: &str, id: u32, title: &str, text: &str) -> bool {
        let class: Vec<u16> = class.encode_utf16().chain([0]).collect();
        let me = std::process::id();
        let mut hwnd = 0;
        loop {
            // Several apps can use the same tray library, so take the window that's ours.
            hwnd = unsafe { FindWindowExW(0, hwnd, class.as_ptr(), std::ptr::null()) };
            if hwnd == 0 {
                return false;
            }
            let mut pid = 0;
            unsafe { GetWindowThreadProcessId(hwnd, &mut pid) };
            if pid == me {
                break;
            }
        }
        let mut data = NotifyIconData {
            size: size_of::<NotifyIconData>() as u32,
            hwnd,
            id,
            flags: NIF_INFO,
            callback_message: 0,
            icon: 0,
            tip: [0; 128],
            state: 0,
            state_mask: 0,
            info: [0; 256],
            timeout_or_version: 4000,
            info_title: [0; 64],
            info_flags: 0, // no icon in the balloon, as 2.x
            guid: [0; 16],
            balloon_icon: 0,
        };
        copy_into(&mut data.info, text);
        copy_into(&mut data.info_title, title);
        unsafe { Shell_NotifyIconW(NIM_MODIFY, &data) != 0 }
    }

    #[cfg(test)]
    #[test]
    fn notify_icon_data_has_windows_layout() {
        // NOTIFYICONDATAW is 976 bytes on 64-bit Windows; anything else and Windows ignores the call.
        assert_eq!(size_of::<NotifyIconData>(), 976);
    }

    // SAFETY (all below): plain calls on our own live window handle.

    pub fn dark_title_bar(hwnd: isize) {
        let dark: i32 = 1;
        unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                (&raw const dark).cast(),
                4,
            )
        };
    }

    pub fn is_minimized(hwnd: isize) -> bool {
        unsafe { IsIconic(hwnd) != 0 }
    }

    pub fn is_visible(hwnd: isize) -> bool {
        unsafe { IsWindowVisible(hwnd) != 0 }
    }

    pub fn is_foreground(hwnd: isize) -> bool {
        unsafe { GetForegroundWindow() == hwnd }
    }

    pub fn hide(hwnd: isize) {
        unsafe { ShowWindow(hwnd, SW_HIDE) };
    }

    pub fn show(hwnd: isize) {
        unsafe {
            // Restore a minimized window to how it was (maximized or not); otherwise just show it.
            ShowWindow(
                hwnd,
                if IsIconic(hwnd) != 0 {
                    SW_RESTORE
                } else {
                    SW_SHOW
                },
            );
            SetForegroundWindow(hwnd);
        }
    }
}

#[cfg(not(windows))]
mod win {
    pub fn dark_title_bar(_: isize) {}
    pub fn is_minimized(_: isize) -> bool {
        false
    }
    pub fn is_visible(_: isize) -> bool {
        true
    }
    pub fn is_foreground(_: isize) -> bool {
        true
    }
    pub fn hide(_: isize) {}
    pub fn show(_: isize) {}
    pub fn tray_balloon(_: &str, _: u32, _: &str, _: &str) -> bool {
        false
    }
}
