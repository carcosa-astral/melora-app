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

/// Opens a web page in the default browser.
pub fn open_url(url: &str) {
    // shortcut: Windows only (explorer hands it to the browser); macOS and Linux would use open and xdg-open.
    let _ = std::process::Command::new("explorer").arg(url).spawn();
}

/// Where the window is and whether it's maximized, as "left,top,right,bottom,maximized"
/// (screen pixels; the size it has when not maximized), to save between runs.
pub fn placement(window: &slint::Window) -> Option<String> {
    hwnd(window).and_then(win::placement)
}

/// Puts the window back where `placement` found it. Windows moves it onto a screen if that one
/// is gone.
pub fn set_placement(window: &slint::Window, saved: &str) {
    let n: Vec<i32> = saved
        .split(',')
        .filter_map(|s| s.trim().parse().ok())
        .collect();
    if let (Some(h), &[l, t, r, b, max]) = (hwnd(window), &n[..])
        && r - l >= 200
        && b - t >= 150
    {
        win::set_placement(h, [l, t, r, b], max != 0);
    }
}

/// One Melora per folder, as 2.x: true for the first. A second one asks the first to come to
/// the front (it notices in `show_requested`) and should then quit.
pub fn first_instance(id: &str) -> bool {
    win::first_instance(id)
}

/// Whether another launch asked this Melora to come to the front since the last call.
pub fn show_requested() -> bool {
    win::show_requested()
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
        fn GetWindowPlacement(hwnd: isize, wp: *mut WindowPlacement) -> i32;
        fn SetWindowPlacement(hwnd: isize, wp: *const WindowPlacement) -> i32;
        fn AllowSetForegroundWindow(pid: u32) -> i32;
    }

    #[link(name = "kernel32", kind = "raw-dylib")]
    unsafe extern "system" {
        fn CreateMutexW(attributes: *const c_void, owned: i32, name: *const u16) -> isize;
        fn CreateEventW(
            attributes: *const c_void,
            manual: i32,
            initial: i32,
            name: *const u16,
        ) -> isize;
        fn SetEvent(event: isize) -> i32;
        fn WaitForSingleObject(handle: isize, ms: u32) -> u32;
        fn GetLastError() -> u32;
    }

    /// Windows' WINDOWPLACEMENT.
    #[repr(C)]
    #[derive(Default)]
    struct WindowPlacement {
        length: u32,
        flags: u32,
        show_cmd: u32,
        min_position: [i32; 2],
        max_position: [i32; 2],
        normal: [i32; 4],
    }

    const SW_SHOWNORMAL: u32 = 1;
    const SW_SHOWMAXIMIZED: u32 = 3;
    const WPF_RESTORETOMAXIMIZED: u32 = 2;
    const ERROR_ALREADY_EXISTS: u32 = 183;
    const ASFW_ANY: u32 = u32::MAX;

    pub fn placement(hwnd: isize) -> Option<String> {
        let mut wp = WindowPlacement {
            length: size_of::<WindowPlacement>() as u32,
            ..Default::default()
        };
        if unsafe { GetWindowPlacement(hwnd, &mut wp) } == 0 {
            return None;
        }
        // Minimized (or in the tray) from maximized still counts as maximized.
        let max = wp.show_cmd == SW_SHOWMAXIMIZED || wp.flags & WPF_RESTORETOMAXIMIZED != 0;
        let [l, t, r, b] = wp.normal;
        Some(format!("{l},{t},{r},{b},{}", u8::from(max)))
    }

    pub fn set_placement(hwnd: isize, normal: [i32; 4], max: bool) {
        let wp = WindowPlacement {
            length: size_of::<WindowPlacement>() as u32,
            show_cmd: if max { SW_SHOWMAXIMIZED } else { SW_SHOWNORMAL },
            normal,
            ..Default::default()
        };
        unsafe { SetWindowPlacement(hwnd, &wp) };
    }

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain([0]).collect()
    }

    // The "come to the front" signal, made by the first Melora (0 in any other).
    static SHOW: std::sync::atomic::AtomicIsize = std::sync::atomic::AtomicIsize::new(0);

    pub fn first_instance(id: &str) -> bool {
        // The mutex is never closed: Windows lets go of it when Melora exits.
        let mutex = unsafe { CreateMutexW(std::ptr::null(), 0, wide(id).as_ptr()) };
        let first = mutex == 0 || unsafe { GetLastError() } != ERROR_ALREADY_EXISTS;
        let event =
            unsafe { CreateEventW(std::ptr::null(), 0, 0, wide(&format!("{id}-show")).as_ptr()) };
        if first {
            SHOW.store(event, std::sync::atomic::Ordering::Relaxed);
        } else if event != 0 {
            unsafe {
                AllowSetForegroundWindow(ASFW_ANY); // let the running copy come to the front
                SetEvent(event);
            }
        }
        first
    }

    pub fn show_requested() -> bool {
        let event = SHOW.load(std::sync::atomic::Ordering::Relaxed);
        event != 0 && unsafe { WaitForSingleObject(event, 0) } == 0
    }

    #[cfg(test)]
    #[test]
    fn window_placement_has_windows_layout() {
        assert_eq!(size_of::<WindowPlacement>(), 44);
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
    pub fn placement(_: isize) -> Option<String> {
        None
    }
    pub fn set_placement(_: isize, _: [i32; 4], _: bool) {}
    pub fn first_instance(_: &str) -> bool {
        true
    }
    pub fn show_requested() -> bool {
        false
    }
    pub fn tray_balloon(_: &str, _: u32, _: &str, _: &str) -> bool {
        false
    }
}
