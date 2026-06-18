namespace Fluentometer.Logic.Settings;

/// <summary>
/// Reads and writes the "launch on login" preference.
/// Implementations must be user-scoped (HKCU), never HKLM.
/// </summary>
public interface ILaunchOnLogin
{
    /// <summary>True when the app is registered to launch at login.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Registers or removes the app from the login launch list.
    /// </summary>
    void Set(bool enabled);
}
