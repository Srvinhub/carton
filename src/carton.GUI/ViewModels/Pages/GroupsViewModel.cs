using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.Core.Services;
using carton.Core.Models;
using carton.GUI.Models;
using carton.GUI.Services;

namespace carton.ViewModels;

public partial class GroupsViewModel : PageViewModelBase
{
    private const string WaitingForSingBoxResourceKey = "Groups.Status.WaitingForSingBoxStart";
    private readonly ISingBoxManager? _singBoxManager;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);

    public override NavigationPage PageType => NavigationPage.Groups;

    [ObservableProperty]
    private ObservableCollection<GroupItemViewModel> _groups = new();

    [ObservableProperty]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestingGroup;

    [ObservableProperty]
    private bool _showGroupTabs;

    public GroupsViewModel()
    {
        Title = "Groups";
        Icon = "Group";
        Groups.CollectionChanged += OnGroupsCollectionChanged;
        UpdateGroupTabsVisibility();
    }

    public GroupsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _singBoxManager.StatusChanged += OnServiceStatusChanged;
        _ = LoadGroupsAsync();
    }

    public void OnNavigatedTo()
    {
        if (_singBoxManager == null)
        {
            StatusMessage = "sing-box manager unavailable";
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            _ = LoadGroupsAsync();
        }
        else if (Groups.Count == 0)
        {
            StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
        }
    }
    private async Task LoadGroupsAsync()
    {
        if (_singBoxManager == null) return;

        await _loadSemaphore.WaitAsync();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = true;
                StatusMessage = "Loading groups...";
            });

            if (_singBoxManager.IsRunning == false)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Groups.Clear();
                    SelectedGroup = null;
                    StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
                    UpdateGroupTabsVisibility();
                });
                return;
            }

            var groups = await _singBoxManager.GetOutboundGroupsAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var previousSelection = SelectedGroup?.Name;
                Groups.Clear();
                GroupItemViewModel? selected = null;

                foreach (var group in groups)
                {
                    var outboundItems = group.Items
                        .Select(i => new OutboundItemViewModel
                        {
                            Tag = i.Tag,
                            Delay = i.UrlTestDelay
                        })
                        .ToList();

                    if (outboundItems.Count == 0)
                    {
                        continue;
                    }

                    var groupVm = new GroupItemViewModel
                    {
                        Name = group.Tag,
                        Type = group.Type,
                        SelectedOutbound = group.Selected,
                        Items = new ObservableCollection<OutboundItemViewModel>(outboundItems)
                    };
                    groupVm.SelectOutboundCommand = new AsyncRelayCommand<string>(
                        outboundTag => SelectOutboundAsync(group.Tag, outboundTag),
                        _ => _singBoxManager?.IsRunning == true
                    );

                    foreach (var item in groupVm.Items)
                    {
                        item.SelectOutboundCommand = groupVm.SelectOutboundCommand;
                    }
                    groupVm.UpdateItemSelection();

                    if (groupVm.Name == previousSelection)
                    {
                        selected = groupVm;
                    }

                    Groups.Add(groupVm);
                }

                SelectedGroup = selected ?? Groups.FirstOrDefault();
                StatusMessage = Groups.Count > 0
                    ? $"Loaded {Groups.Count} groups"
                    : "No groups available";
            });

            Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
            Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to load groups: {ex.Message}";
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
            _loadSemaphore.Release();
        }
    }

    private void OnServiceStatusChanged(object? sender, ServiceStatus status)
    {
        Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
        Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());

        if (status == ServiceStatus.Running)
        {
            _ = LoadGroupsAsync();
            return;
        }

        if (status is ServiceStatus.Stopped or ServiceStatus.Error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Groups.Clear();
                SelectedGroup = null;
                UpdateGroupTabsVisibility();
                StatusMessage = status == ServiceStatus.Error
                    ? "sing-box failed to start"
                    : "sing-box is not running";
            });
        }
    }

    private void OnGroupsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateGroupTabsVisibility();
    }

    private void UpdateGroupTabsVisibility()
    {
        ShowGroupTabs = Groups.Count > 1;
    }

    private void UpdateSelectOutboundCommandStates()
    {
        foreach (var group in Groups)
        {
            if (group.SelectOutboundCommand is AsyncRelayCommand<string> asyncCommand)
            {
                asyncCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task SelectOutboundAsync(string groupTag, string? outboundTag)
    {
        if (_singBoxManager == null || string.IsNullOrEmpty(outboundTag)) return;

        try
        {
            await _singBoxManager.SelectOutboundAsync(groupTag, outboundTag);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var group = Groups.FirstOrDefault(g => g.Name == groupTag);
                if (group != null)
                {
                    group.SelectedOutbound = outboundTag;
                }

                StatusMessage = $"Selected {outboundTag} for {groupTag}";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to select outbound: {ex.Message}";
            });
        }
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTestingGroupChanged(bool value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestCurrentGroup()
    {
        return _singBoxManager?.IsRunning == true && SelectedGroup != null && !IsTestingGroup;
    }

    [RelayCommand(CanExecute = nameof(CanTestCurrentGroup))]
    private async Task TestCurrentGroupAsync()
    {
        if (_singBoxManager == null || SelectedGroup == null)
        {
            return;
        }

        var targets = SelectedGroup.Items
            .Select(i => i.Tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToList();

        if (targets.Count == 0)
        {
            StatusMessage = "当前分组没有可测速的节点";
            return;
        }

        IsTestingGroup = true;
        StatusMessage = $"正在测速 {SelectedGroup.Name}...";

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(targets);
            foreach (var item in SelectedGroup.Items)
            {
                if (delays.TryGetValue(item.Tag, out var value) && value >= 0)
                {
                    item.Delay = value;
                }
                else
                {
                    item.Delay = 0;
                }
            }

            StatusMessage = $"测速完成 - {SelectedGroup.Name}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"测速失败: {ex.Message}";
        }
        finally
        {
            IsTestingGroup = false;
        }
    }
}

public partial class GroupItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _selectedOutbound = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OutboundItemViewModel> _items = new();

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    partial void OnSelectedOutboundChanged(string value)
    {
        UpdateItemSelection();
    }

    partial void OnItemsChanged(ObservableCollection<OutboundItemViewModel> value)
    {
        UpdateItemSelection();
    }

    public void UpdateItemSelection()
    {
        if (Items == null)
        {
            return;
        }

        foreach (var item in Items)
        {
            item.IsSelected = string.Equals(item.Tag, SelectedOutbound, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public partial class OutboundItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _tag = string.Empty;

    [ObservableProperty]
    private int _delay;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    [ObservableProperty]
    private bool _isSelected;
}
