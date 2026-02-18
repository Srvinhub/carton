using System.IO;
using System.Threading;
using carton.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace carton.Core.Services;

public interface IProfileManager
{
    Task<List<Profile>> ListAsync();
    Task<Profile?> GetAsync(int id);
    Task<Profile> CreateAsync(Profile profile, string? configContent = null);
    Task UpdateAsync(Profile profile);
    Task DeleteAsync(int id);
    Task<int> GetSelectedProfileIdAsync();
    Task SetSelectedProfileIdAsync(int id);
    Task<ProfileRuntimeOptions> GetRuntimeOptionsAsync(int profileId);
    Task SaveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions options);
}

public class ProfileManager : IProfileManager
{
    private readonly string _dataPath;
    private readonly IConfigManager _configManager;
    private readonly JsonSerializerSettings _jsonSettings;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public ProfileManager(string baseDirectory, IConfigManager configManager)
    {
        Directory.CreateDirectory(baseDirectory);
        _dataPath = Path.Combine(baseDirectory, "sing-box-data.json");
        _configManager = configManager;
        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };
    }

    public async Task<List<Profile>> ListAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var normalized = NormalizeProfileIds(data.Profiles);
            var selectedNormalized = EnsureSelectedProfileExists(data);

            if (normalized || selectedNormalized)
            {
                await SaveDataUnlockedAsync(data);
            }

            return data.Profiles.Select(CloneProfile).ToList();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<Profile?> GetAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == id);
            return profile != null ? CloneProfile(profile) : null;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<Profile> CreateAsync(Profile profile, string? configContent = null)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var maxId = data.Profiles.Count > 0 ? data.Profiles.Max(p => p.Id) : 0;
            profile.Id = maxId + 1;
            profile.CreatedAt = DateTime.Now;
            profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
            data.Profiles.Add(profile);
            await SaveDataUnlockedAsync(data);

            if (!string.IsNullOrEmpty(configContent))
            {
                await _configManager.SaveConfigAsync(profile.Id, configContent, profile.Type);
            }

            return CloneProfile(profile);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task UpdateAsync(Profile profile)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var index = data.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
            {
                profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
                data.Profiles[index] = profile;
                await SaveDataUnlockedAsync(data);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task DeleteAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var removed = data.Profiles.RemoveAll(p => p.Id == id) > 0;
            if (removed && data.SelectedProfileId == id)
            {
                data.SelectedProfileId = 0;
            }

            if (removed)
            {
                await SaveDataUnlockedAsync(data);
            }
        }
        finally
        {
            _syncLock.Release();
        }

        await _configManager.DeleteConfigAsync(id);
    }

    public async Task<int> GetSelectedProfileIdAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var changed = EnsureSelectedProfileExists(data);
            if (changed)
            {
                await SaveDataUnlockedAsync(data);
            }
            return data.SelectedProfileId;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SetSelectedProfileIdAsync(int id)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            data.SelectedProfileId = id;
            await SaveDataUnlockedAsync(data);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<ProfileRuntimeOptions> GetRuntimeOptionsAsync(int profileId)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == profileId);
            return profile?.RuntimeOptions != null
                ? CloneRuntimeOptions(profile.RuntimeOptions)
                : new ProfileRuntimeOptions();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SaveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions options)
    {
        await _syncLock.WaitAsync();
        try
        {
            var data = await LoadOrCreateDataUnlockedAsync();
            var profile = data.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (profile == null)
            {
                return;
            }

            var normalized = CloneRuntimeOptions(options);
            normalized.Initialized = true;
            normalized.InboundPort = NormalizePort(normalized.InboundPort);
            profile.RuntimeOptions = normalized;
            await SaveDataUnlockedAsync(data);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<SingBoxData> LoadOrCreateDataUnlockedAsync()
    {
        if (File.Exists(_dataPath))
        {
            var json = await File.ReadAllTextAsync(_dataPath);
            var data = JsonConvert.DeserializeObject<SingBoxData>(json, _jsonSettings) ?? new SingBoxData();
            await EnsureConfigLayoutAsync(data.Profiles);
            await EnsureRuntimeOptionsAsync(data.Profiles);
            return data;
        }

        var dataToCreate = new SingBoxData();
        await EnsureConfigLayoutAsync(dataToCreate.Profiles);
        await EnsureRuntimeOptionsAsync(dataToCreate.Profiles);
        await SaveDataUnlockedAsync(dataToCreate);
        return dataToCreate;
    }

    private async Task SaveDataUnlockedAsync(SingBoxData data)
    {
        var json = JsonConvert.SerializeObject(data, _jsonSettings);
        await File.WriteAllTextAsync(_dataPath, json);
    }

    private static bool NormalizeProfileIds(List<Profile> profiles)
    {
        var changed = false;
        var usedIds = new HashSet<int>();
        var nextId = 1;

        foreach (var profile in profiles)
        {
            if (profile.Id > 0 && usedIds.Add(profile.Id))
            {
                nextId = Math.Max(nextId, profile.Id + 1);
                continue;
            }

            while (usedIds.Contains(nextId))
            {
                nextId++;
            }

            profile.Id = nextId;
            usedIds.Add(nextId);
            nextId++;
            changed = true;
        }

        return changed;
    }

    private static bool EnsureSelectedProfileExists(SingBoxData data)
    {
        if (data.SelectedProfileId == 0)
        {
            return false;
        }

        if (data.Profiles.Any(p => p.Id == data.SelectedProfileId))
        {
            return false;
        }

        data.SelectedProfileId = 0;
        return true;
    }

    private async Task EnsureRuntimeOptionsAsync(IEnumerable<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.RuntimeOptions = await ResolveRuntimeOptionsAsync(profile.Id, profile.RuntimeOptions);
        }
    }

    private async Task EnsureConfigLayoutAsync(IEnumerable<Profile> profiles)
    {
        foreach (var profile in profiles)
        {
            await _configManager.GetConfigPathAsync(profile.Id, profile.Type);
        }
    }

    private async Task<ProfileRuntimeOptions> ResolveRuntimeOptionsAsync(int profileId, ProfileRuntimeOptions? options)
    {
        if (options != null && options.Initialized)
        {
            options.InboundPort = NormalizePort(options.InboundPort);
            return options;
        }

        var resolved = await TryLoadRuntimeOptionsFromConfigAsync(profileId) ?? options ?? new ProfileRuntimeOptions();
        resolved.Initialized = true;
        resolved.InboundPort = NormalizePort(resolved.InboundPort);
        return resolved;
    }

    private async Task<ProfileRuntimeOptions?> TryLoadRuntimeOptionsFromConfigAsync(int profileId)
    {
        try
        {
            var configContent = await _configManager.LoadConfigAsync(profileId);
            if (string.IsNullOrWhiteSpace(configContent))
            {
                return null;
            }

            var root = JObject.Parse(configContent);
            if (root["inbounds"] is not JArray inboundsElement || inboundsElement.Count == 0)
            {
                return null;
            }

            var hasTun = false;
            JObject? firstInbound = null;
            JObject? primaryInbound = null;

            foreach (var inbound in inboundsElement)
            {
                if (inbound is not JObject inboundObject)
                {
                    continue;
                }

                if (firstInbound == null)
                {
                    firstInbound = inboundObject;
                }

                var type = TryReadString(inboundObject, "type");
                if (string.Equals(type, "tun", StringComparison.OrdinalIgnoreCase))
                {
                    hasTun = true;
                    continue;
                }

                if (primaryInbound == null)
                {
                    primaryInbound = inboundObject;
                }
            }

            if (primaryInbound == null)
            {
                if (firstInbound == null)
                {
                    return null;
                }

                primaryInbound = firstInbound;
            }

            var port = TryReadInt(primaryInbound, "listen_port") ?? 2028;
            var listen = TryReadString(primaryInbound, "listen");
            var setSystemProxy = TryReadBool(primaryInbound, "set_system_proxy") ?? false;

            return new ProfileRuntimeOptions
            {
                InboundPort = NormalizePort(port),
                AllowLanConnections = string.Equals(listen, "0.0.0.0", StringComparison.OrdinalIgnoreCase),
                EnableSystemProxy = setSystemProxy,
                EnableTunInbound = hasTun,
                Initialized = true
            };
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static int? TryReadInt(JObject element, string propertyName)
    {
        if (element.TryGetValue(propertyName, StringComparison.Ordinal, out var property) &&
            property.Type is JTokenType.Integer or JTokenType.Float &&
            int.TryParse(property.ToString(), out var value))
        {
            return value;
        }

        return null;
    }

    private static string? TryReadString(JObject element, string propertyName)
    {
        if (element.TryGetValue(propertyName, StringComparison.Ordinal, out var property) &&
            property.Type == JTokenType.String)
        {
            return property.ToString();
        }

        return null;
    }

    private static bool? TryReadBool(JObject element, string propertyName)
    {
        if (element.TryGetValue(propertyName, StringComparison.Ordinal, out var property) &&
            property.Type == JTokenType.Boolean)
        {
            return property.Value<bool>();
        }

        return null;
    }

    private static Profile CloneProfile(Profile profile)
    {
        return new Profile
        {
            Id = profile.Id,
            Name = profile.Name,
            Type = profile.Type,
            Path = profile.Path,
            Url = profile.Url,
            LastUpdated = profile.LastUpdated,
            UpdateInterval = profile.UpdateInterval,
            AutoUpdate = profile.AutoUpdate,
            CreatedAt = profile.CreatedAt,
            RuntimeOptions = CloneRuntimeOptions(profile.RuntimeOptions ?? new ProfileRuntimeOptions())
        };
    }

    private static ProfileRuntimeOptions CloneRuntimeOptions(ProfileRuntimeOptions options)
    {
        return new ProfileRuntimeOptions
        {
            InboundPort = NormalizePort(options.InboundPort),
            AllowLanConnections = options.AllowLanConnections,
            EnableSystemProxy = options.EnableSystemProxy,
            EnableTunInbound = options.EnableTunInbound,
            Initialized = options.Initialized
        };
    }

    private static int NormalizePort(int port)
    {
        return port is >= 1 and <= 65535 ? port : 2028;
    }

    private sealed class SingBoxData
    {
        public int SelectedProfileId { get; set; }
        public List<Profile> Profiles { get; set; } = new();
    }
}
