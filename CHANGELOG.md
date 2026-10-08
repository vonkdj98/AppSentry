# Changelog

## 2.1.2

- **Installer**: upgrading, reinstalling or uninstalling while AppSentry is running no longer stalls for about five
  minutes (twice) on PCs where Windows' automatic application shutdown is disabled by policy: the installer now stops the
  service and closes the tray app itself before Windows Installer checks for files in use.

## 2.1.1

- **Filter chips**: a chip that is on shows a check mark, a mouse click no longer leaves a focus outline that looked like
  a third state, a tooltip says what a chip does, and the line under the chips says what is filtering ("Showing 8 of 12
  changes · only Installed, Updated").

## 2.1.0

A redesign, plus the fixes since 2.0.2.

- **New look**: AppSentry's own indigo accent replaces the Windows accent color (check boxes, toggles, chips, links and
  buttons included), tuned for light and dark. The selected page is tinted in the navigation. Apps without an icon get a
  colored letter tile instead of an identical grey box. Search boxes have a clear button, and filters sit on two tidy
  rows. The window opens 1360 px wide (never larger than the screen) and won't go below 1200.
- **Settings**: an About section with the version and a *Copy support info* button (version, Windows version and scan
  status; nothing about your apps or files).
- **Security**: the service unpacks its native libraries into an admin-only folder instead of `C:\Windows\Temp`; the
  pipe caps request size and connections, refuses to serve under a name another process grabbed, and throttles scans by
  standard users; Chocolatey and Scoop files are read with DTDs off and a size cap; the installer copies only the exe
  and won't adopt a data folder someone else created.
- **Fixes**: the service keeps its own identity after a tray client connects; Activity no longer folds an *All users*
  entry into a per-profile one, and counts and copies per row; the tray tooltip keeps what needs a look when an
  edition adds long lines; the installer waits for a locked file instead of giving up; `--install-service --quiet`
  for installers and scripts.

## 2.0.2 and earlier

See the [releases](https://github.com/vonkdj98/AppSentry/releases).
