# LumaChart Studio

A rhythm-chart editor for Malody and osu!mania charts, with a Windows desktop
client and Android/iOS tablet clients built from one shared editor source.

Current release: **1.3.0.0** on all three clients.

## Licence

LumaChart Studio's own client code is MIT licensed; see `LICENSE`. That file is
the licence text and nothing else, so the caveats live here instead — two of
them, and both matter before you build and distribute:

- **The server is not in this repository** and is not covered. The clients talk
  to it over HTTP and reference no server code; nothing here depends on it to
  compile.
- **An MIT grant on the source does not make a binary MIT-redistributable.**
  The clients link LibNMeCab (used here under LGPL-2.1-or-later, elected from
  its GPL/LGPL dual offer) and SoundTouch.Net (LGPL-2.1-or-later). Shipping a
  build carries LGPL obligations, chiefly that recipients can relink against a
  modified version of those components. `THIRD-PARTY-NOTICES.md` sets out every
  component and what each requires.

The bundled IPADIC dictionary (NAIST/ICOT) and Source Han Sans (SIL OFL 1.1)
both require their licence text to travel with the copy, so `licenses/` ships
inside all three clients. Those files are byte-exact as distributed upstream;
do not reformat them.

## Clients

| Client | Host project | State |
|---|---|---|
| Windows | `LumaChart.Avalonia` | Full editor, Inno Setup installer |
| Android tablet | `LumaChart.Android` | Full editor |
| iPad | `LumaChart.iOS` | Full editor |

All three compile the same editor source. Android and iOS bring it in through
`LumaChart.Avalonia.Shared` and `LumaChart.Avalonia.Mobile`, which import
`LumaChart.Avalonia.SharedSource.props`; there is no second editor
implementation and no second copy of the file list.

**The mobile clients are meant to reproduce the Windows client's operation
logic.** A branch that makes mobile behave differently is a defect until a
concrete platform reason is established — see `docs/ios-platform-notes.md` for
the differences that are real.

## Projects

| Project | Responsibility |
|---|---|
| `LumaChart.Core` | Chart model, timing, formats, validation, pure domain rules |
| `LumaChart.Application` | Editing, workspaces, libraries, commands, orchestration |
| `LumaChart.Online` | Account, room, resource and chart-sync transports |
| `LumaChart.Storage.Abstractions` | Platform storage and secure-token contracts |
| `LumaChart.Avalonia` | The editor UI, and the Windows host |
| `LumaChart.Avalonia.Shared` | Shared editor source for Android |
| `LumaChart.Avalonia.Mobile` | Shared editor source for iOS |
| `LumaChart.Android` | Android host, storage, pickers, audio sink |
| `LumaChart.iOS` | iOS host, storage, pickers, audio sink |
| `LumaChart.ClientSecurity.Native` | Native client gate bundled by Android |
| `LumaChart.Server` | ASP.NET Core / PostgreSQL service |
| `LumaChart.Specs` | Domain, application, online and server specifications |
| `LumaChart.Avalonia.Specs` | Editor behaviour and UI-policy specifications |

Dependencies point inward: hosts depend on the editor, the editor depends on
application and domain, and nothing in `Core` or `Application` knows which
platform it is running on. Anything platform-specific reaches the editor through
a seam registered by the host at start-up (`PlatformAudioOutputs`,
`PlatformFileImportServices`, `PlatformNoticeServices` and the rest — the iOS
host registers eleven of them in `LumaChart.iOS/AppDelegate.cs`).

## Building

### Windows

```powershell
dotnet build .\LumaChart.Avalonia\LumaChart.Avalonia.csproj -c Release -warnaserror
```

Installer (output root must not already exist):

```powershell
.\tools\Build-WindowsAvaloniaPreviewInstaller.ps1 `
    -OutputRoot "<new directory>"
```

The installer version now defaults to `<Version>` in `LumaChart.Avalonia.csproj`. Passing a
different number still needs `-AllowUpgradeFixtureVersion`; building the product version does not.

### Android

A distributable Release build must be signed with the release keystore. Set these before building
(the passwords are read from the environment, never written into the project):

```powershell
$env:LUMACHART_ANDROID_KEYSTORE   = "<path to .keystore/.jks>"
$env:LUMACHART_ANDROID_KEY_ALIAS  = "<key alias>"
$env:LUMACHART_ANDROID_STORE_PASS = "<keystore password>"
$env:LUMACHART_ANDROID_KEY_PASS   = "<key password>"
.\tools\Build-AndroidApk.ps1 -Configuration Release
```

Without them a Release build stops; pass `-AllowDebugSigning` only for a build that will not be
distributed. The script prints the signing certificate's SHA-256 after the build. A package signed
with a different key cannot update an installed copy, and uninstalling removes the on-device library.

The .NET Android workload is not installed system-wide; the build uses the
toolchain vendored at `artifacts/android-toolchain`.

### iOS

iOS is built on a macOS VM, because the Apple SDK cannot run on Windows.
`tools\Build-iOSApp.ps1` syncs the source there, builds, and retrieves an
**unsigned** `.ipa` — the VM deliberately holds no Apple account, certificate or
provisioning profile, so signing is a separate step on a machine that does.

```powershell
.\tools\Build-iOSApp.ps1 -Offline
```

Two dependencies are patched for iOS and must be built on that VM first, or the
app will be missing the fixes:

```bash
bash tools/avalonia/build-patched-avalonia-ios.sh   # iOS 26 renders nothing without this
bash tools/nmecab/build-patched-libnmecab.sh        # kanji romanization needs this
```

Both inject through `Exists()` guards, so a machine that has not run them builds
against the stock packages and the build prints which assemblies it used. See
`docs/ios-platform-notes.md`.

### Specifications

```powershell
dotnet run --project .\LumaChart.Specs\LumaChart.Specs.csproj -c Release
dotnet build .\LumaChart.Avalonia.Specs\LumaChart.Avalonia.Specs.csproj -c Release -warnaserror
dotnet run --project .\LumaChart.Avalonia.Specs\LumaChart.Avalonia.Specs.csproj -c Release --no-build
```

Both suites must pass, and Windows and Android release builds must be free of
warnings, before a release.

## Documentation

| Document | Contents |
|---|---|
| `docs/ai-handoff.md` | Start here if you are an AI assistant picking this project up |
| `docs/ios-platform-notes.md` | Why iOS needs what it needs: the rendering fix, the runtime feature switches, the audio sink, the patched dependencies |
| `docs/mobile-phone-layout-brief.md` | In-progress work: adapting the editor layout to phone-sized screens |
| `PROJECT-HANDOFF.md` | Product state, architecture and non-negotiable behaviour |
| `AVALONIA-FUNCTION-MATRIX.md` | Feature coverage across the clients |
| `THIRD-PARTY-NOTICES.md` | Dependencies and their licences, including the two patched ones |

## Repository notes

- Release output goes to `artifacts/Windows`, `artifacts/iOS` and
  `artifacts/Android`. There are no per-version folders: the version lives in the
  file name, and a new release replaces the previous file. None of it is in
  version control.
- `artifacts/android-toolchain` is required to build Android and is not output;
  do not delete it with the packages.
- `.codex-nuget-packages/` is the pinned offline package cache the Windows
  installer build reads from.
- `PRIVATE-OPERATIONS.local.md` holds test accounts and server credentials. It is
  ignored by git and must never be committed, printed or packaged.
