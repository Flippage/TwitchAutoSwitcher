# Changelog

All notable changes to AutoSwitcher. Each release on GitHub shows the section for its version.

<!--
How to add a release:
  1. Add a new "## [x.y.z]" section at the top (below this comment) with Added / Changed / Fixed / Removed.
  2. Commit, then Actions → Build AutoSwitcher → Run workflow with the same version number.
-->

## [1.1.1] - 2026-10-08

### Fixed
- AutoSwitcher 1.1.0 started without showing a window. It now starts normally.

### Changed
- If AutoSwitcher can't start, it now shows an error message and saves details to `%APPDATA%\AutoSwitcher\error.log`, instead of running hidden in the background.
- Every build is now launched and checked automatically before it can be released.

## [1.1.0] - 2026-10-08

### Added
- **Automatic updates.** AutoSwitcher checks GitHub for new versions and shows an in-app notification with **Update now**. Updates are verified with a checksum and installed with one click.
- **Settings → Updates** with update status, **Check for updates**, and two options:
  - **Automatically download new versions in the background** (only while you're offline)
  - **Update automatically on next launch**
- An amber **Update** tag on Settings while an update is waiting.
- **On Stream panel** in the sidebar, which always shows the category currently set on Twitch.
- **Mapped game detected** and **Switching in Ns** (with a timer bar) under the On Stream panel when a detected game differs from what's on stream, plus a **Switch now** button.

### Changed
- **Account** is now **Settings** and has moved to the bottom of the menu: Mappings, Stream Titles, Manual, Behaviour, Settings.
- The window now opens taller (900 px) so the sidebar fits without scrolling.

## [1.0.0] - 2026-10-07

### Added
- First release: automatic Twitch category switching by launched or focused game, stream title templates with name pills, window-title matching for emulators, Manual mode, Recent Titles, Live/Offline status, silent toast notifications with box art, and one-click Twitch sign-in.
