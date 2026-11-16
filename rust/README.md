# Melora (Rust)

Melora's code. 3.0 rebuilt the C# and WPF 2.x in Rust, with Slint for the window and BASS still
for sound; what Melora does is in the main [README](../README.md).

## How it works

The list draws only the rows on screen, at positions worked out in Rust, so it stays light however
big the library is. Covers have 6 px rounded corners and DJ pictures are round, as in 2.x; the
rounding is baked into the picture when it's shrunk, because Slint's software renderer doesn't
round a cropped image.

The database, setting names, cover files and log are exactly 2.x's, so a 2.x `data` folder works
as is (and 2.x can still open it).

Measured (release build): about 16 MB for the exe and BASS DLLs, and about 27 MB of memory with
149 tracks loaded after scrolling through every cover; the window is up in well under a second.

## Building

Needs Rust (stable). From this folder:

```
cargo build --release
```

`target\release\Melora.exe` runs as is: the build copies the BASS DLLs from `../lib/bass` next to it,
and `.cargo/config.toml` builds the C runtime into the exe, so it needs nothing installed. Its own
`data` folder there is for trying things out and never ships.
`cargo test`, `cargo fmt` and `cargo clippy` should pass before each commit (GitHub runs the tests and
clippy on every push).

## Layout

| File | Job |
|---|---|
| `src/main.rs` | Starts things and wires the window to playback |
| `src/bass.rs` | Our own small bindings to BASS and BASSmix (only the calls Melora uses) |
| `src/engine.rs` | Gapless playback and fades, ported from 2.x's AudioEngine, with its tests |
| `src/folder.rs` | Files with given extensions in a folder and its subfolders |
| `src/paths.rs` | Where data lives, and paths stored like 2.x (no drive letter on Melora's own drive) |
| `src/db.rs` | `data\melora.db`, in exactly 2.x's tables |
| `src/library.rs` | Scanning folders and grouping tracks into albums and DJs, with 2.x's rules |
| `src/covers.rs` | Finding album covers and shrinking them on a worker thread |
| `src/player.rs` | The two libraries, the tab on screen, and what's playing |
| `src/queue.rs` | The play order: in order or shuffled, and repeat |
| `src/watch.rs` | Watching library folders for changes |
| `src/radio.rs` | Stream titles and headers, pasted addresses, and .m3u station files |
| `src/online.rs` | Finding pictures online: covers, DJ photos and station logos, with 2.x's cache |
| `src/logos.rs` | Fetching station logos and drawing them round |
| `src/discord.rs` | "Listening to" on Discord |
| `src/rows.rs` | The list's rows (headings and tracks), their positions, and keyboard steps |
| `src/window.rs` | The dark title bar, hiding to the tray, the window's place, and one copy at a time (Windows) |
| `src/tray.rs` | The tray icon and its menu |
| `src/log.rs` | The log in the data folder |
| `src/update.rs` | The update check |
| `src/scrobble.rs` | When a play counts, and the requests Last.fm and ListenBrainz take |
| `src/scrobbler.rs` | Sending plays and loves, and connecting accounts, on a worker thread |
| `src/media.rs` | The Windows media overlay and media keys |
| `ui/app.slint` | The window |
| `ui/track_list.slint` | The library list |
| `ui/dialogs.slint` | Settings, the station panel, delete confirmation and menus |
| `ui/controls.slint` | Icon buttons and the seek bar |
| `ui/theme.slint` | The Nord colours |
| `build.rs` | Compiles the UI, copies the BASS DLLs |

## Approved dependencies

Approved by Griff on 2026-11-15 for the 3.0 rewrite, when the no-crates rule was relaxed.

| Crate | Why |
|---|---|
| `slint` | The window and widgets. Built with only its software renderer (about 50 MB less memory than OpenGL here, same look) |
| `rfd` | The Windows folder picker |
| `raw-window-handle` | To reach the native window (dark title bar) |
| `slint-build` | Compiles `ui/*.slint` at build time |
| `lofty` | Reads tags and embedded covers (2.x used TagLibSharp). Approved 2026-11-15 for phase 2 |
| `image` (JPEG and PNG only) | Decodes and shrinks covers. Slint already uses the same version. Approved 2026-11-15 for phase 2 |
| `notify` | Watches library folders for changes. Approved 2026-11-15 for phase 2 |
| `ureq`, `serde_json` | Asking Radio Browser for station logos, and reading its answers. Approved 2026-11-15 for phase 3 |
| `tray-icon` | The notification-area icon and its menu. Approved 2026-11-15 for phase 4 |
| `winresource` (build only) | Puts Melora's icon into the exe. Approved 2026-11-15 for phase 4 |
| `souvlaki` | The Windows media overlay and media keys (and their macOS and Linux versions). Approved 2026-11-15 for phase 4 |
| `md5` | Last.fm signs every request with an MD5. Approved 2026-11-15 for phase 4 |
| `chrono` | Local times in the log (already in the tree through other crates) |
| `rusqlite` (bundled SQLite) | The library database, in 2.x's layout. Approved 2026-11-15 for phase 2 |
| BASS (DLLs, not a crate) | All playback, as in 2.x |

## Design

Nord, deepened for contrast, with IBM Plex Sans and Segoe Fluent Icons. Solid colours only: no Mica,
no transparency, and no accent taken from album covers (2.x's "Tint Melora" is not coming to 3.0).
Drawn by Slint's software renderer, which here started faster and used about 50 MB less memory
than its OpenGL one.

| Role | Colour | Contrast on the background |
|---|---|---|
| Background | `#1F232A` | |
| Panel, hover | `#2E3440` (nord0) | |
| Divider | `#3B4252` (nord1) | |
| Slider track | `#434C5E` (nord2) | |
| Text | `#ECEFF4` (nord6) | 13.7 : 1 |
| Dim text | `#C0C8D6` | 9.4 : 1 |
| Accent | `#88C0D0` (nord8) | 7.9 : 1 |
| Error | `#D57780` (nord11, lightened) | 5.1 : 1 |
| OK | `#A3BE8C` (nord14) | 7.7 : 1 |
