using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.Core.Utilities;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class ConnectionsViewModel : PageViewModelBase
{
    private readonly ISingBoxManager? _singBoxManager;

    public override NavigationPage PageType => NavigationPage.Connections;

    [ObservableProperty]
    private ObservableCollection<ConnectionItemViewModel> _connections = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private int _connectionCount;

    private readonly DispatcherTimer? _refreshTimer;
    private bool _isRefreshing;
    private bool _isOnPage;

    public ConnectionsViewModel()
    {
        Title = "Connections";
        Icon = "Connections";
    }

    public ConnectionsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        // Timer is NOT started here — it starts when user navigates to this page
    }

    /// <summary>
    /// Called when the user navigates to the Connections page.
    /// </summary>
    public void OnNavigatedTo()
    {
        _isOnPage = true;
        if (_singBoxManager is { IsRunning: true })
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Called when the user navigates away from the Connections page.
    /// </summary>
    public void OnNavigatedFrom()
    {
        _isOnPage = false;
        _refreshTimer?.Stop();
    }

    /// <summary>
    /// Called when sing-box status changes. Starts/stops polling accordingly.
    /// </summary>
    public void OnServiceStatusChanged(bool isRunning)
    {
        if (isRunning && _isOnPage)
        {
            _refreshTimer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _refreshTimer?.Stop();
            if (!isRunning)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Connections.Clear();
                    ConnectionCount = 0;
                });
            }
        }
    }

    [RelayCommand]
    private async Task CloseAll()
    {
        if (_singBoxManager != null)
        {
            await _singBoxManager.CloseAllConnectionsAsync();
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task CloseConnection(ConnectionItemViewModel? connection)
    {
        if (_singBoxManager == null || connection == null) return;
        await _singBoxManager.CloseConnectionAsync(connection.Id);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_singBoxManager == null || _isRefreshing) return;
        _isRefreshing = true;

        var connections = await _singBoxManager.GetConnectionsAsync();

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Connections.Clear();
            foreach (var conn in connections)
            {
                Connections.Add(new ConnectionItemViewModel
                {
                    Id = conn.Id,
                    Process = FormatText(conn.Process, conn.Inbound),
                    Source = FormatText(conn.Source, conn.Ip),
                    Destination = FormatText(conn.Destination, conn.Domain),
                    Protocol = FormatText(conn.Protocol),
                    Outbound = FormatText(conn.Outbound),
                    Upload = FormatBytes(conn.Upload),
                    Download = FormatBytes(conn.Download)
                });
            }
            ConnectionCount = connections.Count;
        });

        _isRefreshing = false;
    }

    private static string FormatBytes(long bytes) => FormatHelper.FormatBytes(bytes);

    private static string FormatText(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value!;
            }
        }

        return "-";
    }
}

public partial class ConnectionItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _process = string.Empty;

    [ObservableProperty]
    private string _source = string.Empty;

    [ObservableProperty]
    private string _destination = string.Empty;

    [ObservableProperty]
    private string _protocol = string.Empty;

    [ObservableProperty]
    private string _outbound = string.Empty;

    [ObservableProperty]
    private string _upload = string.Empty;

    [ObservableProperty]
    private string _download = string.Empty;
}
