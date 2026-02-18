using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using carton.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace carton.Core.Services;

public interface ISingBoxManager
{
    event EventHandler<ServiceStatus>? StatusChanged;
    event EventHandler<TrafficInfo>? TrafficUpdated;
    event EventHandler<string>? LogReceived;

    ServiceState State { get; }
    bool IsRunning { get; }

    Task<bool> SyncRunningStateAsync();
    Task<bool> StartAsync(string configPath);
    Task StopAsync();
    Task ReloadAsync();
    Task<List<OutboundGroup>> GetOutboundGroupsAsync();
    Task SelectOutboundAsync(string groupTag, string outboundTag);
    Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000);
    Task<List<ConnectionInfo>> GetConnectionsAsync();
    Task CloseConnectionAsync(string connectionId);
    Task CloseAllConnectionsAsync();
}

public class SingBoxManager : ISingBoxManager, IDisposable
{
    private const string MacTunPermissionPrompt = "Carton requires administrator permission to start or stop the TUN interface (sing-box).";
    private const string DefaultDelayTestUrl = "https://www.gstatic.com/generate_204";

    private readonly string _singBoxPath;
    private readonly string _workingDirectory;
    private Process? _process;
    private readonly ServiceState _state = new();
    private readonly HttpClient _httpClient;
    private readonly string _apiAddress;
    private readonly int _apiPort;
    private bool _disposed;
    private readonly List<string> _errorOutput = new();
    private int? _elevatedPid;
    private string? _elevatedLogPath;
    private CancellationTokenSource? _elevatedLogCts;
    private Task? _elevatedLogTask;
    private Task? _trafficMonitorTask;
    private IntPtr _windowsJobHandle = IntPtr.Zero;
    private const int WindowsElevatedHelperPort = 47891;
    private const string WindowsElevatedHelperArg = "--carton-elevated-helper";
    private const string WindowsElevatedHelperTokenHeader = "X-Carton-Helper-Token";
    private string? _windowsElevatedHelperToken;
    private int? _windowsElevatedHelperPid;

    public event EventHandler<ServiceStatus>? StatusChanged;
    public event EventHandler<TrafficInfo>? TrafficUpdated;
    public event EventHandler<string>? LogReceived;

    public ServiceState State => _state;
    public bool IsRunning => _state.Status == ServiceStatus.Running;

    public async Task<bool> SyncRunningStateAsync()
    {
        if (await IsApiReachableAsync())
        {
            if (_state.Status != ServiceStatus.Running)
            {
                _state.StartTime ??= DateTime.Now;
                UpdateStatus(ServiceStatus.Running);
                LogReceived?.Invoke(this, "[INFO] Detected existing sing-box instance, synchronized running state");
            }

            if (!_elevatedPid.HasValue)
            {
                _elevatedPid = await TryFindProcessPidByApiPortAsync();
            }

            EnsureTrafficMonitorRunning();
            return true;
        }

        if (_state.Status == ServiceStatus.Running)
        {
            _state.StartTime = null;
            UpdateStatus(ServiceStatus.Stopped);
        }

        return false;
    }

