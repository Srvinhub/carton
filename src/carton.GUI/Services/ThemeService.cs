using System;
using Avalonia;
using Avalonia.Styling;
using carton.Core.Models;

namespace carton.GUI.Services;

public interface IThemeService
{
    AppTheme CurrentTheme { get; }
    event EventHandler<AppTheme>? ThemeChanged;
    void Initialize(AppTheme theme);
    void ApplyTheme(AppTheme theme);
}

public sealed class ThemeService : IThemeService
{
    private static readonly Lazy<ThemeService> _instance = new(() => new ThemeService());
    public static ThemeService Instance => _instance.Value;

    private bool _initialized;

    public AppTheme CurrentTheme { get; private set; } = AppTheme.System;

    public event EventHandler<AppTheme>? ThemeChanged;

    private ThemeService()
    {
    }

    public void Initialize(AppTheme theme)
    {
        if (_initialized)
        {
            ApplyTheme(theme);
            return;
        }

        ApplyTheme(theme);
        _initialized = true;
    }

    public void ApplyTheme(AppTheme theme)
    {
        CurrentTheme = theme;

        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready");
        app.RequestedThemeVariant = theme switch
        {
            AppTheme.Light => ThemeVariant.Light,
            AppTheme.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        ThemeChanged?.Invoke(this, theme);
    }
}
