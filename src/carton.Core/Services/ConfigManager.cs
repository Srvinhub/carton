using carton.Core.Models;
using Newtonsoft.Json;

namespace carton.Core.Services;

public interface IConfigManager
{
    string ConfigDirectory { get; }
    string LocalConfigDirectory { get; }
    string RemoteConfigDirectory { get; }
    string RuntimeConfigDirectory { get; }
    string CacheDirectory { get; }
    string LogDirectory { get; }
    
    Task<string> CreateDefaultConfigAsync();
    Task<string?> LoadConfigAsync(int profileId, ProfileType? profileType = null);
    Task<string?> GetConfigPathAsync(int profileId, ProfileType? profileType = null);
    Task SaveConfigAsync(int profileId, string configContent);
    Task SaveConfigAsync(int profileId, string configContent, ProfileType profileType);
    Task DeleteConfigAsync(int profileId);
    Task<string> GenerateConfigAsync(ProfileConfigOptions options);
}

public class ProfileConfigOptions
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Protocol { get; set; } = "vmess";
    public string? Uuid { get; set; }
    public string? Password { get; set; }
    public string? Method { get; set; }
    public bool TlsEnabled { get; set; } = true;
    public string? ServerName { get; set; }
    public string? TransportType { get; set; }
    public string? TransportPath { get; set; }
    public int SocksPort { get; set; } = 10808;
    public int HttpPort { get; set; } = 10809;
    public int MixedPort { get; set; } = 7890;
    public int ApiPort { get; set; } = 9090;
}

public class ConfigManager : IConfigManager
{
    private readonly string _baseDirectory;
    private readonly JsonSerializerSettings _jsonSettings;

    public string ConfigDirectory { get; }
    public string LocalConfigDirectory { get; }
    public string RemoteConfigDirectory { get; }
    public string RuntimeConfigDirectory { get; }
    public string CacheDirectory { get; }
    public string LogDirectory { get; }

    public ConfigManager(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        ConfigDirectory = Path.Combine(baseDirectory, "configs");
        LocalConfigDirectory = Path.Combine(ConfigDirectory, "local");
        RemoteConfigDirectory = Path.Combine(ConfigDirectory, "remote");
        RuntimeConfigDirectory = Path.Combine(ConfigDirectory, "runtime");
        CacheDirectory = Path.Combine(baseDirectory, "cache");
        LogDirectory = Path.Combine(baseDirectory, "logs");

        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(LocalConfigDirectory);
        Directory.CreateDirectory(RemoteConfigDirectory);
        Directory.CreateDirectory(RuntimeConfigDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(LogDirectory);

        _jsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented
        };
    }

