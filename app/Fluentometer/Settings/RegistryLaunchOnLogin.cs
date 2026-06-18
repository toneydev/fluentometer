using System;
using Fluentometer.Logic.Settings;
using Microsoft.Win32;

namespace Fluentometer.Settings;

/// <summary>
/// Manages the "launch on login" Run value in
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Always user-scoped (HKCU) — never HKLM.
/// </summary>
public sealed class RegistryLaunchOnLogin : ILaunchOnLogin
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string ValueName = "Fluentometer";

    /// <inheritdoc/>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    /// <inheritdoc/>
    public void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                $"Cannot open registry key HKCU\\{RunKeyPath} for writing.");

        if (enabled)
        {
            // Use the full path to the current executable so Windows can find it.
            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Cannot determine current executable path.");

            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
