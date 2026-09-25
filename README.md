# AppSentry

An open-source Windows 11 application that monitors software installs, updates, and removals in real-time — similar to the install-tracking feature in IObit Uninstaller, but free and open source.

## Features

### Core Monitoring
- Detects **installed**, **updated**, **removed**, **modified** and **failed** installs from multiple sources:
  - Windows Registry: HKLM (64- and 32-bit) and every signed-in user's hive, including Entra ID accounts
  - Microsoft Store / MSIX packages (WinRT PackageManager API — no PowerShell)
  - Windows Event Log (MsiInstaller): catches silent/remote MSI installs, **who** ran them, and failures
  - Folders: `Program Files`, `Program Files (x86)` and each user's `%LOCALAPPDATA%\Programs`
  - Windows services, drivers and scheduled tasks — including changes to an existing service's binary or account, or a task's action
  - Scoop apps (which never touch the registry)
- **Real-time**: registry, event log and folder notifications trigger a scan within seconds; the interval scan is a safety net
- Upgrades that change the uninstall key (MSI major upgrades, 32→64-bit moves) are one **Updated** event, not remove + install
- A source that fails or can't be read (Store error, signed-out user, no elevation) never shows up as mass removals
- Identifies installer technology (MSI, WiX Bundle, InnoSetup, NSIS, InstallShield, Squirrel, ClickOnce, Store)
- Package-manager ownership from each manager's own records (**Chocolatey**, **Scoop**, **Winget**)
- Install **size** and the time the change **actually happened** are captured when it's detected

### User Interface
- Color-coded history list (green = installed, blue = updated, red = removed/failed, amber = modified)
- **Dark mode**, **light mode**, or follow **system theme**
- Owner-drawn ListView with alternating rows and accent colors
- **Resizable and reorderable columns** (layout persisted across restarts)
- **Search/filter** bar for real-time filtering by app name, publisher, type, etc.
- **Column sorting** — click any header to sort ascending/descending
- Custom programmatic app icon (no external assets required)

### Notifications
- **Popup notification** slides in from the bottom-right when changes are detected
- Stays visible until dismissed, or **auto-hides** after 10/30/60 seconds (configurable)
- **Optional sound alerts** when changes are detected
- Click "View Details" to jump to the event in the main window
- **Exclusions**: exact names, wildcards (`7-Zip*`, `[Scheduled Task] \Adobe*`) and version-free matching, so an exclusion keeps working after the app updates. Choose "don't notify" or "don't log at all".

### Tools
- **Snapshot Comparison** — pick a date range and see all changes between those dates, with quick-select buttons (Today, 24h, 7 days, 30 days, All)
- **Diff View** — for updated or modified apps, every uninstall-key value before vs after the change
- **Export CSV** — export full history or comparison results
- **Right-click context menu**:
  - Uninstall app (MSI via `msiexec /x`, Store apps via PackageManager; elevation only for machine-wide apps)
  - Open install location in Explorer
  - Copy app name or full details to clipboard
  - View details or diff

