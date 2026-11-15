# Melora 3.0 (Rust, in progress)

The same Melora, rebuilt in Rust. Melora 2.x in `../src` stays the shipped version until 3.0 does
everything 2.x does. The plan and phases are in the main [README](../README.md#roadmap).

## Where it is

Phase 1 proof: a window that plays one file through BASS, with play/pause fades (150 ms, as in
2.x), a seek bar, and Melora's colours and IBM Plex Sans. Open a file with the button, or pass its
path: `melora.exe "D:\Music\song.flac"`. Space plays and pauses.

Measured on the proof (release build, software renderer): 8.9 MB for the exe and BASS DLLs, about
21 MB of memory, window up in well under a second.

Not working yet: Mica. The window shows the solid fallback colour instead; the software renderer
draws an opaque window, so the backdrop needs another approach (phase 1).

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
| `src/bass.rs` | Our own small bindings to bass.dll (only the calls Melora uses) |
| `src/backdrop.rs` | Dark title bar and Mica on Windows 11 |
| `ui/app.slint` | The window |
| `build.rs` | Compiles the UI, copies the BASS DLLs |

## Approved dependencies

Approved by Griff on 2026-11-15 for the 3.0 rewrite, when the no-crates rule was relaxed.

| Crate | Why |
|---|---|
| `slint` | The window and widgets. Built with only its software renderer (about 50 MB less memory than OpenGL here, same look) |
| `rfd` | The Windows file picker |
| `raw-window-handle` | To reach the native window for Mica |
| `slint-build` | Compiles `ui/*.slint` at build time |
| BASS (DLLs, not a crate) | All playback, as in 2.x |

## Design

Melora 2.x's own look: the colours from `../src/Melora/App.xaml`, IBM Plex Sans, Segoe Fluent Icons.
