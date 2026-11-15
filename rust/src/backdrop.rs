//! The dark title bar on Windows (as in Melora 2.x). Elsewhere it does nothing.
//! 3.0 uses solid colours only, so there's no Mica (see Design in rust/README.md).

use raw_window_handle::{HasWindowHandle, RawWindowHandle};

pub fn apply(window: &slint::Window) {
    let slint_handle = window.window_handle();
    let Ok(handle) = slint_handle.window_handle() else {
        return;
    };
    if let RawWindowHandle::Win32(h) = handle.as_raw() {
        win::dark_title_bar(h.hwnd.get());
    }
}

#[cfg(windows)]
mod win {
    use std::ffi::c_void;

    const DWMWA_USE_IMMERSIVE_DARK_MODE: u32 = 20;

    #[link(name = "dwmapi", kind = "raw-dylib")]
    unsafe extern "system" {
        fn DwmSetWindowAttribute(
            hwnd: isize,
            attribute: u32,
            value: *const c_void,
            size: u32,
        ) -> i32;
    }

    pub fn dark_title_bar(hwnd: isize) {
        let dark: i32 = 1;
        // SAFETY: hwnd is our live window; the value is a 4-byte int as DWM expects.
        unsafe {
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_USE_IMMERSIVE_DARK_MODE,
                (&raw const dark).cast(),
                4,
            )
        };
    }
}

#[cfg(not(windows))]
mod win {
    pub fn dark_title_bar(_: isize) {}
}
