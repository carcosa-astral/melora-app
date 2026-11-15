# Melora 3.0 (Rust, in progress)

The same Melora, rebuilt in Rust. Melora 2.x in `../src` stays the shipped version until 3.0 does
everything 2.x does. The plan and phases are in the main [README](../README.md#roadmap).

## Where it is

Phase 1 is done: gapless playback with the 2.x engine ported to Rust (one output stream fed from
each track's decoder, BASSmix for other sample rates and mono), 150 ms fades, and a seek bar of our
own (click to jump, drag, arrows 5 s, Page Up/Down 30 s).

Phase 2, in progress:
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
- Next: folder watching, shuffle and repeat.

Known difference from 2.x: covers and DJ pictures have square corners. Slint's software renderer
doesn't round the corners of a clipped picture.

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
| `src/rows.rs` | The list's rows (headings and tracks), their positions, and keyboard steps |
| `src/backdrop.rs` | Dark title bar on Windows |
| `ui/app.slint` | The window |
| `ui/track_list.slint` | The library list |
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
