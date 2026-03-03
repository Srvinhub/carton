using System.Text.Json.Serialization;

namespace carton.Core.Models;

public class SingBoxConfig
{
    [JsonPropertyName("log")]
    public LogConfig? Log { get; set; }

    [JsonPropertyName("dns")]
    public DnsConfig? Dns { get; set; }

    [JsonPropertyName("inbounds")]
    public List<InboundConfig> Inbounds { get; set; } = new();

    [JsonPropertyName("outbounds")]
    public List<OutboundConfig> Outbounds { get; set; } = new();

    [JsonPropertyName("route")]
    public RouteConfig? Route { get; set; }

    [JsonPropertyName("experimental")]
    public ExperimentalConfig? Experimental { get; set; }
}

public class LogConfig
{
    [JsonPropertyName("disabled")]
    public bool Disabled { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("output")]
    public string? Output { get; set; }

    [JsonPropertyName("timestamp")]
    public bool Timestamp { get; set; } = true;
}

public class DnsConfig
{
    [JsonPropertyName("servers")]
    public List<DnsServer> Servers { get; set; } = new();

    [JsonPropertyName("rules")]
    public List<DnsRule> Rules { get; set; } = new();

    [JsonPropertyName("final")]
    public string? Final { get; set; }

    [JsonPropertyName("strategy")]
    public string? Strategy { get; set; }
}

public class DnsServer
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("detour")]
    public string? Detour { get; set; }
}

public class DnsRule
{
    [JsonPropertyName("outbound")]
    public string? Outbound { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = string.Empty;
}

public class InboundConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("listen")]
    public string Listen { get; set; } = "127.0.0.1";

    [JsonPropertyName("listen_port")]
    public int ListenPort { get; set; }

    [JsonPropertyName("sniff")]
    public bool Sniff { get; set; } = true;

    [JsonPropertyName("sniff_override_destination")]
    public bool SniffOverrideDestination { get; set; } = false;

    [JsonPropertyName("set_system_proxy")]
    public bool SetSystemProxy { get; set; } = false;
}

public class OutboundConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("server")]
    public string? Server { get; set; }

    [JsonPropertyName("server_port")]
    public int? ServerPort { get; set; }

    [JsonPropertyName("uuid")]
    public string? Uuid { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("tls")]
    public TlsConfig? Tls { get; set; }

    [JsonPropertyName("transport")]
    public TransportConfig? Transport { get; set; }
}

public class TlsConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("server_name")]
    public string? ServerName { get; set; }

    [JsonPropertyName("insecure")]
    public bool Insecure { get; set; }

    [JsonPropertyName("alpn")]
    public List<string>? Alpn { get; set; }
}

public class TransportConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("host")]
    public List<string>? Host { get; set; }
}

public class RouteConfig
{
    [JsonPropertyName("rules")]
    public List<RouteRule> Rules { get; set; } = new();

    [JsonPropertyName("final")]
    public string? Final { get; set; }

    [JsonPropertyName("auto_detect_interface")]
    public bool AutoDetectInterface { get; set; } = true;
}

public class RouteRule
{
    [JsonPropertyName("protocol")]
    public string? Protocol { get; set; }

    [JsonPropertyName("outbound")]
    public string Outbound { get; set; } = string.Empty;

    [JsonPropertyName("domain")]
    public List<string>? Domain { get; set; }

    [JsonPropertyName("domain_suffix")]
    public List<string>? DomainSuffix { get; set; }

    [JsonPropertyName("geoip")]
    public List<string>? Geoip { get; set; }

    [JsonPropertyName("geosite")]
    public List<string>? Geosite { get; set; }

    [JsonPropertyName("inbound")]
    public List<string>? Inbound { get; set; }

    [JsonPropertyName("ip_cidr")]
    public List<string>? IpCidr { get; set; }

    [JsonPropertyName("process_name")]
    public List<string>? ProcessName { get; set; }
}

public class ExperimentalConfig
{
    [JsonPropertyName("cache_file")]
    public CacheFileConfig? CacheFile { get; set; }

    [JsonPropertyName("clash_api")]
    public ClashApiConfig? ClashApi { get; set; }
}

public class CacheFileConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("store_fakeip")]
    public bool StoreFakeip { get; set; } = true;
}

public class ClashApiConfig
{
    [JsonPropertyName("external_controller")]
    public string? ExternalController { get; set; }

    [JsonPropertyName("external_ui")]
    public string? ExternalUi { get; set; }

    [JsonPropertyName("external_ui_download_url")]
    public string? ExternalUiDownloadUrl { get; set; }

    [JsonPropertyName("external_ui_download_detour")]
    public string? ExternalUiDownloadDetour { get; set; }

    [JsonPropertyName("secret")]
    public string? Secret { get; set; }

    [JsonPropertyName("default_mode")]
    public string DefaultMode { get; set; } = "rule";
}

