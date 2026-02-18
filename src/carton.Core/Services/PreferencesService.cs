using System.Threading;
using carton.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace carton.Core.Services;

public interface IPreferencesService
{
    Task<AppPreferences> LoadAsync();
    Task SaveAsync(AppPreferences preferences);
}

public class PreferencesService : IPreferencesService
{
    private readonly string _preferencesPath;
    private readonly JsonSerializerSettings _jsonSettings;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public PreferencesService(string baseDirectory)
    {
        Directory.CreateDirectory(baseDirectory);
        _preferencesPath = Path.Combine(baseDirectory, "preferences.json");
        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };
        _jsonSettings.Converters.Add(new StringEnumConverter { AllowIntegerValues = false });
    }

    public async Task<AppPreferences> LoadAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            if (!File.Exists(_preferencesPath))
            {
                return new AppPreferences();
            }

            try
            {
                var json = await File.ReadAllTextAsync(_preferencesPath);
                return JsonConvert.DeserializeObject<AppPreferences>(json, _jsonSettings) ?? new AppPreferences();
            }
            catch (JsonException)
            {
                return new AppPreferences();
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveAsync(AppPreferences preferences)
    {
        await _syncLock.WaitAsync();
        try
        {
            var json = JsonConvert.SerializeObject(preferences, _jsonSettings);
            await File.WriteAllTextAsync(_preferencesPath, json);
        }
        finally
        {
            _syncLock.Release();
        }
    }
}
