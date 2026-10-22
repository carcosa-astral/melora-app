# Changelog

All notable changes to Melora are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Melora uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.2.0] - 2026-10-21

Milestone 2: Mixes and Radio.

### Added
- Mixes library: its own folders, a flat list with thumbnails, double-click to play, shuffle and repeat.
- Radio library: paste a stream address (direct, `.pls` or `.m3u`), check it, and name it before saving.
  The check suggests the station's own name, selected so you can type a better one.
- Live "now playing" titles from ICY and Ogg stream metadata, shown in the station list, status bar and window title.
- Edit (F2) and delete (Del) stations from the keyboard or the right-click menu.
- Stream format in the status bar for radio (codec, sample rate, bitrate).
- Empty states for each tab, with a button to add a folder or station.
- Settings window now lists Music and Mixes folders; a folder can't be in both.
- Melora remembers the last open tab.

### Changed
- Space stops a radio station instead of pausing it, and Next/Previous switch stations while listening to radio.
- A mixes folder inside a music folder now only appears in Mixes.

## [0.1.0] - 2026-10-21

Milestone 1: the core player.

### Added
- Playback of FLAC, Opus and MP3 through BASS.
- Transport bar: previous, play/pause, next, seek bar, elapsed/total time and volume.
- Shuffle that plays every track once before repeating and keeps the same artist or album from playing back-to-back.
- Repeat modes: off, all and one.
- Music library from watched folders, rescanned automatically when files change.
- Track list grouped by album, with cover art from `cover`/`folder`/`front` images or embedded tags.
- Settings window for adding and removing music folders.
- Status bar showing the current track, its format (codec, sample rate, bitrate, channels) and library totals.
- Keyboard shortcuts: Space, Enter, Ctrl+Left and Ctrl+Right.
- Dark theme with Mica backdrop on Windows 11, set in IBM Plex Sans and IBM Plex Mono (bundled with the app).
- Portable data: the library database and settings live in a `data` folder beside the exe.
- Portable publish profile that builds a self-contained folder.
- Unit tests for play order, shuffle and repeat.
