using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using Pace.Claude;

namespace Pace;

public sealed record LimitRowView(string Title, double Percent, string PercentText, string ResetText, string Severity);

/// Usage state, refresh and schedule. Formatting helpers are static for tests.
public sealed class UsageViewModel : INotifyPropertyChanged
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    readonly Func<CancellationToken, Task<UsageSnapshot>> _fetch;
    readonly Func<string?> _plan;
    readonly Func<DateTimeOffset> _now;
    readonly CancellationTokenSource _shutdown = new();
    readonly Settings _settings;
    DispatcherTimer? _refreshTimer, _clockTimer;

    UsageSnapshot? _snapshot;

    public UsageViewModel(Func<CancellationToken, Task<UsageSnapshot>> fetch, Func<string?> plan, Settings settings, Func<DateTimeOffset>? now = null)
    {
        _fetch = fetch; _plan = plan; _settings = settings;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// Tiny UI: refresh every binding at once instead of per-property plumbing.
    void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    // ---- state ----
    public string? Plan { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool Loading { get; private set; }
    public string? LoginError { get; private set; }
    public bool HasError => Error is not null;
    public bool HasLoginError => LoginError is not null;
    public bool HasPlan => Plan is not null;
    public bool CanRefresh => !Loading;

    public IReadOnlyList<LimitRowView> Limits =>
        _snapshot?.Limits.Select(l => new LimitRowView(l.Title, Math.Min(l.Percent, 100), $"{(int)l.Percent}%",
            ResetText(l.ResetsAt, _now()), Severity(l.Percent))).ToList() ?? [];

    public IReadOnlyList<BreakdownRow> Breakdown => _snapshot?.Breakdown ?? [];
    public bool HasBreakdown => Breakdown.Count > 0;

    public string PlanText => Plan is null ? "" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Plan);
    public string UpdatedText => LastUpdated is { } t ? $"Updated {t.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)}" : "";

    /// Tray tooltip: session and weekly %, ⚠ when the numbers are stale.
    public string TrayText
    {
        get
        {
            if (_snapshot is null || _snapshot.Limits.Count == 0) return Error is null ? "Pace" : $"Pace ⚠ {Error}";
            string Pct(string k) => _snapshot.Limits.FirstOrDefault(l => l.Kind == k) is { } l ? $"{(int)l.Percent}%" : "–";
            var t = $"Pace · session {Pct("session")} · week {Pct("weekly_all")}";
            return Error is null ? t : t + " ⚠";
        }
    }

    public double? SessionPercent => _snapshot?.Limits.FirstOrDefault(l => l.Kind == "session")?.Percent;

    public bool LaunchAtLogin
    {
        get => LaunchAtStartup.IsEnabled;
        set
        {
            try { LaunchAtStartup.Set(value); LoginError = null; }
            catch (Exception e) { LoginError = e.Message; }
            Changed();
        }
    }

    public bool ShowPercentInTray
    {
        get => _settings.ShowPercentInTray;
        set { _settings.ShowPercentInTray = value; _settings.Save(); Changed(); }
    }

    // ---- lifecycle ----

    /// Starts the 5-minute schedule, a 30 s tick that keeps countdowns live, and the first refresh.
    public void Start()
    {
        _refreshTimer = new DispatcherTimer { Interval = Interval };
        _refreshTimer.Tick += (_, _) => _ = RefreshAsync();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => Changed();
        _clockTimer.Start();
        _ = RefreshAsync();
    }

    public void Stop()
    {
        _refreshTimer?.Stop(); _clockTimer?.Stop();
        _shutdown.Cancel();
    }

    /// Re-read state that can change outside Pace (Task Manager startup toggle, clock).
    public void Touch() => Changed();

    public async Task RefreshAsync()
    {
        if (Loading || _shutdown.IsCancellationRequested) return; // timer + button can overlap
        Loading = true;
        // Any refresh (manual or scheduled) restarts the 5-minute interval.
        _refreshTimer?.Stop(); _refreshTimer?.Start();
        Changed();
        try
        {
            try
            {
                var snap = await _fetch(_shutdown.Token);
                _snapshot = snap;
                LastUpdated = _now();
                Error = null;
            }
            catch (UsageException e) { Error = e.Message; }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
            catch (Exception e) { Error = $"Refresh failed: {e.Message}"; }
            finally { Plan = _plan() ?? Plan; }
        }
        finally
        {
            Loading = false;
            Changed();
        }
    }

    // ---- formatting ----

    /// "Resetting…", "Resets in 2h 5m", "Resets in 5m", or "Resets Tue 9:00 PM".
    public static string ResetText(DateTimeOffset? d, DateTimeOffset now)
    {
        if (d is null) return "";
        var secs = (long)(d.Value - now).TotalSeconds;
        if (secs <= 0) return "Resetting…";
        if (secs < 86400)
        {
            long h = secs / 3600, m = secs % 3600 / 60;
            return h > 0 ? $"Resets in {h}h {m}m" : $"Resets in {m}m";
        }
        return "Resets " + d.Value.ToLocalTime().ToString("ddd h:mm tt", CultureInfo.CurrentCulture);
    }

    /// Bar color: red ≥ 90, orange ≥ 70, accent otherwise.
    public static string Severity(double pct) => pct >= 90 ? "Critical" : pct >= 70 ? "Warning" : "Normal";

}
