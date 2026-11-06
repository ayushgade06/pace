# Pace

A small Windows tray app that shows how much of your Claude plan you've used: the 5-hour session limit, the weekly limits, and when each resets.

<img src="docs/popover-dark.png" width="300" alt="Pace popover: session and weekly limits, surface breakdown">

Click the tray icon to see your usage, then click away to close it.

## Features

- **Current session (5h)** and **weekly** limits (all models, plus any per-model limits your plan has, such as Opus, Sonnet or another model), each with a progress bar and reset countdown.
- **This week by surface**: how your weekly usage splits across Claude Code, chats and other surfaces.
- Plan badge (Pro / Max / …) and last successful update time.
- Refreshes every 5 minutes. The refresh button fetches immediately and restarts the 5-minute timer. Overlapping refreshes are ignored.
- Tray icon shows the session % as a colored badge (amber at 70%, red at 90%, gray when stale) or a gauge. The tooltip shows session and week %.
- Launch at startup (enabled on first run; toggle in the popover or in Task Manager → Startup apps).
- Follows Windows dark/light app theme and accent color.

## Requirements

- Windows 10 1809+ or Windows 11
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or build self-contained)
- [Claude Code](https://claude.com/claude-code) logged in with a Claude subscription (`claude` → `/login`)

## Build and run

```powershell
git clone <this repo> pace; cd pace
dotnet run --project src/Pace                 # debug run
dotnet test                                   # unit tests
dotnet publish src/Pace -c Release -o publish # Release build in .\publish\Pace.exe
```

Pace runs in the notification area. On Windows 11 new tray icons start under the **^** overflow; drag Pace onto the taskbar to keep it visible. Launching `Pace.exe` again opens the popover of the running instance.

## How authentication works

Pace reuses the login Claude Code already has. It does not have its own sign-in.

1. It reads `%USERPROFILE%\.claude\.credentials.json` (or `%CLAUDE_CONFIG_DIR%\.credentials.json`), read-only, and takes `claudeAiOauth.accessToken`, `expiresAt` and `subscriptionType`. The file is re-read on every refresh, so tokens Claude Code renews are picked up automatically.
2. It calls `GET https://api.anthropic.com/api/oauth/usage` with `Authorization: Bearer …` and `anthropic-beta: oauth-2025-04-20`. This is the same usage endpoint Claude Code's `/usage` uses. It is not the Messages API, so no model runs and no tokens are spent.
3. If the token has expired, Pace doesn't call the API; it asks you to open Claude Code, which refreshes the token. Pace deliberately never refreshes the token itself: refreshing rotates the refresh token and would sign Claude Code out.

## Privacy and local data

- The token is sent only to `api.anthropic.com` over HTTPS. Redirects are refused, so it can't be forwarded elsewhere. No cookies, no HTTP cache.
- The token is never logged, displayed or written anywhere by Pace.
- Pace stores two preferences in `%LOCALAPPDATA%\Pace\settings.json` and one `Pace` value under `HKCU\…\CurrentVersion\Run` for launch at startup. No usage data is cached on disk.
- No analytics, no other network calls.

## Known limitations

- The usage endpoint is undocumented and may change or break without notice. All endpoint-specific code is in `src/Pace/Claude/`.
- An expired token is only renewed when Claude Code itself runs.
- Light theme, multi-monitor setups with mixed DPI, and taskbars on the top/left/right edges are implemented but were not tested by hand.
- The tray can't show text, so the session % is drawn into the 16 px icon; "100" is tighter than two digits.

Not affiliated with Anthropic. MIT License.