### System Integration
- **Background service** (optional): runs as LocalSystem, covers every user, keeps monitoring when nobody is signed in; the tray app connects to it automatically
- Every change written to the **Windows Application log** (source `AppSentry`) for SIEM collection (e.g. Wazuh)
- **Minimize to system tray** with custom icon
- **Run at Windows startup** option
- Single-instance enforcement (won't run duplicate copies)
- Configurable safety-net scan interval (1, 5, 10, 30 minutes, or off)

## Screenshots

*(Coming soon)*

## How It Works

Everything that detects, schedules and stores lives in **AppSentry.Core**. The tray app talks to it through one interface, either in-process or over a named pipe to the service.

Each scan runs this pipeline, one scan at a time:

1. **MSI events** — new MsiInstaller events since the saved bookmark (read first, so everything they mention is visible to step 2)
2. **Inventory** — registry, Store and Scoop. Each *scope* (HKLM 64/32, each user's hive, each user's Store packages) is only compared if it was read in full this time; anything else is carried forward unchanged
3. **Diff** — installs, updates (same key, new version), modifications (same version, changed publisher/location/uninstaller) and removals, then **correlation**: a removal and an install of the same product in one scan become one update
4. **Enrich** — the event log's user and timestamp are attached to the matching registry change; failures and hidden MSI components are recorded on their own
5. **Folders** — new folders are held for 2 minutes (installers create the folder before the registry key), and any folder owned by a known app is skipped
6. **Services, drivers, tasks** — added, removed or reconfigured; Microsoft components are filtered the same way in both directions
7. **Commit** — exclusions are applied, then the events and every source's new baseline are written in **one SQLite transaction**

Real-time triggers (`RegNotifyChangeKeyValue`, `EventLogWatcher`, `FileSystemWatcher`) only ask for a scan (debounced 10 s); they never create events themselves.

## Service Mode

```bat
AppSentry.exe --install-service
AppSentry.exe --uninstall-service
```

`--install-service` copies AppSentry to `%ProgramFiles%\AppSentry`, registers a delayed-start service with restart-on-failure, and starts it (UAC prompt). `--uninstall-service` stops and removes it; history is kept. The **🛡 Install Service** toolbar button does the same.

When the service is running, the tray app shows "Service mode" and reads everything from it over `\\.\pipe\AppSentry`:

- Any signed-in user can view history and request a scan
- Clearing history and changing exclusions/settings need an administrator account (a normal, non-elevated admin session is enough)
- The tray app only trusts a pipe served from session 0, so another user can't impersonate the service

**Windows event log** (Application log, source `AppSentry`):

| Event ID | Change | Level |
|----------|--------|-------|
| 1000 | Installed | Information |
| 1001 | Updated | Information |
| 1002 | Removed | Information |
| 1003 | Modified | depends |
| 1004 | Failed | depends |

The message body is `Key: value` lines (App, Version, PreviousVersion, Publisher, ChangedBy, InstalledFor, Source, Key, OccurredAt, Details).

## Requirements

- Windows 10 1809 or later / Windows 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) *(for running a framework-dependent build)*
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) *(for building from source)*

## Build from Source

```bash
git clone https://github.com/vonkdj98/AppSentry.git
cd AppSentry

# Run in development
dotnet run --project AppSentry/AppSentry.csproj

# Run against a separate data folder (testing; runs beside a normal install)
dotnet run --project AppSentry/AppSentry.csproj -- --data-dir C:\Temp\AppSentryTest

# Tests. The integration tests scan this machine and make (and undo) harmless
# HKCU and scheduled-task changes; skip them with the filter.
dotnet test
dotnet test --filter "Category!=Integration"

# Build a single self-contained exe (no .NET runtime required)
dotnet publish AppSentry/AppSentry.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true
```

The output exe will be in:
```
AppSentry\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish\AppSentry.exe
```

## Data Storage

| Where | Mode | Contents |
|-------|------|----------|
| `%APPDATA%\AppSentry\appsentry.db` | Tray app on its own | History, inventory baseline, source bookmarks, exclusions, settings (SQLite) |
| `%ProgramData%\AppSentry\appsentry.db` | Service | Same, machine-wide; readable by SYSTEM and Administrators only |
| `engine.log` next to the database | Both | Diagnostics: scans, failures, trigger activity (rotates at 1 MB) |
| `%APPDATA%\AppSentry\*.txt` | Tray app | UI preferences: theme, sound, notification auto-hide, column layout |

Upgrading from v1: `history.json`, `exclusions.json` and `interval.txt` are imported on first launch and renamed to `*.imported`. The v1 `snapshot.json` is not imported; the first scan takes a fresh baseline so nothing is falsely reported. An unreadable database is moved aside (`appsentry.db.corrupt-<time>`), never overwritten.

## Project Structure

```
AppSentry.Core/                     # engine, no UI
  Engine/MonitorEngine*.cs          # scheduling, scan pipeline, real-time hookup
  Sources/                          # RegistrySource, StoreSource, MsiEventSource, FileSystemSource,
                                    # ServiceSource, TaskSource, PackageManagerSource
  Detection/                        # ChangeDetector (scopes, correlation), MsiCorrelator, ExclusionMatcher
  Storage/                          # SqliteStore, LegacyImporter
  Triggers/ChangeTriggers.cs        # registry / event log / folder change notifications
  Backend/                          # IMonitorBackend, LocalBackend, PipeBackend
  Ipc/                              # named-pipe protocol and server
  Service/                          # Windows service host, installer, event log writer
  Uninstaller.cs
AppSentry/                          # WinForms tray app
  Program.cs                        # entry point: tray app, --service, --install-service, ...
  MainForm.cs                       # main window, toolbar, history list
  InstalledAppsForm.cs, ExclusionsForm.cs, SnapshotCompareForm.cs, DiffViewForm.cs, NotificationForm.cs
AppSentry.Tests/                    # xUnit
```

## Contributing

Contributions are welcome! Feel free to open issues or submit pull requests.

## License

MIT
