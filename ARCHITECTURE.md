# Architecture

Pace is one WPF process with no services, no database and no DI container. There are about eight source files; each one has a single job.

```
src/Pace/
  App.xaml(.cs)          Startup/lifecycle: single instance, first-run setup, wiring, shutdown
  Tray.cs                Notification-area icon, context menu, icon rendering (System.Drawing)
  PopoverWindow.xaml(.cs) The flyout UI, placement next to the tray, theme/accent
  UsageViewModel.cs      State + refresh scheduling + formatting (the only ViewModel)
  Settings.cs            Preferences JSON + launch-at-startup (HKCU Run key)
  Claude/UsageService.cs Credential discovery + HTTP client for the usage endpoint
  Claude/UsageModels.cs  Response models + lenient JSON parsing + limit titles
```

## Data flow

```
DispatcherTimer (5 min) ─┐
Refresh button ──────────┼─► UsageViewModel.RefreshAsync ──► UsageService.FetchAsync ──► api.anthropic.com
Tray menu "Refresh" ─────┘                                        ▲ ClaudeCredentials.Read (.credentials.json)
                         PropertyChanged ─► PopoverWindow bindings, Tray.Update (icon + tooltip)
```

## Key decisions

**Refresh.** `RefreshAsync` is the single entry point. A `Loading` flag (UI thread only) drops overlapping calls, so the timer and the button can never send two requests at once. Each refresh restarts the 5-minute `DispatcherTimer`, so a manual refresh resets the interval. On failure the previous snapshot and its "Updated" time stay on screen, and an error line and a ⚠ in the tooltip mark them as stale. A second 30 s timer re-renders the countdowns between fetches. Shutdown cancels the in-flight request.

**Endpoint isolation.** Everything about the undocumented API (URL, headers, status codes, JSON shape) lives in `Claude/`. The ViewModel sees only `UsageSnapshot` and `UsageException` (a user-facing message). If the API changes, only the parser and service change.

**Lenient parsing.** The response is read with `JsonDocument`, not strict deserialization. Unknown fields are ignored. A limit with no `kind` or `percent` is dropped rather than shown as 0%. Unknown limit kinds still render, using a title-cased name. `weekly_scoped` limits are labelled with `scope.model.display_name`.

**Credentials.** On Windows, Claude Code stores its OAuth login as plain JSON in `.credentials.json`; there is no Credential Manager entry. Pace opens it read-only with `FileShare.ReadWrite | Delete`, so it never blocks Claude Code while that file is being rewritten. Pace persists no secrets, so DPAPI or Credential Manager has nothing to protect. Expired tokens are not refreshed by Pace (see README).

**Tray.** WPF has no tray API. `System.Windows.Forms.NotifyIcon` ships with the Windows Desktop runtime, so it costs no NuGet dependency. Icons are drawn at the system small-icon size. The % mode is a solid severity-colored badge with the number drawn as a path, scaled to fill it, so it stays legible at 16 px on both light and dark taskbars. The resulting HICON is destroyed after cloning to avoid handle leaks.

**Popover.** A borderless, transparent, topmost window with a rounded card and drop shadow. The cursor position is captured once when it opens. When opened from the tray it is placed next to that point and flush with the taskbar edge (bottom, top, left or right). When opened by relaunching Pace it goes to the tray corner. Re-placing after a content change (an error line appearing, say) reuses the captured point, so the popover never jumps sideways. It hides on deactivation or Esc. Clicks on the tray icon within 300 ms of a hide are ignored, so clicking the icon while the popover is open closes it instead of reopening it.

**Errors.** Refresh exceptions are caught in the ViewModel and shown in the popover. `DispatcherUnhandledException` and `UnobservedTaskException` are handled so that a stray fault never takes the tray icon down.

## Dependencies

Runtime: none beyond .NET 10 (WPF + WinForms for `NotifyIcon`/`System.Drawing`). Tests: xUnit and the test SDK.
