# AppSentry

An open-source Windows 11 application that monitors software installs, updates, and removals in real-time — similar to the install-tracking feature in IObit Uninstaller, but free and open source.

## Features

### Core Monitoring
- Detects **installed**, **updated**, and **removed** apps from multiple sources:
  - Windows Registry (HKLM, HKCU, HKU for all users)
  - Microsoft Store / AppX packages
  - Windows Event Log (catches silent/remote MSI installs)
  - File System monitoring (`C:\Program Files` and `C:\Program Files (x86)`)
  - Windows Services and Scheduled Tasks
- Detects **who installed** each app and **where it was installed from**
- Identifies installer technology (MSI, InnoSetup, NSIS, Store, WiX, Squirrel, ClickOnce)
- Detects package manager installs (**Winget**, **Chocolatey**, **Scoop**)
- Tracks install **disk size**

### User Interface
- Color-coded history list (green = installed, blue = updated, red = removed)
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

### Tools
- **Snapshot Comparison** — pick a date range and see all changes between those dates, with quick-select buttons (Today, 24h, 7 days, 30 days, All)
- **Diff View** — for updated apps, see a side-by-side comparison of registry values (old vs new)
- **Export CSV** — export full history or comparison results
- **Right-click context menu**:
  - Uninstall app (launches the uninstaller with admin elevation)
  - Open install location in Explorer
  - Copy app name or full details to clipboard
  - View details or diff

### System Integration
- **Minimize to system tray** with custom icon
- **Run at Windows startup** option
- Single-instance enforcement (won't run duplicate copies)
- Configurable auto-scan interval (1, 5, 10, 30 minutes, or off)

## Screenshots

*(Coming soon)*

## How It Works

AppSentry monitors multiple data sources to detect software changes:

1. **Registry Scanning** — Reads installed app entries from `HKLM\...\Uninstall`, `HKCU\...\Uninstall`, and `HKU\{SID}\...\Uninstall` (other users, requires admin)
2. **Microsoft Store** — Runs `Get-AppxPackage` via PowerShell to detect Store/sideloaded apps
3. **Windows Event Log** — Monitors MsiInstaller events (IDs 11707, 11724, 1033, 1022) to catch silent or remote installs
4. **File System Watcher** — Watches `C:\Program Files` directories for new/deleted folders
5. **Service & Task Scanner** — Detects new Windows Services and Scheduled Tasks

On each scan, the current state is compared against the previous snapshot. Any differences are recorded as change events and persisted to disk.

## Requirements

- Windows 10/11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) *(for running the pre-built exe)*
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) *(for building from source)*

## Build from Source

```bash
git clone https://github.com/vonkdj98/AppSentry.git
cd AppSentry

# Run in development
dotnet run --project AppSentry/AppSentry.csproj

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
AppSentry\bin\Release\net9.0-windows\win-x64\publish\AppSentry.exe
```

## Data Storage

All data is stored locally in `%APPDATA%\AppSentry\`:

| File | Purpose |
|------|---------|
| `snapshot.json` | Current baseline of all installed apps |
| `history.json` | All detected change events |
| `theme.txt` | Theme preference (System/Dark/Light) |
| `sound.txt` | Sound alert preference |
| `notifyhide.txt` | Notification auto-hide preference |
| `columns.txt` | Column layout (widths and order) |

## Project Structure

```
AppSentry/
  Program.cs                  # Entry point, single-instance mutex
  MainForm.cs                 # Main window, toolbar, ListView, all UI logic
  Models/
    InstalledApp.cs            # Installed app data model
    ChangeEvent.cs             # Change event model, enums
  RegistryScanner.cs           # Registry + Store app scanning
  ChangeDetector.cs            # Snapshot diff algorithm
  SnapshotStore.cs             # JSON persistence
  EventLogMonitor.cs           # Windows Event Log monitoring
  FileSystemMonitor.cs         # Program Files folder watcher
  ServiceTaskScanner.cs        # Windows Services + Scheduled Tasks
  PackageManagerDetector.cs    # Winget/Chocolatey/Scoop detection
  NotificationForm.cs          # Popup notification with slide animation
  DiffViewForm.cs              # Registry diff viewer for updates
  SnapshotCompareForm.cs       # Date-range snapshot comparison tool
  AppIcon.cs                   # Programmatic icon generation (GDI+)
```

## Contributing

Contributions are welcome! Feel free to open issues or submit pull requests.

## License

MIT
