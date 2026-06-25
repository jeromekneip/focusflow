using Microsoft.Win32;

namespace FocusFlow.Services;

/// <summary>
/// Manages the HKCU Run-key entry that launches FocusFlow at Windows logon.
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FocusFlow";

    private static string? ExecutablePath => Environment.ProcessPath;

    public static void Enable()
    {
        string? exe = ExecutablePath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                                     ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key?.SetValue(ValueName, $"\"{exe}\"");
        }
        catch
        {
            // Non-fatal; the user simply won't get autostart.
        }
    }

    public static void Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal.
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reconcile the registry with the desired state.</summary>
    public static void Apply(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }
}
