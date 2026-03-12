using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace carton.GUI.Services;

public sealed class ClashConfigCacheService
{
    private static readonly Lazy<ClashConfigCacheService> _instance = new(() => new ClashConfigCacheService());
    public static ClashConfigCacheService Instance => _instance.Value;

    private ClashConfigCacheService()
    {
    }

    public ClashConfigSnapshot? Current { get; private set; }
    public bool IsDirty { get; private set; }

    public void Update(ClashConfigSnapshot? config, bool isDirty = false)
    {
        Current = config;
        IsDirty = isDirty;
    }

    public void Clear()
    {
        Current = null;
        IsDirty = false;
    }

    public void MarkClean()
    {
        IsDirty = false;
    }
}

public sealed class ClashConfigSnapshot
{
    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("mode-list")]
    public List<string>? ModeList { get; set; }
}
