using Newtonsoft.Json;

namespace carton.Core.Models;

public class SingBoxConfig
{
    [JsonProperty("log")]
    public LogConfig? Log { get; set; }

    [JsonProperty("dns")]
    public DnsConfig? Dns { get; set; }

    [JsonProperty("inbounds")]
    public List<InboundConfig> Inbounds { get; set; } = new();

    [JsonProperty("outbounds")]
    public List<OutboundConfig> Outbounds { get; set; } = new();

    [JsonProperty("route")]
    public RouteConfig? Route { get; set; }

    [JsonProperty("experimental")]
    public ExperimentalConfig? Experimental { get; set; }
}

public class LogConfig
{
    [JsonProperty("disabled")]
    public bool Disabled { get; set; }

    [JsonProperty("level")]
    public string Level { get; set; } = "info";

    [JsonProperty("output")]
    public string? Output { get; set; }

    [JsonProperty("timestamp")]
    public bool Timestamp { get; set; } = true;
}

public class DnsConfig
{
    [JsonProperty("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonProperty("rules")]
    public List<DnsRule> Rules { get; set; } = new();

    [JsonProperty("final")]
    public string? Final { get; set; }

    [JsonProperty("strategy")]
    public string? Strategy { get; set; }
}

public class DnsServer
{
    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("address")]
    public string Address { get; set; } = string.Empty;

    [JsonProperty("detour")]
    public string? Detour { get; set; }
}

public class DnsRule
{
    [JsonProperty("outbound")]
    public string? Outbound { get; set; }

    [JsonProperty("server")]
    public string Server { get; set; } = string.Empty;
}

public class InboundConfig
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("listen")]
    public string Listen { get; set; } = "127.0.0.1";

    [JsonProperty("listen_port")]
    public int ListenPort { get; set; }

    [JsonProperty("sniff")]
    public bool Sniff { get; set; } = true;

    [JsonProperty("sniff_override_destination")]
    public bool SniffOverrideDestination { get; set; } = false;

    [JsonProperty("set_system_proxy")]
    public bool SetSystemProxy { get; set; } = false;
}

public class OutboundConfig
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonProperty("server")]
    public string? Server { get; set; }

    [JsonProperty("server_port")]
    public int? ServerPort { get; set; }

    [JsonProperty("uuid")]
    public string? Uuid { get; set; }

    [JsonProperty("password")]
    public string? Password { get; set; }

    [JsonProperty("method")]
    public string? Method { get; set; }

    [JsonProperty("tls")]
    public TlsConfig? Tls { get; set; }

    [JsonProperty("transport")]
    public TransportConfig? Transport { get; set; }
}

public class TlsConfig
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("server_name")]
    public string? ServerName { get; set; }

    [JsonProperty("insecure")]
    public bool Insecure { get; set; }

    [JsonProperty("alpn")]
    public List<string>? Alpn { get; set; }
}

public class TransportConfig
{
    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("host")]
    public List<string>? Host { get; set; }
}

public class RouteConfig
{
    [JsonProperty("rules")]
    public List<RouteRule> Rules { get; set; } = new();

    [JsonProperty("final")]
    public string? Final { get; set; }

    [JsonProperty("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;
}

public class RouteRule
{
    [JsonProperty("protocol")]
    public string? Protocol { get; set; }

    [JsonProperty("outbound")]
    public string Outbound { get; set; } = string.Empty;

    [JsonProperty("domain")]
    public List<string>? Domain { get; set; }

    [JsonProperty("domain_suffix")]
    public List<string>? DomainSuffix { get; set; }

    [JsonProperty("geoip")]
    public List<string>? Geoip { get; set; }

    [JsonProperty("geosite")]
    public List<string>? Geosite { get; set; }

    [JsonProperty("inbound")]
    public List<string>? Inbound { get; set; }

    [JsonProperty("ip_cidr")]
    public List<string>? IpCidr { get; set; }

    [JsonProperty("process_name")]
    public List<string>? ProcessName { get; set; }
}

public class ExperimentalConfig
{
    [JsonProperty("cache_file")]
    public CacheFileConfig? CacheFile { get; set; }

    [JsonProperty("clash_api")]
    public ClashApiConfig? ClashApi { get; set; }
}

public class CacheFileConfig
{
    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonProperty("path")]
    public string? Path { get; set; }

    [JsonProperty("store_fakeip")]
    public bool StoreFakeip { get; set; } = true;
}

public class ClashApiConfig
{
    [JsonProperty("external_controller")]
    public string? ExternalController { get; set; }

    [JsonProperty("external_ui")]
    public string? ExternalUi { get; set; }

    [JsonProperty("external_ui_download_url")]
    public string? ExternalUiDownloadUrl { get; set; }

    [JsonProperty("external_ui_download_detour")]
    public string? ExternalUiDownloadDetour { get; set; }

    [JsonProperty("secret")]
    public string? Secret { get; set; }

    [JsonProperty("default_mode")]
    public string DefaultMode { get; set; } = "rule";
}

