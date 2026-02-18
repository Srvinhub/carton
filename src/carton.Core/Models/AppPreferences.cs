namespace carton.Core.Models;

public class AppPreferences
{
    public bool StartAtLogin { get; set; }
    public bool AutoStartOnLaunch { get; set; }
    public string Theme { get; set; } = "System";
    public AppLanguage Language { get; set; } = AppLanguageHelper.GetSystemDefaultLanguage();
}

public enum AppLanguage
{
    English,
    SimplifiedChinese
}
