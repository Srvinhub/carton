using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using carton.Core.Models;
using carton.Core.Serialization;

namespace carton.Core.Services;

public interface IPreferencesService
{
    AppPreferences Load();
    void Save(AppPreferences preferences);
}

public class PreferencesService : IPreferencesService
{
    private readonly string _preferencesPath;
    private readonly object _syncLock = new();
    private AppPreferences? _cachedPreferences;

    public PreferencesService(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        _preferencesPath = Path.Combine(baseDirectory, "preferences.json");
        EnsurePreferencesFileExists();
    }

    public AppPreferences Load()
    {
        lock (_syncLock)
        {
            if (_cachedPreferences != null)
            {
                return _cachedPreferences;
            }

            _cachedPreferences = ReadPreferencesFromDisk();
            return _cachedPreferences;
        }
    }

    public void Save(AppPreferences preferences)
    {
        if (preferences == null)
        {
            throw new ArgumentNullException(nameof(preferences));
        }

        lock (_syncLock)
        {
            _cachedPreferences = preferences;
            PersistPreferences(_cachedPreferences);
        }
    }

    private void EnsurePreferencesFileExists()
    {
        if (File.Exists(_preferencesPath))
        {
            return;
        }

        var defaults = CreateDefaultPreferences();
        PersistPreferences(defaults);
    }

    private static AppPreferences CreateDefaultPreferences()
    {
        return new AppPreferences
        {
            Language = AppLanguageHelper.GetSystemDefaultLanguage()
        };
    }

    private AppPreferences ReadPreferencesFromDisk()
    {
        try
        {
            var json = File.ReadAllText(_preferencesPath);
            return JsonSerializer.Deserialize(
                       json,
                       CartonCoreJsonContext.Default.AppPreferences) ?? CreateAndPersistDefaults();
        }
        catch (JsonException)
        {
            return CreateAndPersistDefaults();
        }
    }

    private AppPreferences CreateAndPersistDefaults()
    {
        var defaults = CreateDefaultPreferences();
        PersistPreferences(defaults);
        return defaults;
    }

    private void PersistPreferences(AppPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_preferencesPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(_preferencesPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        JsonSerializer.Serialize(writer, preferences, CartonCoreJsonContext.Default.AppPreferences);
        writer.Flush();
    }
}
