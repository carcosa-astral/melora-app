# Changelog

All notable changes to Melora are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Melora uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed
- Mixes are grouped under a heading per DJ, taken from the folder each mix sits in, sorted A–Z.
  Mixes directly in the mixes folder are grouped under that folder's name. Each DJ heading shows the
  DJ's picture once (their folder image or embedded art); mix rows no longer repeat it.
- Closing the window quits Melora again; only minimizing sends it to the system tray.
- Confirmations and messages use Melora's own dark dialog instead of the Windows message box.
  Deleting a station shows a red Delete button, with Cancel as the default.

### Fixed
- Removing a folder in Settings could silently do nothing when the folder was stored as a full path
  on the same drive as Melora. Folders are now matched however they're stored.
- The volume and seek bars stopped short of the end at 100%; they now fill all the way.
- AAC radio stations took up to 50 seconds to start, because AAC went through Windows Media Foundation.
  Melora now ships the BASS_AAC add-on and AAC stations start in about a second.
- Radio "now playing" titles could be out of step with the music. Through Media Foundation, AAC streams
  read about a minute ahead, so titles changed early; the AAC add-on removes that lag. Melora also
  re-checks the title every few seconds, for decoders that don't announce title changes.

## [0.3.0] - 2026-10-21

Milestone 3: desktop integration.

### Added
- Tray icon. Left-click shows or hides Melora; right-click opens a menu with what's playing,
  play/pause, next, previous, Show Melora and Quit. The tooltip shows the current track or station.
- Closing or minimizing the window sends Melora to the tray. Both can be turned off in Settings.
  The first time it happens, a notification explains how to get Melora back.
- Windows media overlay: the volume flyout and lock screen show the title, artist, album, cover art
  and progress, with working play/pause, next, previous and seek. Radio shows the station and live title.
- Keyboard media keys (play/pause, next, previous, stop), through the same overlay.
- Launching Melora while it's already running brings the running copy back. Each portable folder is its own instance.
- Ctrl+Q quits.
- App icon for the exe, windows and tray.

### Changed
- Melora now targets Windows 10 version 1809 or later, for the media overlay.

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
