# CLAUDE.md

Media Flattener is a desktop app that recursively scans a chosen root folder. It previews a move plan, then moves every file into `image/`, `video/`, `other/` and, optionally, `vr-video/` under that root. It can optionally group files into date‑range folders and remove emptied source folders. Scanning is read‑only and moving only happens after the user presses Continue.

## Layout

| Path | What it is |
|---|---|
| `Core.cs` | **Shared engine**: classification, `Engine.Scan` (read‑only plan), `Engine.Execute` (journaled moves and cleanup). No UI code. |
| `VrVideoDetector.cs` | **Shared**: ffprobe‑based VR (spherical projection) detection. |
| `MediaFlattener.csproj`, `App.xaml*`, `MainWindow.xaml*` | **Windows** front end (WPF, `net8.0-windows`). |
| `Linux/` | **Linux** front end (Avalonia 11, `net8.0`). It links `../Core.cs` and `../VrVideoDetector.cs`, and `MainWindow.axaml*` is a port of the WPF window. |
| `Tests/CoreTests.cs` | Engine, cleanup and VR tests shared by both test runners (`partial class Program`). |
| `Tests/Program.cs` + `Tests/MediaFlattener.Tests.csproj` | Windows runner: shared tests plus the WPF UI test, which renders `preview.png`. |
| `Tests/Linux/` | Linux runner: shared tests plus the Avalonia.Headless UI test, which renders `preview-linux.png`. |
| `Tests/Fixtures/` | Tiny synthetic VR/non‑VR clips (regenerate with `Tests/GenerateFixtures.py`, needs ffmpeg). |

The WPF project and `Tests/MediaFlattener.Tests.csproj` exclude `Linux\**` from globbing. Keep it that way, or Avalonia files get pulled into the WPF build.

## Commands

Linux (the .NET 8 SDK is in `~/.dotnet` on the dev machine, so run `export DOTNET_ROOT=~/.dotnet PATH=~/.dotnet:$PATH` first):
- `./test.sh`: all Linux tests (needs `ffprobe` on PATH).
- `./build.sh`: self‑contained single file at `App-linux/media-flattener`.
- `./install-desktop.sh`: adds a user `.desktop` launcher.
- Check that the Windows side still compiles from Linux: `dotnet build MediaFlattener.csproj -p:EnableWindowsTargeting=true` (same for `Tests/MediaFlattener.Tests.csproj`). WPF tests can only *run* on Windows.

Windows: `Build.ps1` publishes to `App\`, and `Test.ps1` runs the tests.

## Safety invariants (do not regress)

- Scan never creates, moves or deletes anything. Any option change invalidates the plan.
- Write‑ahead JSONL journal in `.flattener-logs/`: a `pending` entry is written before each move or folder delete. If logging fails, the session stops.
- `File.Move(..., overwrite: false)` with no copy/delete fallback. Each source's length, created and modified time is re‑checked before it moves.
- Only non‑recursive `Directory.Delete`, and only for scanned source folders inside the root. The root, output folders, logs and the app's own folder are never deleted.
- Links (junctions, symlinks, reparse points) are never followed, including in ancestors of the root (`ValidatePath`).
- Top‑level `image`, `video`, `other`, `vr-video` and `.flattener-logs` are reserved and excluded from scans.

## Cross‑platform rules

- Keep the engine UI‑free and shared. OS differences go behind `OperatingSystem.IsWindows()` in the engine, never in a copied fork.
- Keep the two UIs feature‑matched. When you change one window, port the change to the other and update both UI tests.
- Path comparisons are deliberately **case‑insensitive on every OS**. On Linux that is conservative (extra suffixes and extra protection), and it is correct for exFAT/NTFS drives.
- Date source: Windows uses `CreationTimeUtc`. Elsewhere it uses the earlier of birth time and `LastWriteTimeUtc` (`Engine.CreatedUtc`). On Linux, `File.SetCreationTime` actually sets mtime, which is how the tests control dates.
- ffprobe is `ffprobe.exe` on Windows and `ffprobe` elsewhere. It is searched for beside the app, then in absolute PATH entries only.

## Status / roadmap

- 2026‑10‑23: Linux (Avalonia) port added on branch `linux-port`. All 16 Linux test groups pass on Ubuntu 26.04 (GNOME/Wayland). The Windows projects still compile, but the Windows tests have not been re‑run since the test split.
- Next: run `Test.ps1` on Windows, then push/merge so the GitHub repo supports both operating systems. Possible follow‑ups: CI matrix (windows-latest + ubuntu-latest), a Linux release artifact, and a shared project/library if more front ends are added.