    public async Task<string> CreateDefaultConfigAsync()
    {
        var configContent = @"{
  ""log"": {
    ""level"": ""info"",
    ""timestamp"": true
  },
  ""dns"": {
    ""servers"": [
      {
        ""tag"": ""remote"",
        ""type"": ""udp"",
        ""server"": ""8.8.8.8""
      },
      {
        ""tag"": ""local"",
        ""type"": ""udp"",
        ""server"": ""223.5.5.5"",
        ""detour"": ""direct""
      }
    ],
    ""rules"": [
      {
        ""outbound"": ""any"",
        ""server"": ""remote""
      }
    ],
    ""final"": ""remote"",
    ""strategy"": ""prefer_ipv4""
  },
  ""inbounds"": [
    {
      ""type"": ""mixed"",
      ""tag"": ""mixed-in"",
      ""listen"": ""127.0.0.1"",
      ""listen_port"": 7890,
      ""sniff"": true,
      ""sniff_override_destination"": false
    }
  ],
  ""outbounds"": [
    {
      ""type"": ""selector"",
      ""tag"": ""proxy"",
      ""outbounds"": [""auto"", ""direct""]
    },
    {
      ""type"": ""urltest"",
      ""tag"": ""auto"",
      ""outbounds"": [""direct""],
      ""url"": ""https://www.gstatic.com/generate_204"",
      ""interval"": ""3m"",
      ""tolerance"": 50
    },
    {
      ""type"": ""direct"",
      ""tag"": ""direct""
    },
    {
      ""type"": ""block"",
      ""tag"": ""block""
    }
  ],
  ""route"": {
    ""rules"": [
      {
        ""action"": ""hijack-dns"",
        ""protocol"": ""dns""
      },
      {
        ""geoip"": [""private""],
        ""outbound"": ""direct""
      }
    ],
    ""final"": ""proxy"",
    ""auto_detect_interface"": true
  },
  ""experimental"": {
    ""cache_file"": {
      ""enabled"": true,
      ""path"": ""cache.db"",
      ""store_fakeip"": true
    },
    ""clash_api"": {
      ""external_controller"": ""127.0.0.1:9090"",
      ""secret"": """",
      ""default_mode"": ""rule""
    }
  }
}";

        var configPath = Path.Combine(RuntimeConfigDirectory, "default.json");
        await File.WriteAllTextAsync(configPath, configContent);
        return configPath;
    }

    public async Task<string?> LoadConfigAsync(int profileId, ProfileType? profileType = null)
    {
        var configPath = await GetConfigPathAsync(profileId, profileType);
        if (!File.Exists(configPath))
        {
            return null;
        }
        return await File.ReadAllTextAsync(configPath);
    }

    public async Task SaveConfigAsync(int profileId, string configContent)
    {
        var configPath = await ResolveSavePathAsync(profileId, null);
        await File.WriteAllTextAsync(configPath, configContent);
    }

    public async Task SaveConfigAsync(int profileId, string configContent, ProfileType profileType)
    {
        var configPath = await ResolveSavePathAsync(profileId, profileType);
        await File.WriteAllTextAsync(configPath, configContent);
    }

    public async Task DeleteConfigAsync(int profileId)
    {
        var fileName = GetProfileConfigFileName(profileId);
        var candidates = new[]
        {
            Path.Combine(LocalConfigDirectory, fileName),
            Path.Combine(RemoteConfigDirectory, fileName),
            Path.Combine(ConfigDirectory, fileName),
            Path.Combine(RuntimeConfigDirectory, $"profile_{profileId}.runtime.json")
        };

        await Task.Run(() =>
        {
            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        });
    }

    public async Task<string?> GetConfigPathAsync(int profileId, ProfileType? profileType = null)
    {
        var fileName = GetProfileConfigFileName(profileId);
        var localPath = Path.Combine(LocalConfigDirectory, fileName);
        var remotePath = Path.Combine(RemoteConfigDirectory, fileName);
        var legacyPath = Path.Combine(ConfigDirectory, fileName);

        if (profileType.HasValue)
        {
            var typedPath = GetTypedProfileConfigPath(profileId, profileType.Value);
            if (File.Exists(typedPath))
            {
                return typedPath;
            }

            // Migrate old root config into its typed directory.
            if (File.Exists(legacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(typedPath)!);
                await Task.Run(() => File.Move(legacyPath, typedPath, overwrite: true));
                return typedPath;
            }

            var oppositePath = profileType == ProfileType.Local ? remotePath : localPath;
            if (File.Exists(oppositePath))
            {
                return oppositePath;
            }

            return null;
        }

        if (File.Exists(localPath))
        {
            return localPath;
        }

        if (File.Exists(remotePath))
        {
            return remotePath;
        }

        if (File.Exists(legacyPath))
        {
            return legacyPath;
        }

        return null;
    }

    public async Task<string> GenerateConfigAsync(ProfileConfigOptions options)
    {
        var config = new SingBoxConfig
        {
            Log = new LogConfig
            {
                Level = "info",
                Timestamp = true
            },
            Dns = new DnsConfig
            {
                Servers = new List<DnsServer>
                {
                    new() { Tag = "remote", Address = "tls://8.8.8.8" },
                    new() { Tag = "local", Address = "223.5.5.5", Detour = "direct" }
                },
                Rules = new List<DnsRule>
                {
                    new() { Outbound = "any", Server = "remote" }
                },
                Final = "remote",
                Strategy = "prefer_ipv4"
            },
            Inbounds = new List<InboundConfig>
            {
                new()
                {
                    Type = "mixed",
                    Tag = "mixed-in",
                    Listen = "127.0.0.1",
                    ListenPort = options.MixedPort,
                    Sniff = true,
                    SniffOverrideDestination = false
                }
            },
            Outbounds = new List<OutboundConfig>
            {
                CreateOutboundConfig(options),
                new() { Type = "direct", Tag = "direct" },
                new() { Type = "block", Tag = "block" },
                new() { Type = "dns", Tag = "dns" }
            },
            Route = new RouteConfig
            {
                Rules = new List<RouteRule>
                {
                    new() { Protocol = "dns", Outbound = "dns" },
                    new() { Geoip = new List<string> { "private" }, Outbound = "direct" }
                },
                Final = "proxy",
                AutoDetectInterface = true
            },
            Experimental = new ExperimentalConfig
            {
                CacheFile = new CacheFileConfig
                {
                    Enabled = true,
                    Path = "cache.db"
                },
                ClashApi = new ClashApiConfig
                {
                    ExternalController = $"127.0.0.1:{options.ApiPort}",
                    Secret = "",
                    DefaultMode = "rule"
                }
            }
        };

        var configPath = Path.Combine(RuntimeConfigDirectory, $"generated_{DateTime.Now:yyyyMMddHHmmss}.json");
        var json = JsonConvert.SerializeObject(config, _jsonSettings);
        await File.WriteAllTextAsync(configPath, json);
        return configPath;
    }

    private static string GetProfileConfigFileName(int profileId)
    {
        return $"profile_{profileId}.json";
    }

    private string GetTypedProfileConfigPath(int profileId, ProfileType profileType)
    {
        var directory = profileType == ProfileType.Local ? LocalConfigDirectory : RemoteConfigDirectory;
        return Path.Combine(directory, GetProfileConfigFileName(profileId));
    }

    private async Task<string> ResolveSavePathAsync(int profileId, ProfileType? profileType)
    {
        var fileName = GetProfileConfigFileName(profileId);
        var legacyPath = Path.Combine(ConfigDirectory, fileName);

        if (profileType.HasValue)
        {
            var typedPath = GetTypedProfileConfigPath(profileId, profileType.Value);
            if (File.Exists(legacyPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(typedPath)!);
                await Task.Run(() => File.Move(legacyPath, typedPath, overwrite: true));
            }

            return typedPath;
        }

        var existingPath = await GetConfigPathAsync(profileId);
        if (!string.IsNullOrWhiteSpace(existingPath) && !string.Equals(existingPath, legacyPath, StringComparison.OrdinalIgnoreCase))
        {
            return existingPath;
        }

        // Unknown type defaults to local to avoid mixing local/remote in root.
        var defaultPath = Path.Combine(LocalConfigDirectory, fileName);
        if (File.Exists(legacyPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(defaultPath)!);
            await Task.Run(() => File.Move(legacyPath, defaultPath, overwrite: true));
        }

        return defaultPath;
    }

    private static OutboundConfig CreateOutboundConfig(ProfileConfigOptions options)
    {
        var outbound = new OutboundConfig
        {
            Type = options.Protocol,
            Tag = "proxy",
            Server = options.Server,
            ServerPort = options.Port
        };

        if (options.Protocol == "vmess" || options.Protocol == "vless")
        {
            outbound.Uuid = options.Uuid;
        }
        else if (options.Protocol == "shadowsocks")
        {
            outbound.Password = options.Password;
            outbound.Method = options.Method ?? "aes-128-gcm";
        }
        else if (options.Protocol == "trojan")
        {
            outbound.Password = options.Password;
        }

        if (options.TlsEnabled)
        {
            outbound.Tls = new TlsConfig
            {
                Enabled = true,
                ServerName = options.ServerName ?? options.Server,
                Insecure = false
            };
        }

        if (!string.IsNullOrEmpty(options.TransportType))
        {
            outbound.Transport = new TransportConfig
            {
                Type = options.TransportType,
                Path = options.TransportPath
            };
        }

        return outbound;
    }
}
