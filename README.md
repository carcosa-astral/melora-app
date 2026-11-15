# Melora

A minimal, portable music player for Windows, in the spirit of foobar2000 with Columns UI, but quieter and prettier.

Melora keeps three separate libraries:

- **Music**: your albums, grouped with cover art.
- **Mixes**: long DJ sets and mixes, kept apart from albums.
- **Radio**: internet streams you paste in and give a proper name before tuning in.

No playlists, no layout editing, no equalizer. Double-click a track and Melora plays that library from there, in order or shuffled.

## Features

| | |
|---|---|
| Formats | FLAC, Opus, MP3 |
| Gapless | Music plays straight from one track into the next, with no gap, even when the next track has another sample rate or channel layout (it's converted as it plays, with BASSmix) |
| Fades | Pausing and stopping fade out over 150 ms, and resuming fades back in, so nothing clicks |
| Output | Follows Windows' default output device, e.g. when headphones are plugged in |
| Shuffle | Every track plays once before any repeats, and the same artist or album never plays back-to-back |
| Repeat | Off, all, one |
| Library | Watched folders, rescanned automatically when files change. Albums with several discs get a heading per disc (from the disc tag, or `CD1`/`Disc 2` folders), and compilations without an album artist stay together as "Various Artists" |
| Missing art | Albums and DJ folders without a picture of their own get one from the Cover Art Archive, iTunes or Deezer (DJ photos from Deezer), and stations get their logo from Radio Browser. Kept in `data\covers`; your music files and folders are never changed. Can be turned off in Settings |
| Getting around | Type the first letters of an artist, DJ or station to jump to it (not a search: nothing is hidden); the same letter again steps to the next one. Click what's playing in the status bar, or press Ctrl+J, to jump back to it |
| Mixes | Their own folders, grouped A–Z by DJ: the folder each mix sits in (`Mixes\DJ Name\...`), then by date, oldest first (a date in the title like "(2020-10-14)", or the year tag). Each DJ's heading shows how many mixes, their total length and the years they span. Double-click to play; shuffle works here too. A mixes folder inside a music folder only shows up in Mixes |
| Radio | Paste an address (direct stream, `.pls`, `.m3u` or HLS `.m3u8`), check it, name it, save. Shows each station's logo, and what it's playing live with the song's cover (looked up on iTunes and Deezer; can be turned off). The ⋯ button next to Add station exports your stations to an `.m3u` file and imports them again (skipping ones you already have). MP3, AAC, Ogg Vorbis and Opus streams |
| Tray | Minimizing sends Melora to the system tray (can be turned off in Settings); closing the window quits. Left-click the icon to show or hide Melora; right-click for play/pause, next, previous and Quit. The tooltip shows what's playing |
| Media keys | Keyboard media keys and the Windows media overlay (volume flyout, lock screen) show the track, cover art and progress, and control playback |
| Scrobbling | Last.fm and ListenBrainz, set up in Settings. Counts a play after half the track or 4 minutes of actual listening; radio and tracks under 30 s are skipped, mixes optional. Plays made offline are queued and sent later |
| Love | The heart in the transport bar (or Ctrl+L) loves the playing track on Last.fm and ListenBrainz. Loves made offline are sent later; tracks loved on the Last.fm website show as loved |
| Discord | "Listening to Melora" on your Discord profile, with the track, a progress bar and the album cover. Mixes and radio can be left out; nothing shows while paused |
| Updates | Settings shows the version and checks GitHub once a day for a newer one, with a link to it. Nothing is downloaded or installed; can be turned off |
| Log | `data\melora.log` keeps what went wrong without showing (skipped files, refused scrobbles, dropped stations); "Open log" in Settings opens it |
| Accessibility | Every control can be reached with Tab and shows an accent ring when it has keyboard focus; buttons, sliders and list rows have names for screen readers |
| One instance | Launching Melora again brings the running copy back instead of starting a second one |
| Look | Dark theme, Mica backdrop on Windows 11, IBM Plex Sans and Plex Mono (bundled, so no font install needed). The accent colour fades to a colour from the playing album's art (can be turned off in Settings) |
| Columns | Drag the dividers in the list headers to resize Artist and Length; Title takes the rest |
| Remembers | Window size, position and maximized state, column widths, the open tab, volume, shuffle and repeat |
| Portable | Everything lives next to `Melora.exe`; nothing is written to AppData or the registry |

### Keyboard

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `Enter` | Play the selected track (or press the button that has keyboard focus) |
| `Tab` | Move between the controls |
| `←` `→` on the seek bar | Back or forward 5 s (`Page Up` / `Page Down`: 30 s) |
| `Ctrl` + `→` | Next track |
| `Ctrl` + `←` | Previous track (restarts the track if more than 3 s in) |
| `Ctrl` + `L` | Love or un-love the playing track |
| `Ctrl` + `J` | Show the playing track or station in its list |
| Letters | Jump to the artist (Music), DJ (Mixes) or station starting with them |
| `Ctrl` + `Q` | Quit |
| `F2` | Edit the selected station |
| `Del` | Delete the selected station |

On the Radio tab, Space stops and restarts the station rather than pausing it (a paused live stream just falls behind), and Next/Previous move between stations.

## Scrobbling setup

Open Settings (⚙) and scroll to **Scrobbling**.

- **Last.fm**: create a free API account at [last.fm/api/account/create](https://www.last.fm/api/account/create)
  (any name and description; leave the callback URL empty). Paste its API key and shared secret into Melora,
  click **Connect…**, and approve Melora in the browser page that opens. Melora notices the approval on its own.
- **ListenBrainz**: copy your user token from [listenbrainz.org/settings](https://listenbrainz.org/settings/),
  paste it into Melora and click **Connect**.

## Discord setup

Discord shows the name of an "application" after "Listening to", so Melora needs one of its own:

1. Open [discord.com/developers/applications](https://discord.com/developers/applications) and sign in.
   If it asks what brings you there, click **Skip**.
2. Click **New Application**, name it **Melora**, and accept the terms.
3. Optionally, set its **App Icon** to `tools/Melora-1024.png`. Discord shows it when there's no album cover.
4. Copy the **Application ID** from the General Information page.
5. In Melora's Settings, turn on **Show what I'm playing on my Discord profile** and paste the ID.

Discord must be running on the same PC. Discord can only show pictures from the web, so Melora looks up
each album's cover online: on the Cover Art Archive for files tagged with Picard, then iTunes, then Deezer,
sending just the artist and album name. For mixes it shows the DJ's photo from Deezer, found by the DJ's folder name. Answers are kept in Melora's data folder, so each album is looked
up once. Nothing is looked up while the Discord status is off.

## Portable data

Melora writes only to a `data` folder beside the exe:

```
Melora\
├── Melora.exe
├── bass.dll, bassflac.dll, bassopus.dll, bass_aac.dll, basshls.dll
└── data\
    ├── melora.db      libraries, folders, stations, settings, scrobble and love queues,
    │                  cover addresses and scrobbling keys/tokens (SQLite, plain text,
    │                  so keep the folder private)
    ├── melora.log     problems worth knowing about: skipped files, refused scrobbles,
    │                  stations that won't connect
    └── melora-<date>_<time>.log   earlier logs (1 MB each); the newest five are kept
```

Folders on the same drive as Melora are stored as relative paths, so a copy on a USB stick keeps working when its drive letter changes.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10 (1809) or later, x64.

```bash
dotnet build src/Melora
```

```bash
dotnet run --project src/Melora
```

```bash
dotnet test
```

### Portable build

```bash
dotnet publish src/Melora -p:PublishProfile=Portable
```

This produces a self-contained folder in `dist\Melora\` (about 85 MB, with the .NET runtime included and compressed into `Melora.exe`). Copy that folder anywhere; no installer is needed.

To trade disk space for memory and startup time, add `-p:EnableCompressionInSingleFile=false -p:PublishReadyToRun=true`.
The folder grows to about 250 MB, but Melora uses about 55 MB of memory instead of 140 MB and shows its
window in about 0.65 s instead of 1 s.

## Releasing

```bash
pwsh tools/release.ps1 -DryRun
```

```bash
pwsh tools/release.ps1
```

The script releases the version in `Melora.csproj`, using its `CHANGELOG.md` entry as the notes. It checks
that everything is committed and pushed and that the version is new, runs the tests, builds the portable
folder, zips it without any `data` folder, tags the commit and publishes the GitHub release with the zip
attached. `-DryRun` does everything except tagging and publishing, and shows the notes. It needs the
[GitHub CLI](https://cli.github.com/), signed in.

## Project layout

```
src/Melora/
├── Audio/        BASS playback wrapper, gapless output
├── Library/      SQLite database, folder scanning and watching, track and album models
├── Radio/        Stations, stream checks, ICY / Ogg metadata parsing
├── Scrobbling/   Last.fm and ListenBrainz clients, scrobble rules, offline queue, loves
├── Discord/      Discord status over its local pipe, cover lookup for it
├── Playback/     Play order: in-order, shuffle with artist and album spread, repeat
├── Interop/      Mica and dark title bar, tray icon, Windows media overlay
├── Theme/        Accent colour picked from album art
├── Assets/Fonts/ IBM Plex (SIL Open Font License)
└── *.xaml        Main window and settings
tests/Melora.Tests/  Unit tests (xUnit)
tools/make-icon.ps1  Renders Assets/Melora.ico and tools/Melora-1024.png
tools/release.ps1    Tests, builds, zips and publishes a GitHub release
.github/workflows/   Builds and tests every push
lib/bass/         Native BASS libraries (x64)
```

## Roadmap

1. ~~**Core player**: playback, transport, seek, volume, Music library grouped by album with art~~
2. ~~**Mixes + Radio**: the Mixes library, and radio stations with a naming step and live stream titles~~
3. ~~**Desktop integration**: tray icon, minimize/close to tray, media keys, Windows media overlay~~
4. ~~**Scrobbling**: Last.fm and ListenBrainz, with an offline queue (Music and Mixes only)~~
5. ~~**Polish**: accent color from album art, resizable columns, remembered window and column state~~
6. ~~**Discord, gapless and love**: Discord status with album covers, gapless Music, love button, follow the output device~~
7. ~~**1.0**: radio covers, lighter pictures, diagnostics log~~
8. ~~**1.2**: HLS radio streams (like the BBC's), disc headings and compilation albums, plus the planned 1.1
   (build and test on GitHub, one-command releases, a "new version" notice in Settings), released together~~
9. ~~**1.3, pictures and getting around**: missing album covers and DJ pictures found online, jump to now playing, type to jump~~
10. ~~**2.0**: fades on pause and stop, gapless across sample rates, station logos, backing up stations, DJ heading details, "Open log", a keyboard and screen-reader pass~~
11. **3.0, Rust**: the same Melora rebuilt in Rust (Slint for the window, BASS still for sound), being built in [`rust/`](rust/README.md) while 2.x keeps shipping from `src/`. Nothing new and nothing dropped. Phases, each ending in something that runs:
    1. Plays music: window, BASS, a folder with gapless and fades (a first proof plays one file)
    2. Library: scanning, tags, albums, DJs, covers, the list screens
    3. Radio: stations, logos, live song info, import and export
    4. Connected: scrobbling, loves, Discord, tray, media keys and overlay
    5. Polish: online covers, type to jump, update check, settings, keyboard and screen readers
    6. Ship 3.0.0: data carried over from 2.x; `src/` and the C# tests are removed

See [CHANGELOG.md](CHANGELOG.md) for what has shipped.

### Ideas for later

Not scheduled; picked from as releases come up.

- **More file types**: M4A/ALAC, WAV, Ogg Vorbis, if they're ever needed
- **Signing the exe**, so SmartScreen doesn't warn on downloads (on hold: costs money, and only matters if Melora is shared)

### Not planned

Playlists, a queue, search, an equalizer, ReplayGain, lyrics, discovery, visualizers, layout editing,
a mini player, a sleep timer, cue sheets, resuming where you left off, taskbar controls, an updater
that downloads or installs anything, exclusive (WASAPI) output, opening files in Explorer, Mp3tag or
Picard, a library check, re-reading tags on demand, dropping folders onto the window, per-disc covers,
a history of what a station played, and starting minimized or with Windows.

## Third-party components

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). BASS is free for non-commercial use only, so Melora is a personal, non-commercial project.
