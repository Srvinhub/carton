using Avalonia.Threading;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Models;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace carton.ViewModels;

public partial class GroupsViewModel : PageViewModelBase
{
    private const string WaitingForSingBoxResourceKey = "Groups.Status.WaitingForSingBoxStart";
    private static readonly TimeSpan CacheExpirationInterval = TimeSpan.FromMinutes(1);
    private readonly ISingBoxManager? _singBoxManager;
    private readonly SemaphoreSlim _loadSemaphore = new(1, 1);
    private readonly ClashConfigCacheService _clashConfigCache;
    private readonly Dictionary<string, List<OutboundItemViewModel>> _outboundItemsByTag = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _groupExpandStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _urlTestRefreshTimer;
    private IReadOnlyList<GroupCacheSnapshot> _cachedGroups = Array.Empty<GroupCacheSnapshot>();
    private DateTimeOffset? _lastCacheRefreshAt;
    private bool _isRefreshingUrlTestGroups;
    private bool _isPageActive;
    private bool _isWindowVisible = true;

    public override NavigationPage PageType => NavigationPage.Groups;

    [ObservableProperty]
    private ObservableCollection<GroupItemViewModel> _groups = new();

    [ObservableProperty]
    private GroupItemViewModel? _selectedGroup;

    [ObservableProperty]
    private IReadOnlyList<GroupMenuSnapshot> _trayGroups = Array.Empty<GroupMenuSnapshot>();

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isTestingGroup;

