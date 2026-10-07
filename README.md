<p align="center">
  <img src="docs/icon.png" width="96" alt="AutoSwitcher icon">
</p>

<h1 align="center">AutoSwitcher</h1>

<p align="center">
  Automatically set your Twitch <b>category</b> and <b>stream title</b> from the game you're playing.<br>
  Built for multiworld and multi-game streams. No Streamer.bot or OBS plugins required.
</p>

<p align="center">
  <img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows&logoColor=white">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white">
  <img alt="WPF" src="https://img.shields.io/badge/UI-WPF-22C7E6">
</p>

---

## Contents

- [Features](#features)
- [Download & install](#download--install)
- [Getting started](#getting-started)
- [Stream titles](#stream-titles)
- [Emulators & window-title matching](#emulators--window-title-matching)
- [Detection modes](#detection-modes)
- [Notifications](#notifications)
- [Privacy & data](#privacy--data)
- [Performance](#performance)
- [Building from source](#building-from-source)
- [Troubleshooting](#troubleshooting)

---

## Features

| | |
|---|---|
| 🎮 **Automatic category switching** | Map Twitch categories to one or more game executables. Launch or focus a game and your category updates. |
| 📝 **Stream title templates** | Write your title once with **Game Name**, **Game Executable Name** and **Custom Name** pills. They're filled in on every switch. |
| 🕹️ **Emulator support** | Match by window title as well as by exe, so one emulator can map to many games. Wildcards are supported. |
| 🔍 **Category search with box art** | Search Twitch categories directly. Box art is shown everywhere and cached locally. |
| ✋ **Manual mode** | Set a category and title by hand, which pauses auto-switching until you resume it. |
| 🕘 **Recent titles** | Your current Twitch title and titles you've used before, one click away. |
| 📡 **Live status** | A Live / Offline indicator and a persistent **Now Playing** panel with box art. |
| 🔔 **Silent toast notifications** | Native Windows notifications with box art when your category or title changes. They never chime on stream. |
| 🔐 **One-click Twitch login** | Secure device-code sign-in that stays connected. There's no client secret and no password stored. |
| 🪶 **Lightweight** | Event-driven detection, tray mode and tiny network usage, so it won't affect game performance. |

---

## Download & install

1. Go to **[Releases](../../releases/latest)** and download **`AutoSwitcher-vX.Y.Z.zip`** (recommended) or the `.exe`.
2. Unzip it anywhere and run **`AutoSwitcher.exe`**. There's no installer.

| File | Size | Notes |
|---|---|---|
| `AutoSwitcher-vX.Y.Z.zip` | ~70 MB | Standalone app, zipped. The easiest to share. |
| `AutoSwitcher-vX.Y.Z.exe` | ~180 MB | Standalone app. Runs on any Windows 10/11 x64 PC. |
| `AutoSwitcher-vX.Y.Z-small-needs-dotnet10.exe` | ~26 MB | Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0). |

> **"Windows protected your PC"?** The app isn't code-signed yet, so SmartScreen may warn on first launch.
> Click **More info → Run anyway**. Some chat apps (e.g. Discord) also block unsigned `.exe` files, so share the `.zip` or the release link instead.

---

## Getting started

1. **Connect Twitch:** open **Account** and click **Connect with Twitch**. Approve the short code at `twitch.tv/activate`. That's it, and you stay signed in.
2. **Add your games:** on **Mappings**, click **Add category**:
   - Search for the Twitch category (box art included).
   - Add one or more executables with **Browse for .exe…** or **Pick running**.
   - Optionally set a **Custom Name** for each executable, e.g. `Zelda OoT SoH`.
3. **Choose how to detect:** on **Behaviour**, pick **A game launches** or **A game window is focused**.
4. **Set your title (optional):** on **Stream Titles**, write a template and insert name pills.

The **Now Playing** panel in the sidebar shows what's detected, what's set on Twitch, and whether auto-switch and auto title are on.

---

## Stream titles

Build a title template once and AutoSwitcher fills it in on every switch:

```
AUS 18+ | Multiworld Day 2 | Now playing: [Custom Name] | !discord
```

| Pill | Example | Source |
|---|---|---|
| **Game Name** | The Legend of Zelda: Ocarina of Time | The Twitch category |
| **Game Executable Name** | Ship of Harkinian | Read from the exe's product info (editable) |
| **Custom Name** | Zelda OoT SoH | Your short name per executable (falls back to the executable name) |

- **Live Title Preview** shows exactly what will be sent, with the real names filled in.
- The **character counter** counts the *filled-in* title. Anything past Twitch's 140-character limit is highlighted in red.
- **Only real changes are sent.** Switching between two executables of the same category doesn't touch your title unless its filled-in text actually changes.
- **Apply now** pushes the title immediately. **Recent Titles** keeps your current and previous titles one click away.

---

## Emulators & window-title matching

Many games share one executable (`project64.exe`, `retroarch.exe`…). Turn on **Match window title** for an executable and enter the part of the window title that identifies the game:

| Window title | Match text | Result |
|---|---|---|
| `Donkey Kong 64 (USA) - app31564` | `Donkey Kong 64 (USA)` | ✅ matches regardless of the changing suffix |
| `Super Mario 64 [60 FPS]` | `Super Mario 64` | ✅ FPS counters in the title don't reset the switch |
| `Zelda - OoT - USA` | `Zelda*USA` | ✅ `*` matches anything in between |

- Matching ignores upper and lower case, and the text can appear anywhere in the title.
- **Use current title** copies the live window title so you only need to trim it.
- Map the same emulator to several categories with different title text. The most specific match wins, and a mapping *without* title matching acts as the fallback.
- Loading a new game inside an already-focused emulator is detected too.

---

## Detection modes

| Mode | Best for | How it works |
|---|---|---|
| **A game window is focused** | Multiworlds with several games open | Switches when you click into a mapped game. A configurable **focus delay** (default 8 s) prevents switching while you click back and forth. |
| **A game launches** | One game at a time | Switches when a mapped game starts. When it closes, it switches to another running mapped game, or to your **fallback category** (optional). |

---

## Notifications

When the category or title changes, a native Windows toast shows:

- the category's **box art**,
- **Stream category updated** / **Stream title updated**,
- how it happened: *focused window*, *launched app*, *fallback*, *set manually*.

Toasts are **silent**, and a new switch replaces the previous toast. You can turn them off on **Behaviour**, or in Windows **Settings → System → Notifications → AutoSwitcher**.

---

## Privacy & data

- **Twitch permission:** only `channel:manage:broadcast`, which lets the app change your title and category.
- **Sign-in:** OAuth Device Code flow with a public client. No password is entered into the app and no client secret ships with it.
- **Tokens** are encrypted for your Windows user with DPAPI. They refresh automatically, and if the app isn't used for 30 days you sign in again.
- **Stored locally:**
  - `%APPDATA%\AutoSwitcher\config.json`: mappings, titles, settings
  - `%APPDATA%\AutoSwitcher\tokens.dat`: encrypted tokens
  - `%LOCALAPPDATA%\AutoSwitcher\art\`: cached box art
- Nothing is sent anywhere except the Twitch API.

---

## Performance

AutoSwitcher is designed to sit in the tray during a stream without being noticed:

- **Focus mode is event-driven:** Windows notifies the app of foreground changes, so there's no polling loop.
- **Launch mode** diffs the process list every 2 s and only inspects *new* processes.
- **Title matching** listens only to the focused emulator's window, not to every window on the system.
- Game exits are detected via the process handle, also without polling.
- In the tray, nothing is rendered and memory is trimmed.
- Network use: one small Twitch status call per minute, plus a request only when something actually changes.

---

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```bat
build.cmd          :: small EXE  → dist\AutoSwitcher.exe            (needs .NET 10 Desktop Runtime)
build.cmd full     :: standalone → dist-standalone\AutoSwitcher.exe
```

**CI:** every push builds both EXEs in GitHub Actions; download them from the run's **Artifacts**.

**Publishing a release:** go to **Actions → Build AutoSwitcher → Run workflow**, enter a version (e.g. `1.0.1`), and run it. The workflow builds, tags and publishes a GitHub Release with the `.exe` and `.zip`.

<details>
<summary>Project layout</summary>

```
src/
├─ Core/            Twitch API + auth, game detection, switching rules, config, toasts, native interop
├─ Controls/        Category picker, title template editor, over-limit highlighter, recent titles
├─ Pages/           Mappings, Edit category, Stream Titles, Manual, Account, Behaviour
├─ Assets/          App icon
├─ Theme.xaml       Colours, typography and control styles
└─ MainWindow.xaml  Sidebar, Now Playing panel, page host
```
</details>

---

## Troubleshooting

<details>
<summary><b>The category doesn't switch</b></summary>

- Make sure **Auto-switch** is on (sidebar). Manual mode pauses it.
- Check that the executable on the mapping matches the game you're running (path or file name).
- In focus mode, the game must stay focused for the **focus delay** (Behaviour).
- For emulators, check the **Match window title** text appears in the window's title (use **Use current title** to copy it).
- If a game runs as administrator, AutoSwitcher may not notice it *closing* (for the fallback category). Running AutoSwitcher as administrator too fixes that.
</details>

<details>
<summary><b>No toast notifications</b></summary>

- Turn on **Toast notification on switch** on Behaviour.
- Check **Settings → System → Notifications → AutoSwitcher** is enabled.
- Windows **Do Not Disturb** (including "when playing a game") hides toasts.
- **No toasts over fullscreen games** (Behaviour) skips toasts while a fullscreen game is running.
</details>

<details>
<summary><b>"Connect with Twitch" fails or I keep getting signed out</b></summary>

- Check your internet connection and try again. Each code is valid for 30 minutes; if it expired, click **Connect with Twitch** for a new one.
- If the app hasn't run for 30+ days, Twitch requires signing in again.
</details>

<details>
<summary><b>The title was cut off</b></summary>

Twitch titles are limited to 140 characters. The red highlight in the editor shows what will be cut once names are filled in.
</details>
