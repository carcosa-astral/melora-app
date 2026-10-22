# Changelog

All notable changes to Melora are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and Melora uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.6.0] - 2026-10-22

Milestone 6: Discord, gapless and love.

### Added
- Gapless playback in Music: the next track starts on the very next sample, so live albums and
  continuous mixes split into tracks play straight through. Tracks in a different sample rate or
  channel layout from the one before still start right after it, with the usual short gap. Mixes
  and radio are unchanged.
- Love button (heart, or Ctrl+L) for the playing track, sent to Last.fm and ListenBrainz. Loves made
  offline are kept and sent later, and a track loved on the Last.fm website shows as loved in Melora.
  ListenBrainz needs to recognise the track: files tagged with Picard always work, others are matched
  by artist and title.
- Discord status: "Listening to Melora" on your profile with the title, artist, album, a progress bar
  and the album cover. It needs a free application from Discord's developer portal (Settings explains
  how), and mixes and radio can each be left out. Nothing shows while paused. Covers are looked up
  online (Cover Art Archive for files tagged with Picard, then iTunes, then Deezer) because Discord can
  only show pictures from the web; nothing is looked up while this is off.
- `tools/make-icon.ps1` also writes a 512 px PNG of the icon, for the Discord application.

### Fixed
- Playback now follows Windows when the default output device changes (headphones plugged in or
  unplugged, or another device picked) instead of staying on the old one.

### Changed
- The portable build compresses its single-file exe: the folder is about 85 MB instead of 193 MB,
  with no noticeable change in startup time.

## [0.5.0] - 2026-10-21

Milestone 5: polish.

### Added
- The accent colour (playing row, seek and volume bars, tab underline, toggles) fades to a colour picked
  from the playing album's art, tuned to stay readable on the dark theme. Grey art, radio and nothing
  playing use the usual periwinkle. Can be turned off in Settings.
- Music and Mixes have a header row with draggable dividers to resize the Artist and Length columns.
- Melora remembers the window's size, position and maximized state, and the column widths. A saved
  position that's no longer on any screen (e.g. an unplugged monitor) is ignored.

## [0.4.0] - 2026-10-21

Milestone 4: scrobbling.

### Added
- Scrobbling to Last.fm and ListenBrainz, each connected from Settings. Last.fm uses your own API key and
  a one-time approval in the browser; ListenBrainz uses your user token.
- "Now playing" is sent when a track starts. A play is scrobbled once you've actually listened to half
  of it or 4 minutes, whichever comes first (paused time and seeking don't count). Tracks of 30 seconds
  or less and radio are never scrobbled; mixes can be switched off.
- Plays are queued in Melora's data folder and sent in batches, oldest first. Plays made offline are sent
  when the connection comes back (checked every 5 minutes and at startup), and each service has its own
  queue so one being down doesn't hold up the other. Settings shows how many are waiting.
- If a service stops accepting the saved login, Settings says so and keeps the plays until you reconnect.

### Changed
- Settings scrolls when it's taller than the screen.
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
