using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using carton.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase
{
    private bool _isOnPage;
    private bool _isWindowVisible = true;
    private bool _hasPendingVisibleRefresh;
    private readonly ILocalizationService _localizationService;

    public override NavigationPage PageType => NavigationPage.Logs;

    [ObservableProperty]
    private ObservableCollection<LogEntryViewModel> _logs = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private LogSourceFilterOptionViewModel? _selectedSourceFilter;

    [ObservableProperty]
    private LogEntryViewModel? _selectedLog;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    public ObservableCollection<LogSourceFilterOptionViewModel> LogSourceFilters { get; } = new();
    private readonly List<LogEntryViewModel> _allLogs = new();

    // Keep a large rolling window for troubleshooting while preventing unbounded growth.
    private const int MaxLogEntries = 2000;

    public LogsViewModel()
    {
        Title = "Logs";
        Icon = "Logs";
        _localizationService = LocalizationService.Instance;
        InitializeSourceFilters();
        _localizationService.LanguageChanged += (_, _) => RefreshLocalizedText();
    }

    public void OnNavigatedTo()
    {
        _isOnPage = true;
        RefreshVisibleLogsIfNeeded();
    }

    public void SetWindowVisible(bool isVisible)
    {
        _isWindowVisible = isVisible;
        if (isVisible)
        {
            RefreshVisibleLogsIfNeeded();
        }
    }

    public void OnNavigatedFrom()
    {
        _isOnPage = false;
    }

    private void RefreshVisibleLogsIfNeeded()
    {
        if (_isOnPage && _isWindowVisible && _hasPendingVisibleRefresh)
        {
            _hasPendingVisibleRefresh = false;
            ApplyFilters();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedSourceFilterChanged(LogSourceFilterOptionViewModel? value)
    {
        ApplyFilters();
    }

    public void AddLog(string message)
    {
        AddLog(message, LogSource.Carton);
    }

    public void AddLog(string message, LogSource source)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message, source));
            return;
        }

        // Strip ANSI escape sequences
        var msg = System.Text.RegularExpressions.Regex.Replace(message, @"\e\[[0-9;]*[a-zA-Z]", "");

        // Remove sing-box timestamp: "+0800 2026-03-09 10:46:36 " or "2024-03-15T12:00:00.000Z "
        var tsRegex = new System.Text.RegularExpressions.Regex(@"^([+-]\d{4}\s+)?\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(\.\d+[Zz]?)?\s+");
        msg = tsRegex.Replace(msg, "");

        var level = "Info";

        // Extract and remove level
        var levelRegex = new System.Text.RegularExpressions.Regex(@"^\[?(DEBUG|INFO|WARN|WARNING|ERROR|FATAL|debug|info|warn|warning|error|fatal)\]?[\s:]+");
        var levelMatch = levelRegex.Match(msg);
        if (levelMatch.Success)
        {
            var l = levelMatch.Groups[1].Value;
            if (l.Equals("WARNING", StringComparison.OrdinalIgnoreCase)) l = "Warn";
            level = l.Length > 0 ? char.ToUpper(l[0]) + l.Substring(1).ToLower() : level;
            msg = msg.Substring(levelMatch.Length);
        }
        else
        {
            // Fallback content scan
            if (msg.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || msg.Contains("[error]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Error";
            }
            else if (msg.Contains("WARN", StringComparison.OrdinalIgnoreCase) || msg.Contains("[warn]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Warn";
            }
            else if (msg.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) || msg.Contains("[debug]", StringComparison.OrdinalIgnoreCase))
            {
                level = "Debug";
            }
        }

        // Extract and remove connection context ID, e.g., "[1651110515 5.0s] " or "[1234] "
        var contextRegex = new System.Text.RegularExpressions.Regex(@"^\[\d+(?:\s+[^\]]+)?\]\s*");
        msg = contextRegex.Replace(msg, "");

        var entry = new LogEntryViewModel
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Source = source,
            SourceDisplayName = GetSourceDisplayName(source),
            Level = level,
            Message = msg
        };

        _allLogs.Add(entry);
        if (_isOnPage && _isWindowVisible && MatchesFilter(entry))
        {
            Logs.Add(entry);
        }
        else if (!_isOnPage || !_isWindowVisible)
        {
            _hasPendingVisibleRefresh = true;
        }

        while (_allLogs.Count > MaxLogEntries)
        {
            var removed = _allLogs[0];
            _allLogs.RemoveAt(0);
            if (_isOnPage && _isWindowVisible)
            {
                Logs.Remove(removed);
            }
            else
            {
                _hasPendingVisibleRefresh = true;
            }
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _allLogs.Clear();
        Logs.Clear();
        SelectedLog = null;
    }

    [RelayCommand]
    private async Task CopySelectedLog()
    {
        if (SelectedLog == null) return;

        var line = $"[{SelectedLog.Time}] [{SelectedLog.Level}] {SelectedLog.Message}";
        if (!string.IsNullOrWhiteSpace(SelectedLog.SourceDisplayName))
        {
            line = $"[{SelectedLog.Time}] [{SelectedLog.SourceDisplayName}] [{SelectedLog.Level}] {SelectedLog.Message}";
        }
        await CopyTextToClipboardAsync(line);
    }

    [RelayCommand]
    private async Task CopyAllLogs()
    {
        if (Logs.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var log in Logs)
        {
            sb.Append('[').Append(log.Time).Append("] [").Append(log.SourceDisplayName).Append("] [").Append(log.Level).Append("] ").Append(log.Message).AppendLine();
        }

        await CopyTextToClipboardAsync(sb.ToString());
    }

    private static async Task CopyTextToClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard == null)
        {
            return;
        }

        await desktop.MainWindow.Clipboard.SetTextAsync(text);
    }

    private void ApplyFilters()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyFilters);
            return;
        }

        var filtered = _allLogs.Where(MatchesFilter).ToList();

        Logs.Clear();
        foreach (var log in filtered)
        {
            Logs.Add(log);
        }

        if (SelectedLog != null && !Logs.Contains(SelectedLog))
        {
            SelectedLog = null;
        }
    }

    private bool MatchesFilter(LogEntryViewModel log)
    {
        var levelMatched = SelectedLevel == "All" ||
                           string.Equals(log.Level, SelectedLevel, StringComparison.OrdinalIgnoreCase);

        if (!levelMatched)
        {
            return false;
        }

        var selectedFilter = SelectedSourceFilter?.Filter ?? LogSourceFilter.All;
        var sourceMatched = selectedFilter switch
        {
            LogSourceFilter.All => true,
            LogSourceFilter.Carton => log.Source == LogSource.Carton,
            LogSourceFilter.SingBox => log.Source == LogSource.SingBox,
            _ => true
        };

        if (!sourceMatched)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.SourceDisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Time.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private void InitializeSourceFilters()
    {
        LogSourceFilters.Clear();
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.All });
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.Carton });
        LogSourceFilters.Add(new LogSourceFilterOptionViewModel { Filter = LogSourceFilter.SingBox });
        UpdateSourceFilterDisplayNames();
        SelectedSourceFilter = LogSourceFilters[0];
    }

    private void RefreshLocalizedText()
    {
        UpdateSourceFilterDisplayNames();
        foreach (var log in _allLogs)
        {
            log.SourceDisplayName = GetSourceDisplayName(log.Source);
        }
    }

    private void UpdateSourceFilterDisplayNames()
    {
        foreach (var option in LogSourceFilters)
        {
            option.DisplayName = option.Filter switch
            {
                LogSourceFilter.All => _localizationService["Logs.Source.All"],
                LogSourceFilter.Carton => _localizationService["Logs.Source.Carton"],
                LogSourceFilter.SingBox => _localizationService["Logs.Source.SingBox"],
                _ => option.Filter.ToString()
            };
        }
    }

    private string GetSourceDisplayName(LogSource source)
    {
        return source switch
        {
            LogSource.Carton => _localizationService["Logs.Source.Carton"],
            LogSource.SingBox => _localizationService["Logs.Source.SingBox"],
            _ => source.ToString()
        };
    }
}

public partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private LogSource _source;

    [ObservableProperty]
    private string _sourceDisplayName = string.Empty;

    [ObservableProperty]
    private string _level = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;
}

public partial class LogSourceFilterOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private LogSourceFilter _filter;

    [ObservableProperty]
    private string _displayName = string.Empty;
}
