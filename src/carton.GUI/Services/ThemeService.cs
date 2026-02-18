using System;
using Avalonia;
using Avalonia.Styling;

namespace carton.GUI.Services;

public interface IThemeService
{
    string CurrentTheme { get; }
    event EventHandler<string>? ThemeChanged;
    void Initialize(string theme);
    void ApplyTheme(string theme);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    private bool _initialized;

    public string CurrentTheme { get; private set; } = "System";

    public event EventHandler<string>? ThemeChanged;

    private ThemeService()
    {
    }

    public void Initialize(string theme)
    {
        if (_initialized)
        {
            ApplyTheme(theme);
            return;
        }

        ApplyTheme(theme);
        _initialized = true;
    }

    public void ApplyTheme(string theme)
    {
        var normalized = NormalizeTheme(theme);
        CurrentTheme = normalized;

        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready");
        app.RequestedThemeVariant = normalized switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        ThemeChanged?.Invoke(this, normalized);
    }

    private static string NormalizeTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme))
        {
            return "System";
        }

        return theme.Trim() switch
        {
            var value when value.Equals("Dark", StringComparison.OrdinalIgnoreCase) => "Dark",
            var value when value.Equals("Light", StringComparison.OrdinalIgnoreCase) => "Light",
            _ => "System"
        };
    }
}
