# Melora 3.0 (Rust, in progress)

The same Melora, rebuilt in Rust. Melora 2.x in `../src` stays the shipped version until 3.0 does
everything 2.x does. The plan and phases are in the main [README](../README.md#roadmap).

## Where it is

Phase 1 is done: gapless playback with the 2.x engine ported to Rust (one output stream fed from
each track's decoder, BASSmix for other sample rates and mono), 150 ms fades, and a seek bar of our
own (click to jump, drag, arrows 5 s, Page Up/Down 30 s).

Phase 2 is done:
- Done: the library. Folders are scanned in the background into `data\melora.db` (2.x's tables,
  2.x's path rules and timestamps), and the Music screen lists albums with their discs and tracks.
  Add a folder with the button next to "Music". Arrow keys, Page Up/Down, Home/End, Enter and
  double-click; Space plays and pauses, Ctrl+Left/Right skip.
- Done: album covers, laid out as in 2.x (168 px on the left of each album). Found in the album's
  folder (cover, folder, front or album .jpg/.jpeg/.png, also above CD1\ folders) or embedded in its
  files, then shrunk to the size shown on a worker thread. Only covers near the view are kept.
- Done: the Mixes screen. Music and Mixes tabs, each keeping its own place. Mixes are grouped by DJ
  (the folder under the mixes folder), oldest first, with the DJ's picture (from their first mix,
  as in 2.x) and "12 mixes · 9h 30m · 2014–2025". A mixes folder inside the music folder only
  shows in Mixes. The first scan of 1,370 mixes took about 3 seconds.
- Done: shuffle (2.x's spread shuffle: no artist or album twice running when it can be helped),
  repeat off/all/one, and volume, saved under 2.x's setting names (volume when Melora closes).
- Done: folder watching. Adding, removing, renaming or retagging music rescans by itself once
  changes settle for 2 seconds, as in 2.x.

Phase 3 is done:
- A Radio tab with your stations. Add one with the + button: paste the address (.pls, .m3u and HLS
  .m3u8 work too), Check reads the name the station gives itself, then Save. F2 edits, Del deletes
  (after asking), and right-click has Play, Edit and Delete.
- Stations connect in the background and show what they're playing (ICY titles, or Ogg comments).
  Play stops a station with a fade and tunes in again.
- The ⋯ button exports your stations to an .m3u file and imports one, skipping stations you
  already have. Same files as 2.x.
- Station logos from Radio Browser, by the station's address or else its name, cached under 2.x's
  keys (found ones kept, misses tried again after a week). They follow 2.x's "online" setting.

Phase 4, in progress:
- Done: Melora's icon on the exe (Explorer, a pinned taskbar button), the window and the tray.
- Done: the tray icon, as in 2.x. Left-click shows or hides Melora; right-click has what's playing,
  Play/Pause (Stop for radio), Next, Previous, Show Melora and Quit; the tooltip says what's
  playing. Minimizing hides to the tray (2.x's `minimizeToTray` setting, on unless turned off).
  Ctrl+Q quits. The first time it hides there, a balloon says so (once ever, as in 2.x).
- Done: Settings (the gear at the right of the top bar), as in 2.x: Music and Mixes folders with
  Add and Remove, backing up and restoring radio stations (.m3u), minimize to tray, and finding
  covers and logos online, plus the version. Done or Escape closes it. Scrobbling, Discord and
  the update check get their sections when they're built.
- Next: media keys and the Windows media overlay, then scrobbling and Discord.

Covers have 6 px rounded corners and DJ pictures are round, as in 2.x. The rounding is baked into
the picture when it's shrunk, because Slint's software renderer doesn't round a cropped image.

The list draws only the rows on screen, at positions worked out in Rust, so it stays light however
big the library is.

Measured (release build): about 9 MB for the exe and BASS DLLs, and about 27 MB of memory with
149 tracks loaded after scrolling through every cover; the window is up in well under a second.

## Building

Needs Rust (stable). From this folder:

```
cargo build --release
```

`target\release\melora.exe` runs as is: the build copies the BASS DLLs from `../lib/bass` next to it.
`cargo test`, `cargo fmt` and `cargo clippy` should pass before each commit.

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
| `src/logos.rs` | Station logos from Radio Browser |
| `src/rows.rs` | The list's rows (headings and tracks), their positions, and keyboard steps |
| `src/window.rs` | The dark title bar, and hiding to and coming back from the tray (Windows) |
| `src/tray.rs` | The tray icon and its menu |
| `ui/app.slint` | The window |
| `ui/track_list.slint` | The library list |
| `ui/dialogs.slint` | The station panel, delete confirmation and menus |
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
