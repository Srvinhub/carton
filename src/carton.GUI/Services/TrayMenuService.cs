using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using carton.Core.Models;
using carton.ViewModels;
using carton.Views;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading.Tasks;

namespace carton.GUI.Services;

public sealed class TrayMenuService : IDisposable
{
    private readonly ILocalizationService _localizationService;
    private Application? _application;
    private IClassicDesktopStyleApplicationLifetime? _desktopLifetime;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private DashboardViewModel? _dashboardViewModel;
    private GroupsViewModel? _groupsViewModel;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _openWindowItem;
    private NativeMenuItem? _startStopItem;
    private NativeMenuItem? _profilesMenuItem;
    private NativeMenuItem? _groupsMenuItem;
    private readonly Dictionary<GroupItemViewModel, NotifyCollectionChangedEventHandler> _groupItemsHandlers = new();
    private readonly object _menuRefreshGate = new();
    private bool _isProfilesMenuRefreshQueued;
    private bool _isGroupsMenuRefreshQueued;
    private int _profilesMenuRenderedHash = int.MinValue;
    private int _profilesMenuRequestedHash = int.MinValue;
    private int _groupsMenuRenderedHash = int.MinValue;
    private int _groupsMenuRequestedHash = int.MinValue;
    private bool _isInitialized;

    public TrayMenuService()
    {
        _localizationService = LocalizationService.Instance;
    }

