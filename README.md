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
| Shuffle | Every track plays once before any repeats, and the same artist or album never plays back-to-back |
| Repeat | Off, all, one |
| Library | Watched folders, rescanned automatically when files change |
| Mixes | Their own folders and a flat list; double-click to play, shuffle works here too. A mixes folder inside a music folder only shows up in Mixes |
| Radio | Paste an address (direct stream, `.pls` or `.m3u`), check it, name it, save. Shows what the station is playing live. MP3, AAC, Ogg Vorbis and Opus streams |
| Look | Dark theme, Mica backdrop on Windows 11, IBM Plex Sans and Plex Mono (bundled, so no font install needed) |
| Portable | Everything lives next to `Melora.exe`; nothing is written to AppData or the registry |

### Keyboard

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `Enter` | Play the selected track |
| `Ctrl` + `→` | Next track |
| `Ctrl` + `←` | Previous track (restarts the track if more than 3 s in) |
| `F2` | Edit the selected station |
| `Del` | Delete the selected station |

On the Radio tab, Space stops and restarts the station rather than pausing it (a paused live stream just falls behind), and Next/Previous move between stations.

## Portable data

Melora writes only to a `data` folder beside the exe:

```
Melora\
├── Melora.exe
├── bass.dll, bassflac.dll, bassopus.dll
└── data\
    └── melora.db      libraries, folders, stations and settings (SQLite)
```

Folders on the same drive as Melora are stored as relative paths, so a copy on a USB stick keeps working when its drive letter changes.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows x64.

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

This produces a self-contained folder in `dist\Melora\`, with the .NET runtime included. Copy that folder anywhere; no installer is needed.

## Project layout

```
src/Melora/
├── Audio/        BASS playback wrapper
├── Library/      SQLite database, folder scanning and watching, track and album models
├── Radio/        Stations, stream checks, ICY / Ogg metadata parsing
├── Playback/     Play order: in-order, shuffle with artist and album spread, repeat
├── Interop/      Mica and dark title bar
├── Assets/Fonts/ IBM Plex (SIL Open Font License)
└── *.xaml        Main window and settings
tests/Melora.Tests/  Unit tests (xUnit)
lib/bass/         Native BASS libraries (x64)
```

## Roadmap

1. ~~**Core player**: playback, transport, seek, volume, Music library grouped by album with art~~
2. ~~**Mixes + Radio**: the Mixes library, and radio stations with a naming step and live stream titles~~
3. **Desktop integration**: tray icon, minimize/close to tray, media keys, Windows media overlay
4. **Scrobbling**: Last.fm and ListenBrainz, with an offline queue (Music and Mixes only)
5. **Polish**: accent color from album art, resizable columns, remembered window and column state

See [CHANGELOG.md](CHANGELOG.md) for what has shipped.

## Third-party components

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). BASS is free for non-commercial use only, so Melora is a personal, non-commercial project.
