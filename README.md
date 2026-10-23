# claude_mods

Small tools for Claude users.

| Tool | For | What it does |
| --- | --- | --- |
| [Claude Limits Tray](windows-tray/) | Claude desktop app on Windows | Tray icon and an overlay above the message box with your 5-hour and weekly limits |
| [usage-limits](#usage-limits) | Claude Code | A band above the prompt with the same limits |

## Claude Limits Tray (Windows)

Download `ClaudeLimitsTray.exe` from [Releases](https://github.com/WiZaRd-228/claude_mods/releases). Setup and details: [windows-tray/README.md](windows-tray/README.md).

## usage-limits

See your Claude subscription limits without opening Settings → Usage. A band above the prompt shows the 5-hour and weekly windows live: a progress bar, the percent used and a countdown to the reset.

```
5h     ██████░░░░░░░░░░░░    34%  · resets in 2h 13m
7d     ████░░░░░░░░░░░░░░    24%  · resets in 2d 15h
```

- Green below 70%, yellow from 70%, red from 90%
- A toast once when a window crosses 80% and 95%
- Countdown refreshes every minute; percentages update after every model reply
- `Compact` and `Hide` buttons on the band

### Install

In the Claude desktop app (Code tab): click **+** next to the prompt box → **Plugins** → **Add marketplace** → enter `WiZaRd-228/claude_mods` → **Add plugin** → `usage-limits`.

At the prompt of a terminal session:

```
/plugin install usage-limits --marketplace WiZaRd-228/claude_mods
```

Answer `y` to add the marketplace, then pick a scope (user scope loads it in every session).

From a shell:

```
claude plugin install usage-limits --marketplace WiZaRd-228/claude_mods
```

### Commands

| Command | Does |
| --- | --- |
| `/limits` | Prints both windows as text |
| `/limits full` | Full band with bars |
| `/limits compact` | One line: `5h 34% · 7d 24%` |
| `/limits hide` | Hides the band (status line too) |
| `/limits pane` | Opens the limits in a side pane, where the client supports panes |

### Notes

- Data comes from the official rate-limit figures Claude Code receives with each reply, so the first numbers appear after the first reply in a session.
- Only Claude subscriptions (Pro/Max/Team) report these windows; API-key sessions show nothing.
- Works where Claude Code draws mods: the terminal and the desktop app's Code tab. Regular chats in the Claude app do not show mod bands.
- Nothing leaves your machine: no network calls, no analytics.

---

## usage-limits (RU)

Показывает лимиты подписки Claude (5-часовое и недельное окно) прямо над полем ввода: прогресс-бар, процент и время до сброса. Установка в десктопе: вкладка Code → **+** → **Plugins** → **Add marketplace** → `WiZaRd-228/claude_mods` → **Add plugin**. Команда `/limits` выводит цифры текстом, `/limits compact | hide | full` переключает вид.

## License

MIT