    public void Initialize(Application application,
        IClassicDesktopStyleApplicationLifetime desktopLifetime,
        MainWindow window,
        MainViewModel mainViewModel)
    {
        if (_isInitialized)
        {
            return;
        }

        _application = application ?? throw new ArgumentNullException(nameof(application));
        _desktopLifetime = desktopLifetime ?? throw new ArgumentNullException(nameof(desktopLifetime));
        _mainWindow = window ?? throw new ArgumentNullException(nameof(window));
        _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel));
        _dashboardViewModel = mainViewModel.DashboardViewModel;
        _groupsViewModel = mainViewModel.GroupsViewModel;

        _localizationService.LanguageChanged += OnLanguageChanged;
        _mainViewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        _dashboardViewModel.AvailableProfiles.CollectionChanged += OnProfilesCollectionChanged;
        SubscribeToProfileItems(_dashboardViewModel.AvailableProfiles);

        _groupsViewModel.Groups.CollectionChanged += OnGroupsCollectionChanged;
        foreach (var group in _groupsViewModel.Groups)
        {
            AttachGroupHandlers(group);
        }

        CreateTrayIcon();
        _isInitialized = true;
    }

    public void Dispose()
    {
        if (!_isInitialized)
        {
            return;
        }

        _isInitialized = false;
        _localizationService.LanguageChanged -= OnLanguageChanged;

        if (_mainViewModel != null)
        {
            _mainViewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        if (_dashboardViewModel != null)
        {
            _dashboardViewModel.AvailableProfiles.CollectionChanged -= OnProfilesCollectionChanged;
            foreach (var profile in _dashboardViewModel.AvailableProfiles)
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
            }
        }

        if (_groupsViewModel != null)
        {
            _groupsViewModel.Groups.CollectionChanged -= OnGroupsCollectionChanged;
            foreach (var group in _groupsViewModel.Groups)
            {
                DetachGroupHandlers(group);
            }
        }

        if (_trayIcon != null)
        {
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon.Menu = null;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        if (_application != null)
        {
            TrayIcon.SetIcons(_application, new TrayIcons());
        }
    }

    private void CreateTrayIcon()
    {
        var menu = BuildInitialMenu();
        var assemblyName = _application?.GetType().Assembly.GetName().Name ?? "carton";
        var iconUri = new Uri($"avares://{assemblyName}/Assets/carton_icon.png");
        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(iconUri)),
            ToolTipText = _localizationService["App.Name"],
            Menu = menu
        };

        icon.Clicked += OnTrayIconClicked;
        TrayIcon.SetIcons(_application!, new TrayIcons { icon });
        _trayIcon = icon;
        UpdateMenuState();
    }

    private NativeMenu BuildInitialMenu()
    {
        _openWindowItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.ShowWindow"]
        };
        _openWindowItem.Click += (_, _) => ShowMainWindow();

        _startStopItem = new NativeMenuItem();
        _startStopItem.Click += async (_, _) => await ToggleServiceAsync();

        _profilesMenuItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Profiles"],
            Menu = new NativeMenu()
        };

        _groupsMenuItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Groups"],
            Menu = new NativeMenu()
        };

        var quitItem = new NativeMenuItem
        {
            Header = _localizationService["Tray.Quit"]
        };
        quitItem.Click += (_, _) => ExitApplication();

        var menu = new NativeMenu();
        menu.Items.Add(_openWindowItem);
        menu.Items.Add(_startStopItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_profilesMenuItem);
        menu.Items.Add(_groupsMenuItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);
        return menu;
    }

    private void ExitApplication()
    {
        RunOnUiThread(() =>
        {
            _mainWindow?.AllowClose();
            _mainWindow?.Close();
            //_desktopLifetime?.Shutdown();
            SingleInstanceService.Dispose();
            Environment.Exit(0);
        });
    }

    private void UpdateMenuState()
    {
        UpdateStartStopItem();
        RefreshProfilesMenu();
        RefreshGroupsMenu();
    }

    private void UpdateStartStopItem()
    {
        if (_startStopItem == null || _mainViewModel == null)
        {
            return;
        }

        var isRunning = _mainViewModel.IsConnected;
        _startStopItem.Header = _localizationService[isRunning ? "Tray.Stop" : "Tray.Start"];
        _startStopItem.IsEnabled = true;
    }

    private void RefreshProfilesMenu()
    {
        if (_profilesMenuItem == null || _dashboardViewModel == null)
        {
            return;
        }

        var stateHash = ComputeProfilesMenuStateHash();
        lock (_menuRefreshGate)
        {
            if (stateHash == _profilesMenuRenderedHash || stateHash == _profilesMenuRequestedHash)
            {
                return;
            }

            _profilesMenuRequestedHash = stateHash;
            if (_isProfilesMenuRefreshQueued)
            {
                return;
            }

            _isProfilesMenuRefreshQueued = true;
        }

        void Update()
        {
            lock (_menuRefreshGate)
            {
                _isProfilesMenuRefreshQueued = false;
            }

            var currentHash = ComputeProfilesMenuStateHash();
            lock (_menuRefreshGate)
            {
                if (currentHash == _profilesMenuRenderedHash)
                {
                    return;
                }

                _profilesMenuRequestedHash = currentHash;
            }

            var menu = _profilesMenuItem.Menu ?? new NativeMenu();
            menu.Items.Clear();
            if (_dashboardViewModel.AvailableProfiles.Count == 0)
            {
                menu.Items.Add(new NativeMenuItem
                {
                    Header = _localizationService["Tray.Profiles.Empty"],
                    IsEnabled = false
                });
            }
            else
            {
                foreach (var profile in _dashboardViewModel.AvailableProfiles)
                {
                    var item = new NativeMenuItem
                    {
                        Header = profile.Name,
                        IsChecked = profile.IsSelected,
                        ToggleType = NativeMenuItemToggleType.Radio
                    };
                    item.Click += (_, _) => SelectProfile(profile);
                    menu.Items.Add(item);
                }
            }

            if (_profilesMenuItem.Menu != menu)
            {
                _profilesMenuItem.Menu = menu;
            }

            lock (_menuRefreshGate)
            {
                _profilesMenuRenderedHash = currentHash;
            }
        }

        RunOnUiThread(Update);
    }

    private void RefreshGroupsMenu()
    {
        if (_groupsMenuItem == null || _groupsViewModel == null)
        {
            return;
        }

        var stateHash = ComputeGroupsMenuStateHash();
        lock (_menuRefreshGate)
        {
            if (stateHash == _groupsMenuRenderedHash || stateHash == _groupsMenuRequestedHash)
            {
                return;
            }

            _groupsMenuRequestedHash = stateHash;
            if (_isGroupsMenuRefreshQueued)
            {
                return;
            }

            _isGroupsMenuRefreshQueued = true;
        }

        void Update()
        {
            lock (_menuRefreshGate)
            {
                _isGroupsMenuRefreshQueued = false;
            }

            var currentHash = ComputeGroupsMenuStateHash();
            lock (_menuRefreshGate)
            {
                if (currentHash == _groupsMenuRenderedHash)
                {
                    return;
                }

                _groupsMenuRequestedHash = currentHash;
            }

            var menu = _groupsMenuItem.Menu ?? new NativeMenu();
            menu.Items.Clear();
            var hasGroups = _groupsViewModel.Groups.Count > 0 && _mainViewModel?.IsConnected == true;

            if (!hasGroups)
            {
                menu.Items.Add(new NativeMenuItem
                {
                    Header = _localizationService[_mainViewModel?.IsConnected == true
                        ? "Tray.Groups.Empty"
                        : "Tray.Groups.Unavailable"],
                    IsEnabled = false
                });
                _groupsMenuItem.Menu = menu;
                _groupsMenuItem.IsEnabled = _mainViewModel?.IsConnected == true;
                lock (_menuRefreshGate)
                {
                    _groupsMenuRenderedHash = currentHash;
                }
                return;
            }

            foreach (var group in _groupsViewModel.Groups)
            {
                var groupMenu = new NativeMenu();
                foreach (var outbound in group.Items)
                {
                    var outboundHeader = string.IsNullOrWhiteSpace(outbound.Tag)
                        ? _localizationService["Common.Unknown"]
                        : outbound.Tag;
                    var delaySuffix = outbound.Delay > 0 ? $" ({outbound.Delay}ms)" : string.Empty;
                    var outboundItem = new NativeMenuItem
                    {
                        Header = outboundHeader + delaySuffix,
                        IsChecked = outbound.IsSelected,
                        ToggleType = NativeMenuItemToggleType.CheckBox
                    };
                    outboundItem.Click += (_, _) => SelectOutbound(group, outbound.Tag);
                    groupMenu.Items.Add(outboundItem);
                }

                if (groupMenu.Items.Count == 0)
                {
                    groupMenu.Items.Add(new NativeMenuItem
                    {
                        Header = _localizationService["Tray.Groups.Empty"],
                        IsEnabled = false
                    });
                }

                var groupHeader = string.IsNullOrWhiteSpace(group.Name)
                    ? _localizationService["Common.Unknown"]
                    : group.Name;

                menu.Items.Add(new NativeMenuItem
                {
                    Header = groupHeader,
                    Menu = groupMenu
                });
            }

            if (_groupsMenuItem.Menu != menu)
            {
                _groupsMenuItem.Menu = menu;
            }
            _groupsMenuItem.IsEnabled = true;

            lock (_menuRefreshGate)
            {
                _groupsMenuRenderedHash = currentHash;
            }
        }

        RunOnUiThread(Update);
    }

    private int ComputeProfilesMenuStateHash()
    {
        if (_dashboardViewModel == null)
        {
            return 0;
        }

        var hash = new HashCode();
        hash.Add(_dashboardViewModel.AvailableProfiles.Count);
        foreach (var profile in _dashboardViewModel.AvailableProfiles)
        {
            hash.Add(profile.Name, StringComparer.Ordinal);
            hash.Add(profile.IsSelected);
        }

        return hash.ToHashCode();
    }

    private int ComputeGroupsMenuStateHash()
    {
        if (_groupsViewModel == null)
        {
            return 0;
        }

        var hash = new HashCode();
        hash.Add(_mainViewModel?.IsConnected == true);
        hash.Add(_groupsViewModel.Groups.Count);
        foreach (var group in _groupsViewModel.Groups)
        {
            hash.Add(group.Name, StringComparer.Ordinal);
            hash.Add(group.SelectedOutbound, StringComparer.Ordinal);

            if (group.Items == null)
            {
                hash.Add(0);
                continue;
            }

            hash.Add(group.Items.Count);
            foreach (var outbound in group.Items)
            {
                hash.Add(outbound.Tag, StringComparer.Ordinal);
                hash.Add(outbound.Delay);
                hash.Add(outbound.IsSelected);
            }
        }

        return hash.ToHashCode();
    }

    private void SelectProfile(DashboardProfileItemViewModel profile)
    {
        if (_dashboardViewModel?.SelectStartupProfileCommand == null)
        {
            return;
        }

        RunOnUiThread(() => _ = _dashboardViewModel.SelectStartupProfileCommand.ExecuteAsync(profile));
    }

    private void SelectOutbound(GroupItemViewModel group, string? outboundTag)
    {
        if (string.IsNullOrWhiteSpace(outboundTag))
        {
            return;
        }

        var command = group.SelectOutboundCommand;
        if (command == null)
        {
            return;
        }

        RunOnUiThread(() => _ = command.ExecuteAsync(outboundTag));
    }

    private async Task ToggleServiceAsync()
    {
        if (_dashboardViewModel == null)
        {
            return;
        }

        var startCommand = _dashboardViewModel.StartWithSelectedProfileCommand;
        var stopCommand = _dashboardViewModel.StopConnectionCommand;

        if (_mainViewModel?.IsConnected == true)
        {
            if (stopCommand != null)
            {
                RunOnUiThread(() => _ = stopCommand.ExecuteAsync(null));
            }
        }
        else if (startCommand != null)
        {
            RunOnUiThread(() => _ = startCommand.ExecuteAsync(null));
        }

        await Task.CompletedTask;
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsConnected))
        {
            UpdateStartStopItem();
            if (_mainViewModel?.IsConnected == true)
            {
                _groupsViewModel?.OnNavigatedTo();
            }
            RefreshGroupsMenu();
        }
        else if (e.PropertyName == nameof(MainViewModel.CurrentProfileName))
        {
            RefreshProfilesMenu();
        }
    }

    private void OnProfilesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DashboardProfileItemViewModel profile in e.OldItems)
            {
                profile.PropertyChanged -= OnProfilePropertyChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (DashboardProfileItemViewModel profile in e.NewItems)
            {
                profile.PropertyChanged += OnProfilePropertyChanged;
            }
        }

        RefreshProfilesMenu();
    }

    private void SubscribeToProfileItems(IEnumerable<DashboardProfileItemViewModel> profiles)
    {
        foreach (var profile in profiles)
        {
            profile.PropertyChanged += OnProfilePropertyChanged;
        }
    }

    private void OnProfilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardProfileItemViewModel.IsSelected) ||
            e.PropertyName == nameof(DashboardProfileItemViewModel.Name))
        {
            RefreshProfilesMenu();
        }
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (GroupItemViewModel group in e.OldItems)
            {
                DetachGroupHandlers(group);
            }
        }

        if (e.NewItems != null)
        {
            foreach (GroupItemViewModel group in e.NewItems)
            {
                AttachGroupHandlers(group);
            }
        }

        RefreshGroupsMenu();
    }

    private void AttachGroupHandlers(GroupItemViewModel group)
    {
        group.PropertyChanged += OnGroupPropertyChanged;
        if (group.Items != null)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => RefreshGroupsMenu();
            group.Items.CollectionChanged += handler;
            _groupItemsHandlers[group] = handler;
        }
    }

    private void DetachGroupHandlers(GroupItemViewModel group)
    {
        group.PropertyChanged -= OnGroupPropertyChanged;
        if (_groupItemsHandlers.TryGetValue(group, out var handler) && group.Items != null)
        {
            group.Items.CollectionChanged -= handler;
            _groupItemsHandlers.Remove(group);
        }
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GroupItemViewModel.Items) && sender is GroupItemViewModel group)
        {
            DetachGroupHandlers(group);
            AttachGroupHandlers(group);
        }

        if (e.PropertyName == nameof(GroupItemViewModel.SelectedOutbound) ||
            e.PropertyName == nameof(GroupItemViewModel.Name) ||
            e.PropertyName == nameof(GroupItemViewModel.Items))
        {
            RefreshGroupsMenu();
        }
    }

    private void OnLanguageChanged(object? sender, AppLanguage e)
    {
        RunOnUiThread(() =>
        {
            lock (_menuRefreshGate)
            {
                _profilesMenuRenderedHash = int.MinValue;
                _profilesMenuRequestedHash = int.MinValue;
                _groupsMenuRenderedHash = int.MinValue;
                _groupsMenuRequestedHash = int.MinValue;
            }

            if (_trayIcon != null)
            {
                _trayIcon.ToolTipText = _localizationService["App.Name"];
            }

            if (_openWindowItem != null)
            {
                _openWindowItem.Header = _localizationService["Tray.ShowWindow"];
            }

            if (_profilesMenuItem != null)
            {
                _profilesMenuItem.Header = _localizationService["Tray.Profiles"];
            }

            if (_groupsMenuItem != null)
            {
                _groupsMenuItem.Header = _localizationService["Tray.Groups"];
            }

            UpdateStartStopItem();
            RefreshProfilesMenu();
            RefreshGroupsMenu();
        });
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (!_mainWindow.IsVisible)
            {
                _mainWindow.Show();
            }

            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }

            _mainWindow.Activate();
            _mainWindow.Topmost = true;
            _mainWindow.Topmost = false;
        });
    }

    private void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
