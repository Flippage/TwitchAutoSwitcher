# Changelog

All notable changes to AutoSwitcher. Each release on GitHub shows the section for its version.

<!--
How to add a release:
  1. Add a new "## [x.y.z]" section at the top (below this comment) with Added / Changed / Fixed / Removed.
  2. Commit, then Actions → Build AutoSwitcher → Run workflow with the same version number.
-->

## [1.1.6] - 2026-10-08

### Changed
- Removing a category now asks for confirmation in an in-app dialog that matches the rest of AutoSwitcher, showing the category's box art and executables, instead of a Windows message box. Esc or clicking outside cancels; Enter confirms.

## [1.1.5] - 2026-10-08

### Fixed
- The On Stream panel no longer changes height slightly when the line under the category switches between "Set on Twitch" and "✓ … detected".

## [1.1.4] - 2026-10-08

### Changed
- On shorter windows, the On Stream box art now shrinks to fit instead of disappearing. It only hides when there's room for less than half its size.

## [1.1.3] - 2026-10-08

### Changed
- **Switch now** on a game that isn't counting down now pauses auto-switch, so you stay on that category until you resume. On a game that's counting down, it still just skips the timer.
- Click the **AUTO OFF** tag in the On Stream panel to resume auto-switch.

### Fixed
- After a manual **Switch now**, auto-switch could stop reacting to the game you'd switched away from. Turning auto-switch back on now picks up from the window you're actually using.
- The On Stream panel could overlap the Settings menu item on shorter windows. The box art now hides whenever space is tight.

## [1.1.2] - 2026-10-08

### Added
- When several mapped games are running, flip between them in the sidebar with the ‹ › arrows and switch to any of them with **Switch now**.
- A detection log, so switching problems can be traced. Open it from **Settings → Storage & diagnostics → Open log folder**.
- Custom names now show on the executables in **Mappings**.

### Changed
- Long category names shrink to fit instead of being cut off, in both the On Stream panel and the detected-game card.

### Fixed
- In focus mode, a detected game could disappear and its switch be cancelled even though the game window stayed focused (for example when a browser or helper window briefly took focus). Focus is now checked against the actual foreground window before cancelling.
- "Switch now" no longer wraps onto two lines.

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
