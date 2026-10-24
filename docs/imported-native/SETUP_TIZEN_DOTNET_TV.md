# Tizen .NET TV Setup Notes

The current SDK check showed only mobile native C rootstraps, so the C native TV path cannot produce a useful TV `.tpk` from this install. The .NET TV route is separate: it needs the Tizen .NET SDK/templates plus Samsung TV .NET reference assemblies.

## Preflight

Run these on Tanya's Mac:

```sh
dotnet --info
dotnet new list | grep -i tizen
tizen version
tizen security-profiles list
sdb devices
```

If `dotnet` is missing, install the .NET SDK required by the Samsung/Tizen tooling first. Samsung's current Tizen .NET docs still describe `net6.0-tizen` for Tizen .NET MAUI projects, while the lower-level NUI route can also be generated through Tizen .NET templates.

## Recommended Project Creation

Use the official wizard/extension first, then copy this source in:

1. Open Visual Studio Code or Visual Studio with the Tizen extension installed.
2. Create a Tizen .NET TV or Tizen NUI project.
3. Confirm the generated project can build a hello-world `.tpk`.
4. Copy this folder's `src/StreamedTv` files into the generated project.
5. Keep the generated signing/project metadata if it differs from this scaffold.

## CLI Build Shape

From an official generated Tizen .NET project:

```sh
tizen build-cs
find . -name '*.tpk'
```

For installation:

```sh
sdb devices
tizen install -n StreamedTv.tpk -t TV_DEVICE_NAME -- path/to/tpk/folder
```

Use the device name shown by `sdb devices`, not the IP string, when the Tizen CLI target resolver accepts the friendly name.

## What To Watch For

- If `tizen build-cs` is unavailable or fails instantly, the .NET SDK/plugin piece is still missing.
- If the generated project targets `net6.0-tizen`, use the generated `.csproj` rather than forcing the scaffold's `tizen60` project file.
- If `Tizen.TV.Multimedia` is not resolved, the Samsung TV .NET extension/reference package is missing.
- Direct HLS/DASH playback should be implemented through the TV multimedia player, not an embedded web iframe.
