# Claude Limits Tray (Windows)

Your Claude subscription limits, always in sight, while you use the regular Claude desktop app.

- **Tray icon** with the 5-hour usage percent, colored green / yellow (70%+) / red (90%+)
- **Overlay** that sticks to the bottom of the Claude window, right above the message box, and shows the 5-hour and weekly windows with progress bars and reset countdowns
- **Notifications** at 80% and 95%, and when a limit resets
- Shows only while Claude is the active window (configurable: always / when Claude is active / hidden)
- Drag the overlay to move it; the position is remembered relative to the Claude window

## Install

1. Download `ClaudeLimitsTray.exe` from [Releases](https://github.com/WiZaRd-228/claude_mods/releases).
2. Run it. Windows SmartScreen may warn about an unsigned app: **More info → Run anyway**.
3. Right-click the tray icon → **Start with Windows** if you want it to start automatically.

Requires Windows 10/11 x64 and the [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) (already installed on Windows 11).

## First run: connecting your session

The app needs your claude.ai session to read your limits. Two ways:

- **Automatic (recommended):** quit Claude Desktop once (tray icon → Quit). The app imports the session from Claude Desktop's local data, then keeps its own copy, so you can reopen Claude right away. While Claude Desktop runs it locks its data, so the import only works when it is closed.
- **Sign in:** right-click the tray icon → **Sign in to claude.ai…** and log in with your email. Google sign-in is usually blocked inside embedded browsers; use the email code instead.

## Menu

| Item | Does |
| --- | --- |
| Refresh now | Fetches limits immediately (also: left-click the icon) |
| Overlay | When Claude is active / Always show / Hide, Reset position |
| Start with Windows | Adds or removes the app from autostart |
| Import session from Claude Desktop | Re-imports the session (Claude Desktop must be closed) |
| Sign in to claude.ai… | Logs in inside the app's own browser profile |
| Open log folder | Opens the folder with `log.txt` and `settings.json` |

## How it works

- The app reads the same usage figures the Claude **Settings → Usage** page shows, from claude.ai, once a minute.
- Requests go through a hidden WebView2 (Microsoft Edge engine) with its own profile, so claude.ai sees a normal browser.
- The session import decrypts the `sessionKey` cookie of Claude Desktop with your Windows account's DPAPI key, in memory, and stores it only in the app's own WebView2 profile.
- Data stays on your machine. The app talks only to `claude.ai`. No analytics.

Files: `%LOCALAPPDATA%\ClaudeLimitsTray\` (`settings.json`, `log.txt`, `WebView2\` profile). Delete the folder to reset everything.

## Disclaimer

- This is an unofficial tool, not made or endorsed by Anthropic.
- It uses an undocumented claude.ai endpoint that can change or disappear at any time; the app will then show "Unknown response format" until it is updated.
- Your session cookie grants full access to your Claude account. Only run builds from this repository, and do not share your `%LOCALAPPDATA%\ClaudeLimitsTray` folder.

## Build

```
dotnet publish windows-tray/src/ClaudeLimitsTray/ClaudeLimitsTray.csproj -c Release -o out
```

Needs the .NET 8 SDK. CI builds every push and attaches the exe to `tray-v*` tag releases.

---

## По-русски

Утилита для Windows: показывает лимиты подписки Claude (окна 5 часов и 7 дней) в трее и в плавающем окошке над полем ввода Claude Desktop, пока Claude активен.

1. Скачай `ClaudeLimitsTray.exe` из [Releases](https://github.com/WiZaRd-228/claude_mods/releases) и запусти. Если SmartScreen предупредит: **Подробнее → Выполнить в любом случае**.
2. Один раз полностью закрой Claude Desktop (иконка в трее → Quit): утилита импортирует сессию и дальше хранит её у себя. Или правый клик по иконке → **Sign in to claude.ai…** и вход по email.
3. Окошко перетаскивается мышью, режим показа и автозапуск — в меню иконки.

Неофициальная утилита: использует недокументированный эндпоинт claude.ai, который может измениться.