    public GroupsViewModel()
    {
        Title = "Groups";
        Icon = "Group";
        _clashConfigCache = ClashConfigCacheService.Instance;
        _urlTestRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5f)
        };
        _urlTestRefreshTimer.Tick += OnUrlTestRefreshTimerTick;
    }

    public GroupsViewModel(ISingBoxManager singBoxManager) : this()
    {
        _singBoxManager = singBoxManager;
        _singBoxManager.StatusChanged += OnServiceStatusChanged;
    }

    public void OnNavigatedTo()
    {
        _isPageActive = true;
        UpdateUrlTestRefreshState();
        var isCacheExpired = IsCacheExpired(DateTimeOffset.UtcNow);

        if (_singBoxManager == null)
        {
            StatusMessage = "sing-box manager unavailable";
            return;
        }

        if (_singBoxManager.IsRunning)
        {
            if (_clashConfigCache.IsDirty || _cachedGroups.Count == 0 || isCacheExpired)
            {
                _ = LoadGroupsAsync();
            }
            else if (Groups.Count == 0)
            {
                RestoreViewGroupsFromCache();
            }
        }
        else if (Groups.Count == 0)
        {
            StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
        }
    }

    public void OnNavigatedFrom()
    {
        _isPageActive = false;
        UpdateUrlTestRefreshState();
        _groupExpandStates.Clear();
        ReleaseViewGroups(clearStatusMessage: false);
    }

    public void EnsureLoadedInBackground()
    {
        if (_singBoxManager?.IsRunning != true)
        {
            return;
        }

        if (_cachedGroups.Count == 0 || _clashConfigCache.IsDirty)
        {
            _ = LoadGroupsAsync();
        }
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        UpdateUrlTestRefreshState();
    }

    private async Task LoadGroupsAsync()
    {
        if (_singBoxManager == null)
        {
            return;
        }

        await _loadSemaphore.WaitAsync();
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = true;
                StatusMessage = "Loading groups...";
            });

            if (!_singBoxManager.IsRunning)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ReleaseViewGroups(clearStatusMessage: false);
                    _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                    TrayGroups = Array.Empty<GroupMenuSnapshot>();
                    StatusMessage = LocalizationService.Instance[WaitingForSingBoxResourceKey];
                });
                return;
            }

            var groups = await _singBoxManager.GetOutboundGroupsAsync();
            var clashConfig = _clashConfigCache.Current;
            var filteredGroups = new List<OutboundGroup>(groups.Count);
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (ShouldDisplayGroup(group, clashConfig))
                {
                    filteredGroups.Add(group);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _cachedGroups = BuildCachedGroupsFromOutboundGroups(filteredGroups);
                _lastCacheRefreshAt = DateTimeOffset.UtcNow;
                UpdateTrayGroupsFromCache();

                if (!_isPageActive)
                {
                    ReleaseViewGroups(clearStatusMessage: false);
                    StatusMessage = _cachedGroups.Count > 0
                        ? $"Loaded {_cachedGroups.Count} groups"
                        : "No groups available";
                    return;
                }

                RestoreViewGroupsFromCache();
                StatusMessage = Groups.Count > 0
                    ? $"Loaded {Groups.Count} groups"
                    : "No groups available";
            });
            _clashConfigCache.MarkClean();

            Dispatcher.UIThread.Post(UpdateSelectOutboundCommandStates);
            Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
            Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
            Dispatcher.UIThread.Post(() => TestGroupCardCommand.NotifyCanExecuteChanged());
            UpdateUrlTestRefreshState();
            _ = RefreshUrlTestGroupsAsync();
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
        Dispatcher.UIThread.Post(UpdateTestDelayCommandStates);
        Dispatcher.UIThread.Post(() => TestCurrentGroupCommand.NotifyCanExecuteChanged());
        Dispatcher.UIThread.Post(() => TestGroupCardCommand.NotifyCanExecuteChanged());

        if (status == ServiceStatus.Running)
        {
            UpdateUrlTestRefreshState();
            _ = LoadGroupsAsync();
            return;
        }

        if (status is ServiceStatus.Stopped or ServiceStatus.Error)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ReleaseViewGroups(clearStatusMessage: false);
                _cachedGroups = Array.Empty<GroupCacheSnapshot>();
                TrayGroups = Array.Empty<GroupMenuSnapshot>();
                StatusMessage = status == ServiceStatus.Error
                    ? "sing-box failed to start"
                    : "sing-box is not running";
            });
            UpdateUrlTestRefreshState();
        }
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

    private void UpdateTestDelayCommandStates()
    {
        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
                {
                    asyncCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    private void RecalculateEffectiveDelays()
    {
        var selectedOutboundByGroup = new Dictionary<string, string>(Groups.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var group in Groups)
        {
            selectedOutboundByGroup[group.Name] = group.SelectedOutbound;
        }

        var rawDelayByTag = CreateRawDelayLookup(_outboundItemsByTag);
        var resolvedDelayLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in Groups)
        {
            foreach (var item in group.Items)
            {
                item.Delay = ResolveEffectiveDelay(
                    item.Tag,
                    selectedOutboundByGroup,
                    rawDelayByTag,
                    resolvedDelayLookup,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
        }
    }

    private static int ResolveEffectiveDelay(
        string tag,
        IReadOnlyDictionary<string, string> selectedOutboundByGroup,
        IReadOnlyDictionary<string, int> rawDelayByTag,
        Dictionary<string, int> resolvedDelayLookup,
        HashSet<string> visitedTags)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return 0;
        }

        if (resolvedDelayLookup.TryGetValue(tag, out var cachedDelay))
        {
            return cachedDelay;
        }

        if (!visitedTags.Add(tag))
        {
            return 0;
        }

        if (selectedOutboundByGroup.TryGetValue(tag, out var selectedOutbound) &&
            !string.IsNullOrWhiteSpace(selectedOutbound) &&
            !string.Equals(selectedOutbound, tag, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedDelay = ResolveEffectiveDelay(
                selectedOutbound,
                selectedOutboundByGroup,
                rawDelayByTag,
                resolvedDelayLookup,
                visitedTags);
            resolvedDelayLookup[tag] = resolvedDelay;
            return resolvedDelay;
        }

        var delay = rawDelayByTag.TryGetValue(tag, out var rawDelay) ? rawDelay : 0;
        resolvedDelayLookup[tag] = delay;
        return delay;
    }

    private static bool ShouldDisplayGroup(OutboundGroup group, ClashConfigSnapshot? clashConfig)
    {
        if (!string.Equals(group.Tag, "GLOBAL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var modeCount = 0;
        var modeList = clashConfig?.ModeList;
        if (modeList != null)
        {
            for (var i = 0; i < modeList.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(modeList[i]))
                {
                    modeCount++;
                }
            }
        }

        if (modeCount <= 1)
        {
            return false;
        }

        return string.Equals(clashConfig?.Mode, "global", StringComparison.OrdinalIgnoreCase);
    }

    private async Task SelectOutboundAsync(string groupTag, string? outboundTag)
    {
        if (_singBoxManager == null || string.IsNullOrEmpty(outboundTag))
        {
            return;
        }

        var group = await Dispatcher.UIThread.InvokeAsync(() => FindGroupByName(groupTag));
        if (group != null && string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _singBoxManager.SelectOutboundAsync(groupTag, outboundTag);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (group != null)
                {
                    SelectedGroup = group;
                    group.SelectedOutbound = outboundTag;
                }

                RecalculateEffectiveDelays();
                if (Groups.Count > 0)
                {
                    UpdateCachesFromLoadedGroups();
                }
                else
                {
                    UpdateCachedGroupSelection(groupTag, outboundTag);
                    UpdateTrayGroupsFromCache();
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

    private async Task RefreshGroupSelectionAsync(string groupTag)
    {
        if (_singBoxManager == null || string.IsNullOrWhiteSpace(groupTag) || !_singBoxManager.IsRunning)
        {
            return;
        }

        try
        {
            await RefreshGroupsSnapshotAsync(new[] { groupTag });
        }
        catch
        {
        }
    }

    private async Task RefreshGroupsSnapshotAsync(IEnumerable<string> groupTags)
    {
        if (_singBoxManager == null || !_singBoxManager.IsRunning)
        {
            return;
        }

        var tagSet = CreateTagSet(groupTags);
        if (tagSet.Count == 0)
        {
            return;
        }

        var groups = await _singBoxManager.GetOutboundGroupsAsync();
        var groupLookup = new Dictionary<string, OutboundGroup>(groups.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            groupLookup[group.Tag] = group;
        }

        if (!HasMatchingGroup(groupLookup))
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var existingGroup in Groups)
            {
                if (!groupLookup.TryGetValue(existingGroup.Name, out var updatedGroup))
                {
                    continue;
                }

                existingGroup.SelectedOutbound = updatedGroup.Selected;
                var updatedItemLookup = CreateOutboundItemLookup(updatedGroup);

                foreach (var existingItem in existingGroup.Items)
                {
                    if (updatedItemLookup.TryGetValue(existingItem.Tag, out var updatedItem))
                    {
                        ApplySharedRawDelay(updatedItem.Tag, updatedItem.UrlTestDelay);
                    }
                }
            }

            RecalculateEffectiveDelays();
            UpdateCachesFromLoadedGroups();
            _lastCacheRefreshAt = DateTimeOffset.UtcNow;
        });
    }

    private void OnUrlTestRefreshTimerTick(object? sender, EventArgs e)
    {
        _ = RefreshUrlTestGroupsAsync();
    }

    private void UpdateUrlTestRefreshState()
    {
        if (_singBoxManager?.IsRunning == true && _isPageActive && _isWindowVisible)
        {
            _urlTestRefreshTimer.Start();
            return;
        }

        _urlTestRefreshTimer.Stop();
    }

    private async Task RefreshUrlTestGroupsAsync()
    {
        if (_singBoxManager?.IsRunning != true || !_isPageActive || !_isWindowVisible || _isRefreshingUrlTestGroups)
        {
            return;
        }

        _isRefreshingUrlTestGroups = true;
        try
        {
            var urlTestGroupNames = await Dispatcher.UIThread.InvokeAsync(GetUrlTestGroupNames);
            if (urlTestGroupNames.Count == 0)
            {
                return;
            }

            try
            {
                await RefreshGroupsSnapshotAsync(urlTestGroupNames);
            }
            catch
            {
            }
        }
        finally
        {
            _isRefreshingUrlTestGroups = false;
        }
    }

    private async Task TestOutboundAsync(OutboundItemViewModel item)
    {
        if (_singBoxManager == null || string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        item.IsTesting = true;
        if (item.TestDelayCommand is AsyncRelayCommand asyncCommand)
        {
            asyncCommand.NotifyCanExecuteChanged();
        }

        StatusMessage = $"Testing {item.Tag}...";

        try
        {
            var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
            var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;
            ApplySharedRawDelay(item.Tag, delay);
            RecalculateEffectiveDelays();
            UpdateCachesFromLoadedGroups();
            StatusMessage = item.Delay > 0
                ? $"{item.Tag}: {item.Delay}ms"
                : $"{item.Tag}: timeout";
        }
        catch (Exception ex)
        {
            ApplySharedRawDelay(item.Tag, 0);
            RecalculateEffectiveDelays();
            StatusMessage = $"Failed to test {item.Tag}: {ex.Message}";
        }
        finally
        {
            foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
            {
                sharedItem.IsTesting = false;
                if (sharedItem.TestDelayCommand is AsyncRelayCommand updatedCommand)
                {
                    updatedCommand.NotifyCanExecuteChanged();
                }
            }
        }
    }

    partial void OnSelectedGroupChanged(GroupItemViewModel? value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
        TestGroupCardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsTestingGroupChanged(bool value)
    {
        TestCurrentGroupCommand.NotifyCanExecuteChanged();
        TestGroupCardCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestCurrentGroup()
    {
        return _singBoxManager?.IsRunning == true && SelectedGroup != null && !IsTestingGroup;
    }

    private bool CanTestGroupCard(GroupItemViewModel? group)
    {
        return _singBoxManager?.IsRunning == true && group != null && !IsTestingGroup;
    }

    [RelayCommand]
    private void ToggleGroupExpansion(GroupItemViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        SelectedGroup = group;
        group.IsExpanded = !group.IsExpanded;
        _groupExpandStates[group.Name] = group.IsExpanded;
    }

    private void RegisterOutboundItem(OutboundItemViewModel item)
    {
        if (string.IsNullOrWhiteSpace(item.Tag))
        {
            return;
        }

        if (!_outboundItemsByTag.TryGetValue(item.Tag, out var items))
        {
            items = new List<OutboundItemViewModel>();
            _outboundItemsByTag[item.Tag] = items;
        }

        items.Add(item);
        var sharedDelay = GetFirstPositiveRawDelay(items);
        if (sharedDelay > 0 && item.RawDelay != sharedDelay)
        {
            item.RawDelay = sharedDelay;
        }
    }

    private IReadOnlyList<OutboundItemViewModel> GetSharedOutboundItems(string tag)
    {
        return _outboundItemsByTag.TryGetValue(tag, out var items)
            ? items
            : Array.Empty<OutboundItemViewModel>();
    }

    private int GetSharedRawDelay(string tag)
    {
        var items = GetSharedOutboundItems(tag);
        for (var i = 0; i < items.Count; i++)
        {
            var delay = items[i].RawDelay;
            if (delay > 0)
            {
                return delay;
            }
        }

        return 0;
    }

    private void ApplySharedRawDelay(string tag, int delay)
    {
        foreach (var item in GetSharedOutboundItems(tag))
        {
            item.RawDelay = delay;
        }
    }

    private async Task TestGroupAsync(
        GroupItemViewModel group,
        bool updateTestingState,
        bool onlyTestMissingDelay)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
        {
            await TestUrlTestGroupAsync(group, updateTestingState, onlyTestMissingDelay);
            return;
        }

        var targets = BuildUniqueOutboundTargets(group.Items, onlyTestMissingDelay);

        if (targets.Count == 0)
        {
            return;
        }

        if (updateTestingState)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTestingGroup = true;
                StatusMessage = $"Testing {group.Name}...";
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in targets)
            {
                foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                {
                    sharedItem.IsTesting = true;
                    if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                    {
                        asyncCommand.NotifyCanExecuteChanged();
                    }
                }
            }
        });

        try
        {
            var completedCounter = new int[1];
            var totalCount = targets.Count;
            var testTasks = new Task[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                testTasks[i] = TestGroupTargetAsync(group, item, updateTestingState, totalCount, completedCounter);
            }

            await Task.WhenAll(testTasks);
            if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshGroupSelectionAsync(group.Name);
            }

            if (updateTestingState)
            {
                UpdateCachesFromLoadedGroups();
                StatusMessage = $"Test completed: {group.Name}";
            }
        }
        catch (Exception ex)
        {
            if (updateTestingState)
            {
                StatusMessage = $"Test failed: {ex.Message}";
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in targets)
                {
                    foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                    {
                        sharedItem.IsTesting = false;
                        if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                        {
                            asyncCommand.NotifyCanExecuteChanged();
                        }
                    }
                }

                if (updateTestingState)
                {
                    IsTestingGroup = false;
                }
            });
        }
    }

    private async Task TestUrlTestGroupAsync(
        GroupItemViewModel group,
        bool updateTestingState,
        bool onlyTestMissingDelay)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        var targets = BuildUniqueOutboundTargets(group.Items, onlyTestMissingDelay: false);

        if (targets.Count == 0)
        {
            return;
        }

        if (onlyTestMissingDelay && AllTargetsHaveSharedDelay(targets))
        {
            await RefreshGroupSelectionAsync(group.Name);
            return;
        }

        if (updateTestingState)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsTestingGroup = true;
                StatusMessage = $"Testing {group.Name}...";
            });
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var item in targets)
            {
                foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                {
                    sharedItem.IsTesting = true;
                    if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                    {
                        asyncCommand.NotifyCanExecuteChanged();
                    }
                }
            }
        });

        try
        {
            await _singBoxManager.RunGroupDelayTestAsync(group.Name);

            var testTasks = new Task[targets.Count];
            for (var i = 0; i < targets.Count; i++)
            {
                var item = targets[i];
                testTasks[i] = RefreshUrlTestTargetDelayAsync(item);
            }

            await Task.WhenAll(testTasks);
            await RefreshGroupSelectionAsync(group.Name);

            if (updateTestingState)
            {
                UpdateCachesFromLoadedGroups();
                StatusMessage = $"Test completed: {group.Name}";
            }
        }
        catch (Exception ex)
        {
            if (updateTestingState)
            {
                StatusMessage = $"Test failed: {ex.Message}";
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var item in targets)
                {
                    foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
                    {
                        sharedItem.IsTesting = false;
                        if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                        {
                            asyncCommand.NotifyCanExecuteChanged();
                        }
                    }
                }

                if (updateTestingState)
                {
                    IsTestingGroup = false;
                }
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestCurrentGroup))]
    private async Task TestCurrentGroupAsync()
    {
        if (SelectedGroup == null)
        {
            return;
        }

        await TestGroupAsync(SelectedGroup, updateTestingState: true, onlyTestMissingDelay: false);
    }

    [RelayCommand(CanExecute = nameof(CanTestGroupCard))]
    private async Task TestGroupCardAsync(GroupItemViewModel? group)
    {
        if (group == null)
        {
            return;
        }

        SelectedGroup = group;
        await TestGroupAsync(group, updateTestingState: true, onlyTestMissingDelay: false);
    }

    public Task SelectOutboundFromTrayAsync(string groupTag, string outboundTag)
    {
        return SelectOutboundAsync(groupTag, outboundTag);
    }

    private GroupItemViewModel? FindGroupByName(string groupName)
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            if (string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase))
            {
                return group;
            }
        }

        return null;
    }

    private static HashSet<string> CreateTagSet(IEnumerable<string> groupTags)
    {
        var tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in groupTags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tagSet.Add(tag);
            }
        }

        return tagSet;
    }

    private bool HasMatchingGroup(Dictionary<string, OutboundGroup> groupLookup)
    {
        for (var i = 0; i < Groups.Count; i++)
        {
            if (groupLookup.ContainsKey(Groups[i].Name))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, OutboundItem> CreateOutboundItemLookup(OutboundGroup group)
    {
        var itemLookup = new Dictionary<string, OutboundItem>(group.Items.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < group.Items.Count; i++)
        {
            var item = group.Items[i];
            itemLookup[item.Tag] = item;
        }

        return itemLookup;
    }

    private List<string> GetUrlTestGroupNames()
    {
        var result = new List<string>();
        for (var i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i];
            if (string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(group.Name);
            }
        }

        return result;
    }

    private static int GetFirstPositiveRawDelay(IReadOnlyList<OutboundItemViewModel> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var delay = items[i].RawDelay;
            if (delay > 0)
            {
                return delay;
            }
        }

        return 0;
    }

    private static Dictionary<string, int> CreateRawDelayLookup(
        IReadOnlyDictionary<string, List<OutboundItemViewModel>> outboundItemsByTag)
    {
        var rawDelayByTag = new Dictionary<string, int>(outboundItemsByTag.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in outboundItemsByTag)
        {
            var delay = GetFirstPositiveRawDelay(entry.Value);
            if (delay > 0)
            {
                rawDelayByTag[entry.Key] = delay;
            }
        }

        return rawDelayByTag;
    }

    private static Dictionary<string, int> CreateResolvedDelayLookup(IReadOnlyList<OutboundGroup> groups)
    {
        var selectedOutboundByGroup = new Dictionary<string, string>(groups.Count, StringComparer.OrdinalIgnoreCase);
        var rawDelayByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            selectedOutboundByGroup[group.Tag] = group.Selected;
            for (var j = 0; j < group.Items.Count; j++)
            {
                var item = group.Items[j];
                if (item.UrlTestDelay > 0 && !rawDelayByTag.ContainsKey(item.Tag))
                {
                    rawDelayByTag[item.Tag] = item.UrlTestDelay;
                }
            }
        }

        var resolvedDelayLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in rawDelayByTag)
        {
            ResolveEffectiveDelay(
                entry.Key,
                selectedOutboundByGroup,
                rawDelayByTag,
                resolvedDelayLookup,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        return resolvedDelayLookup;
    }

    private void RestoreViewGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            ReleaseViewGroups(clearStatusMessage: false);
            return;
        }

        var previousSelection = SelectedGroup?.Name;
        var preferGlobalGroup = string.Equals(_clashConfigCache.Current?.Mode, "global", StringComparison.OrdinalIgnoreCase);
        ReleaseViewGroups(clearStatusMessage: false);
        GroupItemViewModel? selected = null;
        GroupItemViewModel? globalGroup = null;
        GroupItemViewModel? firstGroup = null;

        foreach (var cachedGroup in _cachedGroups)
        {
            if (cachedGroup.Items.Count == 0)
            {
                continue;
            }

            var outboundItems = new List<OutboundItemViewModel>(cachedGroup.Items.Count);
            for (var i = 0; i < cachedGroup.Items.Count; i++)
            {
                var item = cachedGroup.Items[i];
                outboundItems.Add(new OutboundItemViewModel
                {
                    Tag = item.Tag,
                    Type = item.Type,
                    Delay = item.Delay,
                    RawDelay = item.RawDelay
                });
            }

            var groupVm = new GroupItemViewModel
            {
                Name = cachedGroup.Name,
                Type = cachedGroup.Type,
                SelectedOutbound = cachedGroup.SelectedOutbound,
                Items = new ObservableCollection<OutboundItemViewModel>(outboundItems),
                IsExpanded = _groupExpandStates.TryGetValue(cachedGroup.Name, out var isExpanded) && isExpanded
            };

            groupVm.SelectOutboundCommand = new AsyncRelayCommand<string>(
                outboundTag => SelectOutboundAsync(cachedGroup.Name, outboundTag),
                _ => cachedGroup.IsSelectable && _singBoxManager?.IsRunning == true);

            foreach (var item in groupVm.Items)
            {
                RegisterOutboundItem(item);
                item.SelectOutboundCommand = groupVm.SelectOutboundCommand;
                item.TestDelayCommand = new AsyncRelayCommand(
                    () => TestOutboundAsync(item),
                    () => _singBoxManager?.IsRunning == true && !item.IsTesting);
            }

            groupVm.UpdateItemSelection();

            if (string.Equals(groupVm.Name, previousSelection, StringComparison.OrdinalIgnoreCase))
            {
                selected = groupVm;
            }

            if (string.Equals(groupVm.Name, "GLOBAL", StringComparison.OrdinalIgnoreCase))
            {
                globalGroup = groupVm;
            }

            firstGroup ??= groupVm;
            Groups.Add(groupVm);
        }

        SelectedGroup = preferGlobalGroup
            ? globalGroup ?? selected ?? firstGroup
            : selected ?? firstGroup;
    }

    private static IReadOnlyList<GroupCacheSnapshot> BuildCachedGroupsFromOutboundGroups(IReadOnlyList<OutboundGroup> groups)
    {
        var resolvedDelayLookup = CreateResolvedDelayLookup(groups);
        var cachedGroups = new List<GroupCacheSnapshot>(groups.Count);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var cachedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            for (var j = 0; j < group.Items.Count; j++)
            {
                var item = group.Items[j];
                cachedItems.Add(new OutboundCacheSnapshot(
                    item.Tag,
                    item.Type,
                    resolvedDelayLookup.TryGetValue(item.Tag, out var delay) ? delay : 0,
                    item.UrlTestDelay,
                    string.Equals(item.Tag, group.Selected, StringComparison.OrdinalIgnoreCase)));
            }

            cachedGroups.Add(new GroupCacheSnapshot(
                group.Tag,
                group.Type,
                group.Selected,
                !string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase),
                cachedItems));
        }

        return cachedGroups;
    }

    private void UpdateCachesFromLoadedGroups()
    {
        if (Groups.Count == 0)
        {
            return;
        }

        var cachedGroups = new List<GroupCacheSnapshot>(Groups.Count);
        foreach (var group in Groups)
        {
            var cachedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            foreach (var item in group.Items)
            {
                cachedItems.Add(new OutboundCacheSnapshot(
                    item.Tag,
                    item.Type,
                    item.Delay,
                    item.RawDelay,
                    item.IsSelected));
            }

            cachedGroups.Add(new GroupCacheSnapshot(
                group.Name,
                group.Type,
                group.SelectedOutbound,
                !string.Equals(group.Type, "URLTest", StringComparison.OrdinalIgnoreCase),
                cachedItems));
        }

        _cachedGroups = cachedGroups;
        UpdateTrayGroupsFromCache();
    }

    private void UpdateCachedGroupSelection(string groupTag, string outboundTag)
    {
        if (_cachedGroups.Count == 0)
        {
            return;
        }

        var updatedGroups = new List<GroupCacheSnapshot>(_cachedGroups.Count);
        var hasChanges = false;

        foreach (var group in _cachedGroups)
        {
            if (!string.Equals(group.Name, groupTag, StringComparison.OrdinalIgnoreCase))
            {
                updatedGroups.Add(group);
                continue;
            }

            var updatedItems = new List<OutboundCacheSnapshot>(group.Items.Count);
            foreach (var item in group.Items)
            {
                var isSelected = string.Equals(item.Tag, outboundTag, StringComparison.OrdinalIgnoreCase);
                updatedItems.Add(item with { IsSelected = isSelected });
                hasChanges |= item.IsSelected != isSelected;
            }

            updatedGroups.Add(group with { SelectedOutbound = outboundTag, Items = updatedItems });
        }

        if (hasChanges)
        {
            _cachedGroups = updatedGroups;
        }
    }

    private void UpdateTrayGroupsFromCache()
    {
        if (_cachedGroups.Count == 0)
        {
            TrayGroups = Array.Empty<GroupMenuSnapshot>();
            return;
        }

        var trayGroups = new List<GroupMenuSnapshot>(_cachedGroups.Count);
        foreach (var group in _cachedGroups)
        {
            var trayItems = new List<OutboundMenuSnapshot>(group.Items.Count);
            foreach (var item in group.Items)
            {
                trayItems.Add(new OutboundMenuSnapshot(item.Tag, item.Delay, item.IsSelected));
            }

            trayGroups.Add(new GroupMenuSnapshot(
                group.Name,
                group.Type,
                group.IsSelectable,
                trayItems));
        }

        TrayGroups = trayGroups;
    }

    private void ReleaseViewGroups(bool clearStatusMessage)
    {
        _outboundItemsByTag.Clear();
        Groups.Clear();
        SelectedGroup = null;
        IsTestingGroup = false;

        if (clearStatusMessage)
        {
            StatusMessage = string.Empty;
        }
    }

    private bool IsCacheExpired(DateTimeOffset now)
    {
        return _lastCacheRefreshAt.HasValue && now - _lastCacheRefreshAt.Value > CacheExpirationInterval;
    }

    private List<OutboundItemViewModel> BuildUniqueOutboundTargets(
        ObservableCollection<OutboundItemViewModel> items,
        bool onlyTestMissingDelay)
    {
        var result = new List<OutboundItemViewModel>(items.Count);
        var seenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (string.IsNullOrWhiteSpace(item.Tag) || !seenTags.Add(item.Tag))
            {
                continue;
            }

            if (onlyTestMissingDelay && HasPositiveSharedRawDelay(item.Tag))
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private bool HasPositiveSharedRawDelay(string tag)
    {
        var sharedItems = GetSharedOutboundItems(tag);
        for (var i = 0; i < sharedItems.Count; i++)
        {
            if (sharedItems[i].RawDelay > 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool AllTargetsHaveSharedDelay(List<OutboundItemViewModel> targets)
    {
        for (var i = 0; i < targets.Count; i++)
        {
            if (!HasPositiveSharedRawDelay(targets[i].Tag))
            {
                return false;
            }
        }

        return true;
    }

    private async Task TestGroupTargetAsync(
        GroupItemViewModel group,
        OutboundItemViewModel item,
        bool updateTestingState,
        int totalCount,
        int[] completedCounter)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
        var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplySharedRawDelay(item.Tag, delay);
            RecalculateEffectiveDelays();
            foreach (var sharedItem in GetSharedOutboundItems(item.Tag))
            {
                sharedItem.IsTesting = false;
                if (sharedItem.TestDelayCommand is AsyncRelayCommand asyncCommand)
                {
                    asyncCommand.NotifyCanExecuteChanged();
                }
            }

            completedCounter[0]++;
            if (updateTestingState)
            {
                StatusMessage = delay > 0
                    ? $"{group.Name}: {item.Tag} {delay}ms ({completedCounter[0]}/{totalCount})"
                    : $"{group.Name}: {item.Tag} timeout ({completedCounter[0]}/{totalCount})";
            }
        });
    }

    private async Task RefreshUrlTestTargetDelayAsync(OutboundItemViewModel item)
    {
        if (_singBoxManager == null)
        {
            return;
        }

        var delays = await _singBoxManager.RunOutboundDelayTestsAsync(new[] { item.Tag });
        var delay = delays.TryGetValue(item.Tag, out var value) && value >= 0 ? value : 0;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplySharedRawDelay(item.Tag, delay);
            RecalculateEffectiveDelays();
        });
    }
}

