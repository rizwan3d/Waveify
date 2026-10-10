# Waveify

![Waveify product screenshot](ss.png)

Waveify is a Windows desktop music player for building and enjoying an offline music library. It combines local playback, playlist management, metadata cleanup, lyrics lookup, and optional authorized download tooling in a polished WPF interface.

## Product Features

- Local music library with import, rescan, missing-file cleanup, and managed music folder support.
- Rich playback controls with play/pause, next/previous, shuffle, repeat, seeking, volume, and output-device selection.
- Playlists, liked songs, right-click track actions, and queue-oriented "Up Next" views.
- Album artwork loading from embedded tags, cached artwork files, and remote metadata where available.
- Metadata enrichment for imported tracks.
- Lyrics lookup and local `.lrc` lyric file storage.
- Batch URL import for supported authorized sources.
- Optional tool setup window for downloader dependencies.
- Restores volume, output device, repeat mode, last track, and playback position between sessions.


## Library and playback features

- Sort songs using the library dropdown: playlist order, title, artist, or newest added.
- In a playlist, drag a song onto another row to reorder it; the order is saved automatically. Select **Playlist order** to see the saved order.
- Right-click a song to edit its title, artist, album, and genre. These edits update Waveify's library metadata (they do not rewrite the audio file tags).
- **Recently Added** displays the 50 newest imports, and **Recently Played** keeps the last 100 distinct tracks between launches.
- Home offers artist, album, and genre shelves. Double-click a shelf track to play it.
- The Bass slider in the bottom player controls a low-shelf equalizer from -12 to +12 dB.
- Keyboard shortcuts: **Space** play/pause, **Ctrl+F** search, **Ctrl+O** import files, **Ctrl+Right** next, and **Ctrl+Left** previous.

## Additional controls

- Toggle list and grid views above the song table.
- The **Up Next** panel displays upcoming queue tracks; double-click to play, move tracks up, remove individual entries, or clear upcoming tracks. The bottom Queue button toggles the panel.
- Use the playlist options menu to rename, duplicate, delete, or choose a cover image. Playlist song counts appear in the sidebar.
- Filter the library by artist, album, genre, year, liked songs, and available local files. Applied filters are saved between launches.
- Open **Most Played** to see play counts and last-played times.
- The metadata editor offers to write edited fields into supported local audio files.
- The equalizer has bass, midrange, and treble controls with Flat, Rock, Vocal, and Electronic presets.
- Media-key events are handled when Waveify has keyboard focus; Ctrl+M toggles mute. System-wide media controls and configurable shortcuts are not yet implemented.

## Dependencies

Waveify targets Windows and requires:

- Windows 10 or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source.
- .NET 8 Desktop Runtime to run framework-dependent builds.

NuGet packages used by the app:

- [WPF-UI](https://github.com/lepoco/wpfui) for the Fluent WPF controls.
- [NAudio](https://github.com/naudio/NAudio) for audio playback and output-device support.
- [TagLibSharp](https://github.com/mono/taglib-sharp) for audio metadata and embedded artwork.

Optional tools for authorized downloads:

- `yt-dlp`
- `ffmpeg`
- `deno`
- `Sunnify-Windows-CLI`

Waveify can install these optional tools into its local tools folder from their official GitHub releases. The player still works without them for local music playback.

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Run

```powershell
dotnet run -c Release
```

## Publish

Create a Windows publish folder:

```powershell
dotnet publish Waveify.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

The app executable will be created in `publish/win-x64`.

## GitHub Release Workflow

This repository includes a GitHub Actions workflow at `.github/workflows/release.yml`.

To create a release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The workflow publishes a self-contained Windows x64 build, packages it as `Waveify-win-x64.zip`, uploads it as a workflow artifact, and attaches it to the GitHub Release.
