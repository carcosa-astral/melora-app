# Melora 3.0 (Rust, in progress)

The same Melora, rebuilt in Rust. Melora 2.x in `../src` stays the shipped version until 3.0 does
everything 2.x does. The plan and phases are in the main [README](../README.md#roadmap).

## Where it is

Phase 1, in progress. Open a folder (or pass one, or a file in it: `melora.exe "D:\Music\Album"`)
and its tracks, subfolders included, play in order A–Z without gaps, using the 2.x engine ported to
Rust: one output stream fed from each track's decoder, with BASSmix converting a track in another
sample rate or channel layout. Previous, Play/Pause (150 ms fades), Next and a seek bar of our own like 2.x's (click to jump,
drag, arrows 5 s, Page Up/Down 30 s, a focus ring only for keyboard focus). Double-click
or Enter plays a row, Space plays and pauses, Ctrl+Left/Right skip. Tracks that can't be opened are
skipped.

The engine tests from 2.x are ported and run on BASS's silent device (`cargo test`): the hand-over,
seeking near the end, other sample rates and mono, the end of the last track, and the fades.

Measured (release build, software renderer): 8.9 MB for the exe and BASS DLLs, about 21 MB of
memory, window up in well under a second.

Still to do in phase 1:
- Mica: the window shows the solid fallback colour. The software renderer draws an opaque window;
  Slint's OpenGL renderer could be transparent but used about 50 MB more memory here.
- Listening closely to a join between two tracks that run into each other (the hand-over itself
  is checked in the app and by the tests).

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
| `src/folder.rs` | The audio files in a folder, sorted |
| `src/backdrop.rs` | Dark title bar on Windows |
| `ui/app.slint` | The window |
| `build.rs` | Compiles the UI, copies the BASS DLLs |

## Approved dependencies

Approved by Griff on 2026-11-15 for the 3.0 rewrite, when the no-crates rule was relaxed.

| Crate | Why |
|---|---|
| `slint` | The window and widgets. Built with only its software renderer (about 50 MB less memory than OpenGL here, same look) |
| `rfd` | The Windows folder picker |
| `raw-window-handle` | To reach the native window (dark title bar) |
| `slint-build` | Compiles `ui/*.slint` at build time |
| BASS (DLLs, not a crate) | All playback, as in 2.x |

## Design

Melora 2.x's own look: the colours from `../src/Melora/App.xaml`, IBM Plex Sans, Segoe Fluent Icons.
