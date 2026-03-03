namespace carton.Core.Models;

public class AppPreferences
{
    public bool StartAtLogin { get; set; }
    public bool AutoStartOnLaunch { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AppLanguage Language { get; set; } = AppLanguage.English;
    public AppUpdateChannel UpdateChannel { get; set; } = AppUpdateChannel.Release;
    public bool AutoCheckAppUpdates { get; set; } = true;
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum AppLanguage
{
    English,
    SimplifiedChinese
}

public enum AppUpdateChannel
{
    Release,
    Beta
}