public partial class GroupItemViewModel : ObservableObject
{
    private const int CollapsedPreviewItemLimit = 24;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _selectedOutbound = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OutboundItemViewModel> _items = new();

    [ObservableProperty]
    private bool _isExpanded;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    public IReadOnlyList<OutboundItemViewModel> CollapsedPreviewItems
    {
        get
        {
            if (Items.Count <= CollapsedPreviewItemLimit)
            {
                return Items;
            }

            var previewItems = new List<OutboundItemViewModel>(CollapsedPreviewItemLimit);
            for (var i = 0; i < CollapsedPreviewItemLimit; i++)
            {
                previewItems.Add(Items[i]);
            }

            return previewItems;
        }
    }

    public bool IsCollapsed => !IsExpanded;

    partial void OnSelectedOutboundChanged(string value)
    {
        UpdateItemSelection();
    }

    partial void OnItemsChanged(ObservableCollection<OutboundItemViewModel> value)
    {
        UpdateItemSelection();
        OnPropertyChanged(nameof(CollapsedPreviewItems));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCollapsed));
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
    private string _type = string.Empty;

    [ObservableProperty]
    private int _delay;

    [ObservableProperty]
    private int _rawDelay;

    public IAsyncRelayCommand<string>? SelectOutboundCommand { get; set; }

    public IAsyncRelayCommand? TestDelayCommand { get; set; }

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    private bool _isHovered;

    public string DelayDisplay => IsTesting ? "..." : Delay > 0 ? $"{Delay}ms" : string.Empty;

    partial void OnDelayChanged(int value)
    {
        OnPropertyChanged(nameof(DelayDisplay));
    }

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(DelayDisplay));
    }
}

public sealed record OutboundMenuSnapshot(string Tag, int Delay, bool IsSelected);

public sealed record GroupMenuSnapshot(
    string Name,
    string Type,
    bool IsSelectable,
    IReadOnlyList<OutboundMenuSnapshot> Items);

public sealed record OutboundCacheSnapshot(
    string Tag,
    string Type,
    int Delay,
    int RawDelay,
    bool IsSelected);

public sealed record GroupCacheSnapshot(
    string Name,
    string Type,
    string SelectedOutbound,
    bool IsSelectable,
    IReadOnlyList<OutboundCacheSnapshot> Items);
