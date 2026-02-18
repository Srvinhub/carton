using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using carton.Core.Models;
using carton.GUI.Resources.Localization;

namespace carton.GUI.Services;

public interface ILocalizationService : INotifyPropertyChanged
{
    AppLanguage CurrentLanguage { get; }
    event EventHandler<AppLanguage>? LanguageChanged;
    void Initialize(AppLanguage language);
    void SetLanguage(AppLanguage language);
    string this[string key] { get; }
    string GetString(string key);
    string GetLanguageDisplayName(AppLanguage language);
}

public sealed class LocalizationService : ILocalizationService
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly Dictionary<AppLanguage, Func<ResourceDictionary>> _languageResources = new()
    {
        { AppLanguage.English, () => new Strings_en() },
        { AppLanguage.SimplifiedChinese, () => new Strings_zh_Hans() }
    };

    private IResourceProvider? _currentDictionary;
    private bool _initialized;

    public AppLanguage CurrentLanguage { get; private set; } = AppLanguage.English;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AppLanguage>? LanguageChanged;

    private LocalizationService()
    {
    }

    public void Initialize(AppLanguage language)
    {
        if (_initialized)
        {
            return;
        }

        ApplyLanguage(language);
        _initialized = true;
    }

    public void SetLanguage(AppLanguage language)
    {
        if (language == CurrentLanguage && _currentDictionary != null)
        {
            return;
        }

        ApplyLanguage(language);
        LanguageChanged?.Invoke(this, language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => GetString(key);

    public string GetString(string key)
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is string text)
        {
            return text;
        }

        return key;
    }

    public string GetLanguageDisplayName(AppLanguage language)
    {
        return language switch
        {
            AppLanguage.SimplifiedChinese => GetString("Language.SimplifiedChinese"),
            _ => GetString("Language.English")
        };
    }

    private void ApplyLanguage(AppLanguage language)
    {
        var app = Application.Current ?? throw new InvalidOperationException("Application is not ready");

        if (_currentDictionary != null)
        {
            app.Resources.MergedDictionaries.Remove(_currentDictionary);
        }

        if (!_languageResources.TryGetValue(language, out var factory))
        {
            factory = _languageResources[AppLanguage.English];
        }

        var dictionary = factory();
        app.Resources.MergedDictionaries.Add(dictionary);
        _currentDictionary = dictionary;
        CurrentLanguage = language;
    }
}
