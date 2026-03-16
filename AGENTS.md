# AGENTS.md — PhotoViewer

## Project Overview
Cross-platform photo viewer for photography culling workflows, built with **Avalonia 11 / .NET 9 / ReactiveUI (MVVM)**. Supports Windows, macOS, iOS, Android. Core value: browse JPG/HEIF images and sync XMP star ratings to companion RAW files.

## Architecture

### Solution Structure
| Project | Target | Role |
|---|---|---|
| `PhotoViewer` | net9.0; net9.0-ios | Shared core, ViewModels, Views, Controls |
| `PhotoViewer.Desktop` | net9.0-windows | Windows entry point (LibHeifSharp + WIC) |
| `PhotoViewer.Mac` | net9.0-macos | macOS entry point (native HEIF via ImageIO) |
| `PhotoViewer.Android` | net9.0-android | Android entry point |
| `PhotoViewer.iOS` | net9.0-ios | iOS entry point |

### Platform Injection Pattern (Critical)
Each platform `Program.cs` / `AppDelegate` calls three static initializers in `AfterSetup` **before** any UI code runs:
```csharp
HeifLoader.Initialize(new PlatformHeifDecoder());   // HEIF decoding
MemoryBudget.Initialize(new PlatformMemoryBudget()); // memory limits
SettingsService.ConfigureStorage(new PlatformStorage()); // settings persistence
```
Platform-specific implementations live in `<PlatformProject>/Core/`. When adding a new platform capability, follow this same static-init + interface pattern.

### Data Flow: Image Loading Pipeline
`FolderViewModel.LoadNewImageFolder` → populates `AllFiles` / `FilteredFiles` → setting `MainViewModel.CurrentFile` triggers `ImageViewModel.LoadImageAsync` → `BitmapLoader.GetBitmapAsync` (LRU cache + EXIF rotation) → `BitmapPrefetcher` preloads neighbors.

### Data Flow: Star Rating
`MainViewModel.SetRatingAsync` → `XmpWriter.WriteRatingAsync` (byte-level in-place XMP edit with backup) → syncs to `ImageFile.HiddenFiles` (companion RAW files) → reloads EXIF → `FolderVM.RefreshFilters`.

## Development / Run / Publish

### Local Build & Run (Rider-first)
For day-to-day development, prefer **running the startup project directly in Rider**. This is closer to real app startup behavior than ad-hoc CLI commands, especially for UI, platform initialization, and device deployment.

| Scenario | Rider startup project / action | Closest CLI equivalent | Notes |
|---|---|---|---|
| Windows desktop debug | Run `PhotoViewer.Desktop` | `dotnet run --project .\PhotoViewer.Desktop\PhotoViewer.Desktop.csproj -c Debug` | Closest to clicking Run in Rider on Windows. |
| macOS desktop debug | Run `PhotoViewer.Mac` | `dotnet run --project ./PhotoViewer.Mac/PhotoViewer.Mac.csproj -c Debug` | Use on macOS only. |
| Android debug | Run `PhotoViewer.Android` on device/emulator | `dotnet build .\PhotoViewer.Android\PhotoViewer.Android.csproj -c Debug /p:AndroidSdkDirectory="$env:LOCALAPPDATA\Android\Sdk"` | Rider Run is preferred because it also deploys and starts the app. |
| iOS debug | Run `PhotoViewer.iOS` on simulator/device | `dotnet build ./PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Debug` | Use Rider/Xcode device tooling; build-only CLI is not equivalent to Rider Run. |

Use CLI mainly for **build validation** or scripting. If CLI succeeds but Rider Run fails, trust Rider/device configuration first and debug from the startup project used in Rider.

#### Common build validation commands

```powershell
# Windows desktop
dotnet build .\PhotoViewer.Desktop\PhotoViewer.Desktop.csproj -c Debug

# Android (requires Android SDK)
$env:ANDROID_SDK_ROOT = "$env:LOCALAPPDATA\Android\Sdk"
dotnet build .\PhotoViewer.Android\PhotoViewer.Android.csproj -c Debug /p:AndroidSdkDirectory="$env:ANDROID_SDK_ROOT"
```

```bash
# macOS desktop
dotnet build ./PhotoViewer.Mac/PhotoViewer.Mac.csproj -c Debug

# iOS (requires Xcode)
dotnet build ./PhotoViewer.iOS/PhotoViewer.iOS.csproj -c Debug
```

### Publish distributable artifacts
Distribution packaging is documented centrally in `PUBLISH.md`.

- `PUBLISH.md` covers Windows / Android / macOS package commands and output paths
- All package scripts also copy final artifacts into the repository root `release/` folder
- `release/` is only for collected distributable files, not source-controlled assets

NuGet versions are centrally managed in `Directory.Packages.props` — **always edit versions there**, not in individual `.csproj` files.

## Key Conventions