    public SingBoxManager(string singBoxPath, string workingDirectory, int apiPort = 9090)
    {
        _singBoxPath = singBoxPath;
        _workingDirectory = workingDirectory;
        _apiPort = apiPort;
        _apiAddress = $"http://127.0.0.1:{apiPort}";
        _httpClient = new HttpClient();

        Directory.CreateDirectory(_workingDirectory);
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "logs"));
        Directory.CreateDirectory(Path.Combine(_workingDirectory, "cache"));

        AppDomain.CurrentDomain.ProcessExit += OnCurrentProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryInitializeWindowsProcessJob();
        }
    }

    public async Task<bool> StartAsync(string configPath)
    {
        if (_state.Status == ServiceStatus.Running)
        {
            return true;
        }

        if (!File.Exists(configPath))
        {
            var error = $"Configuration file not found: {configPath}";
            LogReceived?.Invoke(this, $"[ERROR] {error}");
            SetError(error);
            return false;
        }

        if (!File.Exists(_singBoxPath))
        {
            var error = $"sing-box binary not found at: {_singBoxPath}";
            LogReceived?.Invoke(this, $"[ERROR] {error}");
            SetError(error);
            return false;
        }

        try
        {
            UpdateStatus(ServiceStatus.Starting);
            _errorOutput.Clear();

            LogReceived?.Invoke(this, $"[INFO] Starting sing-box with config: {configPath}");
            LogReceived?.Invoke(this, $"[INFO] Binary path: {_singBoxPath}");

            if (RequiresElevatedPrivileges(configPath))
            {
                LogReceived?.Invoke(this, "[INFO] TUN inbound detected, requesting elevated privileges...");
                return await StartElevatedAsync(configPath);
            }

            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _singBoxPath,
                    Arguments = $"run -c \"{configPath}\"",
                    WorkingDirectory = _workingDirectory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };

            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    LogReceived?.Invoke(this, e.Data);
                }
            };

            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    _errorOutput.Add(e.Data);
                    LogReceived?.Invoke(this, $"[ERROR] {e.Data}");
                }
            };

            _process.Exited += (_, _) =>
            {
                var exitCode = _process.ExitCode;
                if (_state.Status == ServiceStatus.Running || _state.Status == ServiceStatus.Starting)
                {
                    var errorMsg = $"sing-box exited with code {exitCode}";
                    if (_errorOutput.Count > 0)
                    {
                        errorMsg += $": {string.Join("\n", _errorOutput)}";
                    }
                    LogReceived?.Invoke(this, $"[ERROR] {errorMsg}");
                    SetError(errorMsg);
                }
            };

            _process.EnableRaisingEvents = true;

            LogReceived?.Invoke(this, "[INFO] Starting process...");
            _process.Start();
            TryAttachProcessToWindowsJob(_process);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            await Task.Delay(2000);

            if (_process.HasExited)
            {
                var exitCode = _process.ExitCode;
                var errorMsg = $"sing-box process exited unexpectedly with code {exitCode}";
                if (_errorOutput.Count > 0)
                {
                    errorMsg += $"\n{string.Join("\n", _errorOutput)}";
                }
                LogReceived?.Invoke(this, $"[ERROR] {errorMsg}");
                SetError(errorMsg);
                return false;
            }

            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogReceived?.Invoke(this, "[INFO] sing-box started successfully");

            EnsureTrafficMonitorRunning();

            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to start sing-box: {ex.Message}";
            LogReceived?.Invoke(this, $"[ERROR] {error}");
            SetError(error);
            return false;
        }
    }

    public async Task StopAsync()
    {
        if (_process == null && !_elevatedPid.HasValue)
        {
            _elevatedPid = await TryFindProcessPidByApiPortAsync();
            if (!_elevatedPid.HasValue)
            {
                return;
            }
        }

        try
        {
            LogReceived?.Invoke(this, "[INFO] Stopping sing-box...");
            UpdateStatus(ServiceStatus.Stopping);
            var stopped = true;

            if (_process != null)
            {
                _process.Kill(true);
                await _process.WaitForExitAsync();
                _process.Dispose();
                _process = null;
            }
            else if (_elevatedPid.HasValue)
            {
                stopped = await StopElevatedAsync();
            }

            if (!stopped)
            {
                var error = "Failed to stop sing-box: elevated process is still running";
                LogReceived?.Invoke(this, $"[ERROR] {error}");
                SetError(error);
                return;
            }

            await StopElevatedLogTailAsync();
            _elevatedPid = null;
            _elevatedLogPath = null;

            _state.StartTime = null;
            UpdateStatus(ServiceStatus.Stopped);
            LogReceived?.Invoke(this, "[INFO] sing-box stopped");
        }
        catch (Exception ex)
        {
            var error = $"Failed to stop sing-box: {ex.Message}";
            LogReceived?.Invoke(this, $"[ERROR] {error}");
            SetError(error);
        }
    }

    public async Task ReloadAsync()
    {
        if (_state.Status != ServiceStatus.Running)
        {
            return;
        }

        try
        {
            var response = await _httpClient.PutAsync($"{_apiAddress}/configs", new StringContent(""));
            response.EnsureSuccessStatusCode();
        }
        catch
        {
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryShutdownWindowsHelperWithoutThrow();
            }
        }
        catch
        {
        }
    }

    public async Task<List<OutboundGroup>> GetOutboundGroupsAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_apiAddress}/proxies");
            var json = JObject.Parse(response);
            var groups = new List<OutboundGroup>();

            if (json["proxies"] is JObject proxies)
            {
                foreach (var prop in proxies.Properties())
                {
                    if (prop.Value is not JObject proxy)
                    {
                        continue;
                    }

                    var group = new OutboundGroup
                    {
                        Tag = prop.Name,
                        Type = proxy.Value<string>("type") ?? string.Empty,
                        Selected = proxy.Value<string>("now") ?? string.Empty
                    };

                    if (proxy["all"] is JArray all)
                    {
                        foreach (var item in all)
                        {
                            var itemStr = item.Value<string>() ?? string.Empty;
                            group.Items.Add(new OutboundItem { Tag = itemStr });
                        }
                    }

                    groups.Add(group);
                }
            }

            return groups;
        }
        catch
        {
            return new List<OutboundGroup>();
        }
    }

    public async Task SelectOutboundAsync(string groupTag, string outboundTag)
    {
        try
        {
            var content = new StringContent(
                JsonConvert.SerializeObject(new { name = outboundTag }),
                Encoding.UTF8,
                "application/json"
            );
            await _httpClient.PutAsync($"{_apiAddress}/proxies/{Uri.EscapeDataString(groupTag)}", content);
        }
        catch
        {
        }
    }

    public async Task<Dictionary<string, int>> RunOutboundDelayTestsAsync(IEnumerable<string> outboundTags, string? testUrl = null, int timeoutMs = 5000)
    {
        var result = new Dictionary<string, int>();
        if (outboundTags == null)
        {
            return result;
        }

        var urlParam = Uri.EscapeDataString(string.IsNullOrWhiteSpace(testUrl) ? DefaultDelayTestUrl : testUrl);

        foreach (var tag in outboundTags)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                continue;
            }

            try
            {
                var endpoint = $"{_apiAddress}/proxies/{Uri.EscapeDataString(tag)}/delay?timeout={timeoutMs}&url={urlParam}";
                using var response = await _httpClient.GetAsync(endpoint);
                if (!response.IsSuccessStatusCode)
                {
                    result[tag] = -1;
                    continue;
                }

                var payload = await response.Content.ReadAsStringAsync();
                var json = JObject.Parse(payload);
                if (json["delay"] != null && int.TryParse(json["delay"]!.ToString(), out var delay))
                {
                    result[tag] = delay;
                }
                else
                {
                    result[tag] = -1;
                }
            }
            catch
            {
                result[tag] = -1;
            }
        }

        return result;
    }
    public async Task<List<ConnectionInfo>> GetConnectionsAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync($"{_apiAddress}/connections");
            var json = JObject.Parse(response);
            var connections = new List<ConnectionInfo>();

            if (json["connections"] is not JArray conns)
            {
                return connections;
            }

            foreach (var conn in conns.OfType<JObject>())
            {
                var metadata = conn["metadata"] as JObject;
                var chains = conn["chains"];

                connections.Add(new ConnectionInfo
                {
                    Id = ReadString(conn, "id"),
                    StartTime = ReadDateTime(conn, "start"),
                    Inbound = ReadString(conn, "inbound"),
                    Process = ReadString(metadata, "process"),
                    Ip = ReadString(metadata, "sourceIP"),
                    Source = ComposeEndpoint(
                        ReadString(metadata, "sourceIP"),
                        ReadString(metadata, "sourcePort")),
                    Destination = ComposeDestination(
                        ReadString(metadata, "host"),
                        ReadString(metadata, "destinationIP"),
                        ReadString(metadata, "destinationPort")),
                    Domain = ReadString(metadata, "host"),
                    Protocol = ResolveProtocol(conn, metadata),
                    Outbound = ResolveOutbound(conn, chains),
                    Upload = ReadInt64(conn, "upload"),
                    Download = ReadInt64(conn, "download")
                });
            }

            return connections;
        }
        catch
        {
            return new List<ConnectionInfo>();
        }
    }

    public async Task CloseConnectionAsync(string connectionId)
    {
        try
        {
            await _httpClient.DeleteAsync($"{_apiAddress}/connections/{Uri.EscapeDataString(connectionId)}");
        }
        catch
        {
        }
    }

    private static string ResolveProtocol(JObject connection, JObject? metadata)
    {
        var protocol = ReadString(connection, "protocol");
        return string.IsNullOrWhiteSpace(protocol) ? ReadString(metadata, "network") : protocol;
    }

    private static string ResolveOutbound(JObject connection, JToken? chains)
    {
        var outbound = ReadString(connection, "outbound");
        if (!string.IsNullOrWhiteSpace(outbound))
        {
            return outbound;
        }

        if (chains is JArray chainArray)
        {
            var tags = new List<string>();
            foreach (var chain in chainArray)
            {
                var tag = chain.Value<string>();
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    tags.Add(tag);
                }
            }

            if (tags.Count > 0)
            {
                return string.Join(" -> ", tags);
            }
        }

        return string.Empty;
    }

    private static string ComposeEndpoint(string address, string port)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? address : $"{address}:{port}";
    }

    private static string ComposeDestination(string host, string destinationIp, string port)
    {
        var target = string.IsNullOrWhiteSpace(host) ? destinationIp : host;
        if (string.IsNullOrWhiteSpace(target))
        {
            return string.Empty;
        }

        return string.IsNullOrWhiteSpace(port) ? target : $"{target}:{port}";
    }

    private static string ReadString(JObject? element, string propertyName)
    {
        if (element == null)
        {
            return string.Empty;
        }

        return element.TryGetValue(propertyName, StringComparison.Ordinal, out var property) && property.Type != JTokenType.Null
            ? property.Value<string>() ?? string.Empty
            : string.Empty;
    }

    private static long ReadInt64(JObject? element, string propertyName)
    {
        if (element == null)
        {
            return 0;
        }

        if (!element.TryGetValue(propertyName, StringComparison.Ordinal, out var property))
        {
            return 0;
        }

        if (property.Type is JTokenType.Integer or JTokenType.Float &&
            long.TryParse(property.ToString(), out var integerValue))
        {
            return integerValue;
        }

        return property.Type is JTokenType.Integer or JTokenType.Float &&
               double.TryParse(property.ToString(), out var floatingValue)
            ? (long)floatingValue
            : 0;
    }

    private static DateTime ReadDateTime(JObject? element, string propertyName)
    {
        if (element != null &&
            element.TryGetValue(propertyName, StringComparison.Ordinal, out var property) &&
            property.Type == JTokenType.Date)
        {
            return property.Value<DateTime>();
        }

        if (element != null &&
            element.TryGetValue(propertyName, StringComparison.Ordinal, out property) &&
            property.Type == JTokenType.String &&
            DateTime.TryParse(property.ToString(), out var timestamp))
        {
            return timestamp;
        }

        return DateTime.Now;
    }

    public async Task CloseAllConnectionsAsync()
    {
        try
        {
            await _httpClient.DeleteAsync($"{_apiAddress}/connections");
        }
        catch
        {
        }
    }

    private async Task StartTrafficMonitorAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        var stream = new MemoryStream();
        ClientWebSocket? webSocket = null;
        var wsUri = BuildWebSocketUri("traffic");
        var connectionsCts = new CancellationTokenSource();
        var connectionTask = MonitorConnectionsAsync(connectionsCts.Token);

        try
        {
            while (_state.Status == ServiceStatus.Running)
            {
                try
                {
                    if (webSocket == null || webSocket.State != WebSocketState.Open)
                    {
                        webSocket?.Dispose();
                        webSocket = new ClientWebSocket();
                        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await webSocket.ConnectAsync(wsUri, connectCts.Token);
                        stream.SetLength(0);
                    }

                    var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor reconnect");
                        webSocket.Dispose();
                        webSocket = null;
                        await Task.Delay(500);
                        continue;
                    }

                    if (result.Count > 0)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        stream.SetLength(0);
                        continue;
                    }

                    if (stream.Length == 0)
                    {
                        continue;
                    }

                    var payload = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    stream.SetLength(0);
                    var traffic = TryParseTrafficSnapshot(payload);
                    if (traffic == null)
                    {
                        continue;
                    }

                    _state.UploadSpeed = traffic.Uplink;
                    _state.DownloadSpeed = traffic.Downlink;
                    _state.TotalUpload += traffic.Uplink;
                    _state.TotalDownload += traffic.Downlink;
                    TrafficUpdated?.Invoke(this, traffic);
                }
                catch (Exception e)
                {
                    LogReceived?.Invoke(this, $"[WARN] Traffic monitor error: {e.Message}");
                    if (webSocket != null)
                    {
                        await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor error");
                        webSocket.Dispose();
                        webSocket = null;
                    }

                    stream.SetLength(0);
                    await Task.Delay(1000);
                }
            }
        }
        finally
        {
            connectionsCts.Cancel();
            try
            {
                await connectionTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                LogReceived?.Invoke(this, $"[WARN] Connection monitor error: {e.Message}");
            }
            finally
            {
                connectionsCts.Dispose();
            }

            if (webSocket != null)
            {
                await CloseSocketSilentlyAsync(webSocket, "Carton traffic monitor stopped");
                webSocket.Dispose();
            }

            stream.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
            _trafficMonitorTask = null;
        }

        async Task MonitorConnectionsAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _state.Status == ServiceStatus.Running)
            {
                try
                {
                    var connections = await GetConnectionsAsync();
                    _state.ConnectionCount = connections.Count;
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void EnsureTrafficMonitorRunning()
    {
        if (_trafficMonitorTask is { IsCompleted: false })
        {
            return;
        }

        _trafficMonitorTask = Task.Run(StartTrafficMonitorAsync);
    }

    private TrafficInfo? TryParseTrafficSnapshot(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var root = JObject.Parse(payload);
            var uplink = ReadTrafficValue(root, "uplink", "up", "upload");
            var downlink = ReadTrafficValue(root, "downlink", "down", "download");
            return new TrafficInfo
            {
                Uplink = uplink,
                Downlink = downlink
            };
        }
        catch (JsonException ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Failed to parse traffic snapshot: {ex.Message}");
            return null;
        }
    }

    private static long ReadTrafficValue(JObject root, params string[] propertyNames)
    {
        if (TryReadTrafficValue(root, propertyNames, out var value))
        {
            return value;
        }

        if (root.TryGetValue("now", StringComparison.Ordinal, out var nowElement) &&
            nowElement is JObject nowObject &&
            TryReadTrafficValue(nowObject, propertyNames, out value))
        {
            return value;
        }

        return 0;
    }

    private static async Task CloseSocketSilentlyAsync(ClientWebSocket socket, string reason)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch
        {
        }
    }

    private static bool TryReadTrafficValue(JToken element, IReadOnlyList<string> propertyNames, out long value)
    {
        if (element is not JObject obj)
        {
            value = 0;
            return false;
        }

        foreach (var name in propertyNames)
        {
            if (!obj.TryGetValue(name, StringComparison.Ordinal, out var property) ||
                property.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                continue;
            }

            if (long.TryParse(property.ToString(), out var result))
            {
                value = result;
                return true;
            }

            if (double.TryParse(property.ToString(), out var floating))
            {
                value = (long)floating;
                return true;
            }
        }

        value = 0;
        return false;
    }

    private Uri BuildWebSocketUri(string relativePath)
    {
        var builder = new UriBuilder(_apiAddress)
        {
            Scheme = _apiAddress.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = relativePath.TrimStart('/'),
            Query = string.Empty
        };
        return builder.Uri;
    }

    private void UpdateStatus(ServiceStatus status)
    {
        _state.Status = status;
        _state.ErrorMessage = null;
        StatusChanged?.Invoke(this, status);
    }

    private void SetError(string message)
    {
        _state.Status = ServiceStatus.Error;
        _state.ErrorMessage = message;
        StatusChanged?.Invoke(this, ServiceStatus.Error);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        AppDomain.CurrentDomain.ProcessExit -= OnCurrentProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            TryShutdownWindowsHelperWithoutThrow();
        }

        _process?.Kill(true);
        _process?.Dispose();
        _elevatedLogCts?.Cancel();
        _elevatedLogCts?.Dispose();
        _httpClient.Dispose();

        if (_windowsJobHandle != IntPtr.Zero)
        {
            CloseHandle(_windowsJobHandle);
            _windowsJobHandle = IntPtr.Zero;
        }
    }

    private bool RequiresElevatedPrivileges(string configPath)
    {
        try
        {
            var content = File.ReadAllText(configPath);
            var document = JObject.Parse(content);
            if (document["inbounds"] is not JArray inbounds)
            {
                return false;
            }

            foreach (var inbound in inbounds.OfType<JObject>())
            {
                if (string.Equals(inbound.Value<string>("type"), "tun", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Failed to inspect config for TUN: {ex.Message}");
        }

        return false;
    }

    private async Task<bool> StartElevatedAsync(string configPath)
    {
        try
        {
            var elevatedLogPath = Path.Combine(_workingDirectory, "logs", "sing-box.elevated.log");
            Directory.CreateDirectory(Path.GetDirectoryName(elevatedLogPath)!);
            await File.WriteAllTextAsync(elevatedLogPath, string.Empty);

            var result = await StartElevatedProcessForCurrentPlatformAsync(configPath, elevatedLogPath);
            if (!result.Success)
            {
                var msg = string.IsNullOrWhiteSpace(result.ErrorMessage) ? "Elevated start failed" : result.ErrorMessage;
                LogReceived?.Invoke(this, $"[ERROR] {msg}");
                SetError(msg);
                return false;
            }

            if (!result.Pid.HasValue || result.Pid.Value <= 0)
            {
                var msg = "Failed to get elevated process PID";
                LogReceived?.Invoke(this, $"[ERROR] {msg}");
                SetError(msg);
                return false;
            }

            var pid = result.Pid.Value;
            _elevatedPid = pid;
            TryAttachProcessIdToWindowsJob(pid);
            _elevatedLogPath = elevatedLogPath;
            StartElevatedLogTail(elevatedLogPath);

            var ready = await WaitForElevatedStartupReadyAsync(pid, TimeSpan.FromSeconds(6));
            if (!ready)
            {
                var fallbackPid = _elevatedPid;
                if (!fallbackPid.HasValue || fallbackPid.Value <= 0)
                {
                    fallbackPid = await TryFindProcessPidByApiPortAsync();
                }

                var running = false;
                if (fallbackPid.HasValue && fallbackPid.Value > 0)
                {
                    running = await IsProcessRunningAsync(fallbackPid.Value);
                    if (running)
                    {
                        _elevatedPid = fallbackPid.Value;
                    }
                }

                if (!running)
                {
                    running = await IsProcessRunningAsync(pid);
                    if (running)
                    {
                        _elevatedPid = pid;
                    }
                }

                if (running)
                {
                    LogReceived?.Invoke(this, "[WARN] sing-box process is running but API is not reachable yet; continuing as running");
                }
                else
                {
                    var recentLog = await ReadRecentLogLinesAsync(elevatedLogPath, 20);
                    var msg = "sing-box elevated process exited unexpectedly" +
                              (string.IsNullOrWhiteSpace(recentLog) ? string.Empty : $": {recentLog}");
                    LogReceived?.Invoke(this, $"[ERROR] {msg}");
                    SetError(msg);
                    return false;
                }
            }

            _state.StartTime = DateTime.Now;
            UpdateStatus(ServiceStatus.Running);
            LogReceived?.Invoke(this, $"[INFO] sing-box started successfully (elevated, pid={pid})");
            EnsureTrafficMonitorRunning();
            return true;
        }
        catch (Exception ex)
        {
            var error = $"Failed to start sing-box with administrator privileges: {ex.Message}";
            LogReceived?.Invoke(this, $"[ERROR] {error}");
            SetError(error);
            return false;
        }
    }

    private async Task<bool> WaitForElevatedStartupReadyAsync(int initialPid, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        var runningHits = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            if (await IsApiReachableAsync())
            {
                var discoveredPid = await TryFindProcessPidByApiPortAsync();
                if (discoveredPid.HasValue && discoveredPid.Value > 0)
                {
                    _elevatedPid = discoveredPid.Value;
                }

                return true;
            }

            var discoveredByPort = await TryFindProcessPidByApiPortAsync();
            if (discoveredByPort.HasValue && discoveredByPort.Value > 0)
            {
                _elevatedPid = discoveredByPort.Value;
            }

            var pidToCheck = _elevatedPid.HasValue && _elevatedPid.Value > 0
                ? _elevatedPid.Value
                : initialPid;

            if (pidToCheck > 0 && await IsProcessRunningAsync(pidToCheck))
            {
                _elevatedPid = pidToCheck;
                runningHits++;
                if (runningHits >= 2)
                {
                    return true;
                }
            }
            else
            {
                runningHits = 0;
                if (_elevatedPid == initialPid)
                {
                    _elevatedPid = null;
                }
            }

            await Task.Delay(200);
        }

        return false;
    }

    private async Task<bool> StopElevatedAsync()
    {
        if (!_elevatedPid.HasValue)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await TryStopViaWindowsElevatedHelperAsync(force: false);
            }

            return true;
        }

        var pid = _elevatedPid.Value;
        await RunElevatedStopCommandAsync(pid, force: false);
        await Task.Delay(500);

        if (await IsProcessRunningAsync(pid))
        {
            LogReceived?.Invoke(this, $"[WARN] sing-box process {pid} did not exit on TERM, trying force kill...");
            await RunElevatedStopCommandAsync(pid, force: true);
            await Task.Delay(500);
        }

        var stillRunning = await IsProcessRunningAsync(pid);
        if (stillRunning)
        {
            LogReceived?.Invoke(this, $"[ERROR] sing-box process {pid} is still running after stop attempts");
            return false;
        }

        return true;
    }

    private async Task<ElevatedStartResult> StartElevatedProcessForCurrentPlatformAsync(string configPath, string logPath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return await StartElevatedOnMacAsync(configPath, logPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await StartElevatedOnLinuxAsync(configPath, logPath);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return await StartElevatedOnWindowsAsync(configPath, logPath);
        }

        return new ElevatedStartResult
        {
            Success = false,
            ErrorMessage = "Unsupported OS for elevated TUN startup"
        };
    }

    private async Task RunElevatedStopCommandAsync(int pid, bool force)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var signal = force ? "KILL" : "TERM";
            await RunAppleScriptAdminCommandAsync($"kill -{signal} {pid}", MacTunPermissionPrompt);
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var signal = force ? "-KILL" : "-TERM";
            await RunPkexecCommandAsync($"kill {signal} {pid}");
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (await TryStopViaWindowsElevatedHelperAsync(force))
            {
                return;
            }

            var taskkillArgs = force ? $"/PID {pid} /T /F" : $"/PID {pid} /T";
            await RunWindowsUacCommandAsync(
                "$p = Start-Process -FilePath 'taskkill.exe' -ArgumentList " +
                $"'{EscapeForPowerShellSingleQuoted(taskkillArgs)}' " +
                "-Verb RunAs -WindowStyle Hidden -Wait -PassThru; " +
                "if ($p) { $p.ExitCode }");
        }
    }

    private void StartElevatedLogTail(string logPath)
    {
        _elevatedLogCts?.Cancel();
        _elevatedLogCts?.Dispose();
        _elevatedLogCts = new CancellationTokenSource();
        _elevatedLogTask = Task.Run(() => TailLogAsync(logPath, _elevatedLogCts.Token));
    }

    private async Task StopElevatedLogTailAsync()
    {
        if (_elevatedLogCts == null)
        {
            return;
        }

        _elevatedLogCts.Cancel();
        if (_elevatedLogTask != null)
        {
            try
            {
                await _elevatedLogTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _elevatedLogTask = null;
        _elevatedLogCts.Dispose();
        _elevatedLogCts = null;
    }

    private async Task TailLogAsync(string logPath, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    LogReceived?.Invoke(this, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Elevated log tail stopped: {ex.Message}");
        }
    }

    private async Task<bool> IsProcessRunningAsync(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "tasklist",
                    Arguments = $"/FI \"PID eq {pid}\" /FO CSV /NH",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 &&
                   output.Contains($"\"{pid}\"", StringComparison.OrdinalIgnoreCase);
        }

        using var unixProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ps",
                Arguments = $"-p {pid} -o pid=",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        unixProcess.Start();
        var unixOutput = await unixProcess.StandardOutput.ReadToEndAsync();
        await unixProcess.WaitForExitAsync();
        return unixProcess.ExitCode == 0 && !string.IsNullOrWhiteSpace(unixOutput);
    }

    private async Task<bool> IsApiReachableAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var response = await _httpClient.GetAsync($"{_apiAddress}/traffic", cts.Token);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            return response.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden
                or System.Net.HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    private async Task<int?> TryFindProcessPidByApiPortAsync()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "netstat",
                        Arguments = "-ano -p tcp",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (!line.Contains($":{_apiPort}", StringComparison.Ordinal) ||
                        !line.Contains("LISTEN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && int.TryParse(parts[^1], out var pid) && pid > 0)
                    {
                        return pid;
                    }
                }

                return null;
            }

            using var unixProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "lsof",
                    Arguments = $"-nP -iTCP:{_apiPort} -sTCP:LISTEN -t",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            unixProcess.Start();
            var pidOutput = (await unixProcess.StandardOutput.ReadToEndAsync()).Trim();
            await unixProcess.WaitForExitAsync();

            var firstLine = pidOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(firstLine, out var unixPid) && unixPid > 0)
            {
                return unixPid;
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<string> ReadRecentLogLinesAsync(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var lines = await File.ReadAllLinesAsync(path);
            if (lines.Length == 0)
            {
                return string.Empty;
            }

            return string.Join(" | ", lines.TakeLast(Math.Max(1, maxLines)));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string QuoteShellArg(string value)
    {
        return $"'{value.Replace("'", "'\"'\"'")}'";
    }

    private static string EscapeForPowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static string EscapeForAppleScript(string value)
    {
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private async Task<ElevatedStartResult> StartElevatedOnMacAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";
        var appleScript =
            $"do shell script \"{EscapeForAppleScript(shellCommand)}\" with prompt \"{EscapeForAppleScript(MacTunPermissionPrompt)}\" with administrator privileges";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "Administrator permission denied or failed" : error
            };
        }

        if (!int.TryParse(output, out var pid))
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse elevated PID: '{output}'"
            };
        }

        return new ElevatedStartResult { Success = true, Pid = pid };
    }

    private async Task<ElevatedStartResult> StartElevatedOnLinuxAsync(string configPath, string logPath)
    {
        var shellCommand =
            $"{QuoteShellArg(_singBoxPath)} run -c {QuoteShellArg(configPath)} < /dev/null >> {QuoteShellArg(logPath)} 2>&1 & echo $!";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"/bin/sh -lc {QuoteShellArg(shellCommand)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"pkexec unavailable: {ex.Message}"
            };
        }

        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error)
                    ? "Root permission denied or pkexec failed"
                    : error
            };
        }

        if (!int.TryParse(output, out var pid))
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to parse elevated PID: '{output}'"
            };
        }

        return new ElevatedStartResult { Success = true, Pid = pid };
    }

    private async Task<ElevatedStartResult> StartElevatedOnWindowsAsync(string configPath, string logPath)
    {
        if (!await EnsureWindowsElevatedHelperRunningAsync())
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = "Failed to start elevated helper"
            };
        }

        try
        {
            var request = new WindowsHelperStartRequest
            {
                SingBoxPath = _singBoxPath,
                ConfigPath = configPath,
                WorkingDirectory = _workingDirectory,
                LogPath = logPath
            };

            using var message = new HttpRequestMessage(HttpMethod.Post, GetWindowsHelperUri("start"))
            {
                Content = new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")
            };
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken!);

            using var response = await _httpClient.SendAsync(message);
            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            if (!response.IsSuccessStatusCode)
            {
                return new ElevatedStartResult
                {
                    Success = false,
                    ErrorMessage = string.IsNullOrWhiteSpace(payload)
                        ? "Elevated helper start failed"
                        : payload
                };
            }

            WindowsHelperActionResponse? result = null;
            try
            {
                result = JsonConvert.DeserializeObject<WindowsHelperActionResponse>(payload);
            }
            catch
            {
            }

            if (result is not { Success: true } || !result.Pid.HasValue || result.Pid.Value <= 0)
            {
                return new ElevatedStartResult
                {
                    Success = false,
                    ErrorMessage = !string.IsNullOrWhiteSpace(result?.Error)
                        ? result.Error
                        : $"Invalid elevated helper response: {payload}"
                };
            }

            return new ElevatedStartResult
            {
                Success = true,
                Pid = result.Pid.Value
            };
        }
        catch (Exception ex)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = $"Failed to start sing-box via elevated helper: {ex.Message}"
            };
        }
    }

    private static async Task<ElevatedStartResult> RunWindowsUacCommandAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var error = (await process.StandardError.ReadToEndAsync()).Trim();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            return new ElevatedStartResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(error) ? "UAC denied or failed" : error
            };
        }

        return new ElevatedStartResult { Success = true, RawOutput = output };
    }

    private string GetWindowsHelperUri(string path)
    {
        return $"http://127.0.0.1:{WindowsElevatedHelperPort}/{path}";
    }

    private async Task<bool> EnsureWindowsElevatedHelperRunningAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_windowsElevatedHelperToken) &&
            await PingWindowsElevatedHelperAsync(_windowsElevatedHelperToken))
        {
            return true;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            LogReceived?.Invoke(this, "[ERROR] Unable to resolve current executable path for elevated helper");
            return false;
        }

        var token = Guid.NewGuid().ToString("N");
        var parentPid = Environment.ProcessId;
        var helperArgs =
            $"{WindowsElevatedHelperArg} --port {WindowsElevatedHelperPort} --token {token} --parent-pid {parentPid}";
        var script =
            "$p = Start-Process -FilePath " +
            $"'{EscapeForPowerShellSingleQuoted(executablePath)}' " +
            "-ArgumentList " +
            $"'{EscapeForPowerShellSingleQuoted(helperArgs)}' " +
            $"-WorkingDirectory '{EscapeForPowerShellSingleQuoted(_workingDirectory)}' " +
            "-Verb RunAs -WindowStyle Hidden -PassThru; " +
            "$p.Id";

        var result = await RunWindowsUacCommandAsync(script);
        if (!result.Success)
        {
            LogReceived?.Invoke(this, $"[ERROR] {result.ErrorMessage ?? "Failed to start elevated helper"}");
            return false;
        }

        if (int.TryParse(result.RawOutput, out var helperPid) && helperPid > 0)
        {
            _windowsElevatedHelperPid = helperPid;
        }

        for (var i = 0; i < 30; i++)
        {
            if (await PingWindowsElevatedHelperAsync(token))
            {
                _windowsElevatedHelperToken = token;
                LogReceived?.Invoke(this, "[INFO] Elevated helper ready");
                return true;
            }

            await Task.Delay(200);
        }

        LogReceived?.Invoke(this, "[ERROR] Elevated helper did not become ready in time");
        return false;
    }

    private async Task<bool> PingWindowsElevatedHelperAsync(string token)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var message = new HttpRequestMessage(HttpMethod.Get, GetWindowsHelperUri("ping"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, token);
            using var response = await _httpClient.SendAsync(message, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            return string.Equals(payload, token, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> TryStopViaWindowsElevatedHelperAsync(bool force)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            return false;
        }

        try
        {
            using var message = new HttpRequestMessage(
                HttpMethod.Post,
                GetWindowsHelperUri(force ? "stop?force=1" : "stop"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken);
            using var response = await _httpClient.SendAsync(message);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var payload = (await response.Content.ReadAsStringAsync()).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return true;
            }

            WindowsHelperActionResponse? result = null;
            try
            {
                result = JsonConvert.DeserializeObject<WindowsHelperActionResponse>(payload);
            }
            catch
            {
            }

            return result == null || result.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task TryShutdownWindowsElevatedHelperAsync()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            string.IsNullOrWhiteSpace(_windowsElevatedHelperToken))
        {
            return;
        }

        var shutdownSucceeded = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var message = new HttpRequestMessage(HttpMethod.Post, GetWindowsHelperUri("shutdown"));
            message.Headers.Add(WindowsElevatedHelperTokenHeader, _windowsElevatedHelperToken);
            using var response = await _httpClient.SendAsync(message, cts.Token);
            shutdownSucceeded = response.IsSuccessStatusCode;
        }
        catch
        {
        }
        finally
        {
            if (shutdownSucceeded)
            {
                _windowsElevatedHelperToken = null;
                _windowsElevatedHelperPid = null;
            }
        }
    }

    private void TryShutdownWindowsHelperWithoutThrow()
    {
        try
        {
            TryShutdownWindowsElevatedHelperAsync().GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    private static async Task RunAppleScriptAdminCommandAsync(string shellCommand, string? prompt = null)
    {
        var promptClause = string.IsNullOrWhiteSpace(prompt)
            ? string.Empty
            : $" with prompt \"{EscapeForAppleScript(prompt)}\"";
        var appleScript = $"do shell script \"{EscapeForAppleScript(shellCommand)}\"{promptClause} with administrator privileges";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e \"{EscapeForAppleScript(appleScript)}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
    }

    private static async Task RunPkexecCommandAsync(string shellCommand)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "pkexec",
                Arguments = $"/bin/sh -lc {QuoteShellArg(shellCommand)}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync();
    }

    private void OnCurrentProcessExit(object? sender, EventArgs e)
    {
        TryForceStopWithoutThrow();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        TryForceStopWithoutThrow();
    }

    private void TryForceStopWithoutThrow()
    {
        try
        {
            _process?.Kill(true);
        }
        catch
        {
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                TryStopViaWindowsElevatedHelperAsync(force: true).GetAwaiter().GetResult();
                TryShutdownWindowsHelperWithoutThrow();
            }
        }
        catch
        {
        }

        try
        {
            if (_elevatedPid.HasValue && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = $"/PID {_elevatedPid.Value} /T /F",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                process.WaitForExit(3000);
            }
        }
        catch
        {
        }
    }

    private void TryInitializeWindowsProcessJob()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            _windowsJobHandle = CreateJobObject(IntPtr.Zero, null);
            if (_windowsJobHandle == IntPtr.Zero)
            {
                var code = Marshal.GetLastWin32Error();
                LogReceived?.Invoke(this, $"[WARN] Failed to create Windows job object: {code}");
                return;
            }

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JobObjectLimitKillOnJobClose
                }
            };

            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                var success = SetInformationJobObject(
                    _windowsJobHandle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    ptr,
                    (uint)length);
                if (!success)
                {
                    var code = Marshal.GetLastWin32Error();
                    LogReceived?.Invoke(this, $"[WARN] Failed to configure Windows job object: {code}");
                    CloseHandle(_windowsJobHandle);
                    _windowsJobHandle = IntPtr.Zero;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Failed to initialize Windows job object: {ex.Message}");
            if (_windowsJobHandle != IntPtr.Zero)
            {
                CloseHandle(_windowsJobHandle);
                _windowsJobHandle = IntPtr.Zero;
            }
        }
    }

    private void TryAttachProcessToWindowsJob(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            _windowsJobHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var success = AssignProcessToJobObject(_windowsJobHandle, process.Handle);
            if (!success)
            {
                var code = Marshal.GetLastWin32Error();
                LogReceived?.Invoke(this, $"[WARN] Failed to attach sing-box to Windows job object: {code}");
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Failed to attach sing-box to Windows job object: {ex.Message}");
        }
    }

    private void TryAttachProcessIdToWindowsJob(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ||
            _windowsJobHandle == IntPtr.Zero ||
            pid <= 0)
        {
            return;
        }

        if (!IsCurrentProcessElevatedOnWindows())
        {
            return;
        }

        IntPtr processHandle = IntPtr.Zero;
        try
        {
            processHandle = OpenProcess(ProcessSetQuota | ProcessTerminate, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                var openCode = Marshal.GetLastWin32Error();
                if (openCode == 5)
                {
                    return;
                }
                LogReceived?.Invoke(this, $"[WARN] Failed to open sing-box process {pid} for job object attach: {openCode}");
                return;
            }

            var success = AssignProcessToJobObject(_windowsJobHandle, processHandle);
            if (!success)
            {
                var code = Marshal.GetLastWin32Error();
                LogReceived?.Invoke(this, $"[WARN] Failed to attach sing-box process {pid} to Windows job object: {code}");
            }
        }
        catch (Exception ex)
        {
            LogReceived?.Invoke(this, $"[WARN] Failed to attach sing-box process {pid} to Windows job object: {ex.Message}");
        }
        finally
        {
            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }
        }
    }

    private static bool IsCurrentProcessElevatedOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private sealed class WindowsHelperStartRequest
    {
        public string SingBoxPath { get; init; } = string.Empty;
        public string ConfigPath { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
        public string LogPath { get; init; } = string.Empty;
    }

    private sealed class WindowsHelperActionResponse
    {
        public bool Success { get; init; }
        public int? Pid { get; init; }
        public string? Error { get; init; }
    }

    private sealed class ElevatedStartResult
    {
        public bool Success { get; init; }
        public int? Pid { get; init; }
        public string? ErrorMessage { get; init; }
        public string RawOutput { get; init; } = string.Empty;
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public IntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType infoType,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessSetQuota = 0x0100;
}



