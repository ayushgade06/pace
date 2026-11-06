using System.Text.Json;
using Microsoft.Win32;

namespace Pace;

/// The only local state Pace keeps. No tokens, no usage cache.
public sealed class Settings
{
    public bool ShowPercentInTray { get; set; } = true;
    public bool DidSetupLogin { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pace", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); } // missing or corrupt: defaults
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// Launch at login via the per-user Run key.
/// Users can also toggle it in Task Manager → Startup apps, so always read the live state.
public static class LaunchAtStartup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "Pace";
    static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(Name) is not string v || !string.Equals(v, Command, StringComparison.OrdinalIgnoreCase)) return false;
            // Task Manager disables entries via StartupApproved without touching the Run key.
            using var a = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return a?.GetValue(Name) is not byte[] { Length: > 0 } b || (b[0] & 1) == 0;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            k.SetValue(Name, Command);
            // Clear a "disabled" flag left by Task Manager so enabling here actually takes effect.
            using var a = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", writable: true);
            a?.DeleteValue(Name, throwOnMissingValue: false);
        }
        else k.DeleteValue(Name, throwOnMissingValue: false);
    }
}
