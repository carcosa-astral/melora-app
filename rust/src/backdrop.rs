//! Dark title bar and Mica behind the window on Windows 11 (same as Melora 2.x). Elsewhere it does nothing.

use raw_window_handle::{HasWindowHandle, RawWindowHandle};

/// Returns true when Mica is on, so the UI can drop its solid background.
pub fn apply(window: &slint::Window) -> bool {
    let slint_handle = window.window_handle();
    let Ok(handle) = slint_handle.window_handle() else {
        return false;
    };
    let RawWindowHandle::Win32(h) = handle.as_raw() else {
        return false;
    };
    win::apply(h.hwnd.get())
}

#[cfg(windows)]
mod win {
    use std::ffi::c_void;

    const DWMWA_USE_IMMERSIVE_DARK_MODE: u32 = 20;
    const DWMWA_SYSTEMBACKDROP_TYPE: u32 = 38;
    const DWMSBT_MAINWINDOW: i32 = 2; // Mica

    #[repr(C)]
    struct Margins([i32; 4]);

    #[link(name = "dwmapi", kind = "raw-dylib")]
    unsafe extern "system" {
        fn DwmSetWindowAttribute(
            hwnd: isize,
            attribute: u32,
            value: *const c_void,
            size: u32,
        ) -> i32;
        fn DwmExtendFrameIntoClientArea(hwnd: isize, margins: *const Margins) -> i32;
    }

    pub fn apply(hwnd: isize) -> bool {
        let dark: i32 = 1;
        let backdrop = DWMSBT_MAINWINDOW;
        // SAFETY: hwnd is our live window; the values are 4-byte ints as DWM expects.
        unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                (&raw const dark).cast(),
                4,
            );
            if DwmSetWindowAttribute(
                hwnd,
                DWMWA_SYSTEMBACKDROP_TYPE,
                (&raw const backdrop).cast(),
                4,
            ) != 0
            {
                return false; // Windows 10: keep the solid background
            }
            DwmExtendFrameIntoClientArea(hwnd, &Margins([-1; 4]));
        }
        true
    }
}

#[cfg(not(windows))]
mod win {
    pub fn apply(_: isize) -> bool {
        false
    }
}
