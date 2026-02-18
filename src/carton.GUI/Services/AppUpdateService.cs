using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace carton.GUI.Services;

public interface IAppUpdateService
{
    string CurrentVersion { get; }

    bool IsUpdatePendingRestart { get; }

    Task<AppUpdateResult?> CheckForUpdatesAsync(
        string channel,
        bool includePrerelease,
        CancellationToken cancellationToken = default);

    Task DownloadUpdateAsync(
        AppUpdateResult update,
        string channel,
        bool includePrerelease,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    Task RestartToApplyDownloadedUpdateAsync(bool silentRestart = false);
}

public sealed record AppUpdateResult(
    string Version,
    string? ReleaseNotesMarkdown,
    string Channel,
    UpdateInfo UpdateInfo);

public sealed class AppUpdateService : IAppUpdateService
{
    private readonly string _repositoryUrl;
    private readonly string? _token;
    private readonly Action<string>? _log;
    private readonly Lazy<IVelopackLocator> _locator;

    private VelopackAsset? _stagedRelease;
    private string? _stagedChannel;
    private bool _stagedIncludesPrerelease;

    public AppUpdateService(string repositoryUrl, string? token = null, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("Repository URL must be provided", nameof(repositoryUrl));
        }

        _repositoryUrl = repositoryUrl;
        _token = token;
        _log = log;
        _locator = new Lazy<IVelopackLocator>(() =>
            VelopackLocator.Current ?? VelopackLocator.CreateDefaultForPlatform());

        var version = Assembly.GetEntryAssembly()?.GetName().Version ??
                      Assembly.GetExecutingAssembly().GetName().Version;
        CurrentVersion = version?.ToString() ?? "0.0.0";
    }

    public string CurrentVersion { get; }

    public bool IsUpdatePendingRestart
    {
        get
        {
            using var manager = CreateManager(_stagedChannel, _stagedIncludesPrerelease);
            return manager.IsUpdatePendingRestart;
        }
    }

    public async Task<AppUpdateResult?> CheckForUpdatesAsync(
        string channel,
        bool includePrerelease,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var manager = CreateManager(channel, includePrerelease);
        Log($"Checking for updates (channel={channel})");

        var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (info == null || info.TargetFullRelease == null)
        {
            _stagedRelease = manager.UpdatePendingRestart;
            return null;
        }

        var version = info.TargetFullRelease.Version?.ToString() ?? "unknown";
        return new AppUpdateResult(version, info.TargetFullRelease.NotesMarkdown, channel, info);
    }

    public async Task DownloadUpdateAsync(
        AppUpdateResult update,
        string channel,
        bool includePrerelease,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (update == null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        using var manager = CreateManager(channel, includePrerelease);
        Log($"Downloading update {update.Version} (channel={channel})");

        await manager.DownloadUpdatesAsync(
            update.UpdateInfo,
            percent => progress?.Report(percent),
            cancellationToken).ConfigureAwait(false);

        _stagedRelease = update.UpdateInfo.TargetFullRelease;
        _stagedChannel = channel;
        _stagedIncludesPrerelease = includePrerelease;
    }

    public async Task RestartToApplyDownloadedUpdateAsync(bool silentRestart = false)
    {
        if (_stagedRelease == null)
        {
            using var manager = CreateManager(_stagedChannel, _stagedIncludesPrerelease);
            _stagedRelease = manager.UpdatePendingRestart;
        }

        if (_stagedRelease == null)
        {
            throw new InvalidOperationException("No downloaded update is ready to apply.");
        }

        Log($"Applying update {_stagedRelease.Version} (restart={true})");
        using var updater = CreateManager(_stagedChannel, _stagedIncludesPrerelease);
        await updater.WaitExitThenApplyUpdatesAsync(
            _stagedRelease,
            true,
            silentRestart,
            Array.Empty<string>()).ConfigureAwait(false);
    }

    private UpdateManager CreateManager(string? channel, bool includePrerelease)
    {
        var normalizedChannel = string.IsNullOrWhiteSpace(channel)
            ? null
            : channel.Trim();

        var options = new UpdateOptions
        {
            ExplicitChannel = normalizedChannel,
            AllowVersionDowngrade = false
        };

        var source = new GithubSource(_repositoryUrl, _token ?? string.Empty, includePrerelease, null);
        return new UpdateManager(source, options, _locator.Value);
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}
