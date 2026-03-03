namespace carton.Core.Models;

internal sealed class WindowsHelperStartRequest
{
    public string SingBoxPath { get; set; } = string.Empty;
    public string ConfigPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
}

internal sealed class WindowsHelperActionResponse
{
    public bool Success { get; set; }
    public int? Pid { get; set; }
    public string? Error { get; set; }
}
