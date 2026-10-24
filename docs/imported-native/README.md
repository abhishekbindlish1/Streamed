# Streamed .NET TV Candidate

This folder is the Tizen .NET TV exploration path for Streamed. It is kept beside the existing projects so we can compare them without disturbing the working web app.

- Web app: `/Users/abindlish/codex/streamedapp`
- C native experiment: `/Users/abindlish/codex/streamed-native`
- .NET TV candidate: `/Users/abindlish/codex/streamed-dotnet-tv`

## Why This Path

Samsung TV public distribution is still normally centered on web `.wgt` apps, but Tizen .NET TV is the realistic route to a TV-native `.tpk` style app without relying on the C native TV SDK/rootstrap that is missing from the current install.

This machine does not currently have `dotnet`, so this project is a scaffold and migration target, not a locally verified build artifact yet.

## What Is Included

- A Tizen .NET/XAML app shell with a TV-style catalog view.
- Internal Streamed backend endpoint constants.
- Catalog service with one-hour in-memory caching and bundled demo fallback.
- Stream ranking that prefers matches with usable stream sources.
- Stream picker flow separated from playback.
- Tizen TV player service placeholder for direct URL playback using `Tizen.TV.Multimedia.Player` once the official TV template/reference assemblies are installed.
- Reused fallback catalog: `src/StreamedTv/Resources/Raw/live-demo.json`
- Reused app icon: `src/StreamedTv/Resources/AppIcon/icon.png`

## Recommended Build Flow

1. Install the Tizen .NET TV tooling on the Mac that has Tizen Studio.
2. Create a fresh official Tizen .NET TV app from the Samsung/Tizen project wizard.
3. Copy the `src/StreamedTv` source files from this folder into that generated project.
4. Keep the generated `.csproj`, `tizen-manifest.xml`, and signing settings if they differ from this scaffold.
5. Build/package from the generated project to produce the `.tpk`.

See `SETUP_TIZEN_DOTNET_TV.md` for the command checks and packaging notes.