### MVVM Wiring
- `ViewLocator` maps ViewModels → Views by name convention: `FooViewModel` → `FooView` (namespace swap).
- `MainViewModel` is the composition root — owns `FolderVM`, `ImageVM`, `DetailVM`, `ControlVM`, `Settings`.
- Sub-ViewModels receive `MainViewModel` via constructor (not DI), access siblings through it.
- Use `ReactiveObject` + `RaiseAndSetIfChanged` for all observable properties. `ViewModelBase : ReactiveObject` is the base class.

### Window Selection (in `App.axaml.cs`)
- Windows → `MainWindowForWindows` (custom title bar)
- macOS → `MainWindowForMac` (native title bar)
- Mobile → `SingleView`

### Settings Architecture
`SettingsViewModel` is a **partial class** split across multiple files (`SettingsViewModel.*.cs`) by concern: `Hotkeys`, `ExifDisplay`, `FileFormats`, `BitmapCache`, `ImagePreview`, `Layout`, `Rating`. Follow this pattern when adding new settings categories.

### Static Service Classes
`BitmapLoader`, `HeifLoader`, `XmpWriter`, `ExifLoader`, `MemoryBudget` are **static classes** (not DI-registered). They use static state and are accessed directly. This is intentional for performance-critical paths.

### Compiled Bindings
`AvaloniaUseCompiledBindingsByDefault` is **true** — all AXAML bindings must use `x:DataType` and compiled binding syntax. Avoid `{Binding Path}` without data type context.

## File Naming & Organization
- Views: `PhotoViewer/Views/Main/` (main UI), `PhotoViewer/Views/Settings/` (settings pages)
- Windows: `PhotoViewer/Windows/` — platform-specific window shells (`MainWindowForWindows`, `MainWindowForMac`, `SingleView`)
- Each `.axaml` has a matching `.axaml.cs` code-behind
- Reusable controls: `PhotoViewer/Controls/`
- Value converters: `PhotoViewer/Converters/`
- Platform-specific capability implementations: `<PlatformProject>/Core/`

## Task → File Quick Reference
> Use this to jump directly to the right file without searching.

| Task | Files to edit |
|---|---|
| Add / change a **hotkey** | `SettingsViewModel.Hotkeys.cs`, `Views/Settings/ControlSettingsView.axaml` |
| Add / change **EXIF display fields** | `SettingsViewModel.ExifDisplay.cs`, `Views/Settings/ExifSettingsView.axaml` |
| Add a new **settings category** | New `SettingsViewModel.{Category}.cs` (partial class) + new `Views/Settings/{Category}SettingsView.axaml` |
| Modify **image loading / LRU cache** | `Core/BitmapLoader.cs` |
| Modify **prefetch behavior** | `Core/BitmapPrefetcher.cs` |
| Modify **EXIF / thumbnail reading** | `Core/ExifLoader.cs` |
| Modify **XMP star rating write logic** | `Core/XmpWriter.cs` |
| Modify **HEIF decoding** (platform-specific) | `<PlatformProject>/Core/*HeifDecoder.cs` |
| Modify **memory budget** (platform-specific) | `<PlatformProject>/Core/*MemoryBudget.cs` |
| Modify **settings persistence** (platform-specific) | `<PlatformProject>/Core/*SettingsStorage.cs` |
| Change **layout / panel visibility** | `MainViewModel.cs` (`UpdateLayoutFromSettings`), `SettingsViewModel.Layout.cs` |
| Change **thumbnail strip UI** | `Views/Main/ThumbnailView.axaml`, `FolderViewModel.cs` |
| Change **main image view / zoom** | `Views/Main/ImageView.axaml`, `ImageViewModel.cs` |
| Change **control panel buttons** | `Views/Main/ControlView.axaml`, `ControlViewModel.cs` |
| Change **detail preview panels** | `Views/Main/DetailView.axaml`, `Controls/DetailPreview.axaml`, `DetailViewModel.cs` |
| Add a **new platform** | New `<Platform>/Core/` impls + `Program.cs`/`AppDelegate.cs` `AfterSetup` injections |
| Modify **window chrome / title bar** | `Windows/MainWindowForWindows.axaml` (Windows) or `Windows/MainWindowForMac.axaml` (macOS) |
| Modify **cache size / format settings** | `SettingsViewModel.BitmapCache.cs`, `Views/Settings/ImageSettingsView.axaml` |
| Modify **supported file formats** | `SettingsViewModel.FileFormats.cs`, `Views/Settings/FileSettingsView.axaml` |
| Publish **Windows single-file EXE** | `PhotoViewer.Desktop/publish-win-x64-singlefile.ps1`, `PhotoViewer.Desktop/PhotoViewer.Desktop.csproj`, `Directory.Build.props`, `PUBLISH.md`, `AGENTS.md` |
| Publish **Android APK** | `PhotoViewer.Android/publish-android-apk.ps1`, `PhotoViewer.Android/PhotoViewer.Android.csproj`, `Directory.Build.props`, `PUBLISH.md`, `AGENTS.md` |
| Publish **macOS build** | `PhotoViewer.Mac/publish.sh`, `Directory.Build.props`, `PUBLISH.md`, `AGENTS.md` |

## Documentation Maintenance
When adding new files or making significant changes, update this file's Quick Reference table. Keep descriptions concise.

