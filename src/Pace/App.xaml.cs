using System.Windows;
using System.Windows.Threading;
using Pace.Claude;

namespace Pace;

public partial class App : Application
{
    Mutex? _single;
    Tray? _tray;
    UsageViewModel? _vm;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--export-icon", var path]) { Tray.ExportIcon(path); Shutdown(); return; }

        // One instance per user session. Launching Pace again (Start menu, shortcut) opens the
        // running instance's popover instead of starting a second tray icon.
        var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Pace.Show");
        _single = new Mutex(true, @"Local\Pace.SingleInstance", out var first);
        if (!first) { showSignal.Set(); Shutdown(); return; }

        // Keep a stray UI/background fault from killing the tray app. Refresh errors are already
        // caught in UsageViewModel and shown in the popover.
        DispatcherUnhandledException += (_, a) => { a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();

        var settings = Settings.Load();
        // Enable launch at login once on first run, then respect the user's choice.
        if (!settings.DidSetupLogin)
        {
            settings.DidSetupLogin = true;
            settings.Save();
            try { LaunchAtStartup.Set(true); } catch { /* policy-restricted registry: toggle shows the real state */ }
        }

        var service = new UsageService();
        _vm = new UsageViewModel(service.FetchAsync, () => service.Plan, settings);

        var popover = new PopoverWindow(_vm, Quit);
        ThreadPool.RegisterWaitForSingleObject(showSignal,
            (_, _) => Dispatcher.BeginInvoke(popover.ShowPopover), null, Timeout.Infinite, executeOnlyOnce: false);
        _tray = new Tray(popover.Toggle, () => _ = _vm.RefreshAsync(), Quit);
        _vm.PropertyChanged += (_, _) => UpdateTray();
        UpdateTray();
        _vm.Start();
    }

    void UpdateTray()
    {
        if (_vm is null || _tray is null) return;
        _tray.Update(_vm.SessionPercent, _vm.ShowPercentInTray, _vm.HasError, _vm.TrayText);
    }

    void Quit()
    {
        _vm?.Stop();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
