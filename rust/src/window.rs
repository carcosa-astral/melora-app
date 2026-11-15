//! What Melora asks of the operating system's window: the dark title bar (as in 2.x), and hiding
//! to and coming back from the tray. Windows only for now; elsewhere these do nothing.
//! 3.0 uses solid colours only, so there's no Mica (see Design in rust/README.md).

use raw_window_handle::{HasWindowHandle, RawWindowHandle};

/// The native window behind a Slint window, once it exists.
fn hwnd(window: &slint::Window) -> Option<isize> {
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

/// Hides the window, leaving only the tray icon.
pub fn hide(window: &slint::Window) {
    if let Some(h) = hwnd(window) {
        win::hide(h);
    }
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
}
