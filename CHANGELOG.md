# Changelog

All notable changes to Melora are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Melora uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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
