# AutoSwitcher

Sets your Twitch **category** and **stream title** automatically from the game you're playing. No Streamer.bot needed.

- Map one Twitch category to **several executables** (Ship of Harkinian, Project64, a randomizer…).
- **Title template** with `%gameName%`, `%fullGameName%`, `%customName%`.
- Two detection modes: **game launches** or **game window is focused** (with a configurable delay so clicking back and forth doesn't spam Twitch).
- **Manual mode** to set category/title yourself (pauses auto-switch).
- One-click Twitch login (OAuth **Device Code** flow, Client ID built in); stays signed in.
- Tray icon, start with Windows, optional toast on every switch.

## Title variables

| Variable | Example | Where it comes from |
|---|---|---|
| `%gameName%` | The Legend of Zelda: Ocarina of Time | The Twitch category |
| `%fullGameName%` | Ship of Harkinian | Read from the exe's product info (falls back to the file name); editable |
| `%customName%` | Zelda OoT SoH | Your own short name per exe; falls back to `%fullGameName%` |

**Update rule:** on every switch the app builds the target category and filled-in title, compares them with what's live on Twitch, and only sends what changed. Switching between two exes of the same category sends nothing, unless the title's text differs (because it uses `%fullGameName%` or `%customName%`).

## First-time setup

Open AutoSwitcher → **Account** → **Connect with Twitch**, then approve the code on twitch.tv/activate. That's it. The app's Twitch Client ID is built in (public client, no secret), so there's nothing to register or paste.

The only permission requested is `channel:manage:broadcast` (edit title and category).

## Building the EXE

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then double-click / run:

```
build.cmd          →  dist\AutoSwitcher.exe              (small; needs the .NET 10 Desktop Runtime)
build.cmd full     →  dist-standalone\AutoSwitcher.exe   (bigger; runs on any Windows 10/11 x64 PC)
```

Or push this folder to a GitHub repo: the included workflow (`.github/workflows/build.yml`) builds both EXEs on every push. Download them from the run's **Artifacts**.

## Performance notes

- Focus mode uses a Windows event hook (`SetWinEventHook`, foreground changes), so it does nothing until you switch windows. There's no polling loop.
- Launch mode diffs the process ID list every 2 s and only looks up *new* processes.
- Game exit is detected through the process handle, not polling.
- Hidden in the tray, there's no rendering, and the working set is trimmed when the window closes.
- Box art is downloaded once, decoded at thumbnail size, and cached in `%LOCALAPPDATA%\AutoSwitcher\art`.

## Where things are stored

- `%APPDATA%\AutoSwitcher\config.json`: mappings, template, settings.
- `%APPDATA%\AutoSwitcher\tokens.dat`: Twitch tokens, encrypted with Windows DPAPI (only your Windows user can read them).

Public-client refresh tokens expire after 30 days unused. The app refreshes on every launch and every few hours while running, so you only re-login if it hasn't run for a month.
