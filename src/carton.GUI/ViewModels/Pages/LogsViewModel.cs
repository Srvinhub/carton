using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using carton.GUI.Models;

namespace carton.ViewModels;

public partial class LogsViewModel : PageViewModelBase
{
    public override NavigationPage PageType => NavigationPage.Logs;

    [ObservableProperty]
    private ObservableCollection<LogEntryViewModel> _logs = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _selectedLevel = "All";

    [ObservableProperty]
    private LogEntryViewModel? _selectedLog;

    public ObservableCollection<string> LogLevels { get; } = new() { "All", "Debug", "Info", "Warn", "Error" };
    private readonly List<LogEntryViewModel> _allLogs = new();

    // Keep a large rolling window for troubleshooting while preventing unbounded growth.
    private const int MaxLogEntries = 50000;

    public LogsViewModel()
    {
        Title = "Logs";
        Icon = "Logs";
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilters();
    }

    partial void OnSelectedLevelChanged(string value)
    {
        ApplyFilters();
    }

    public void AddLog(string message)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddLog(message));
            return;
        }

        var level = "Info";
        var msg = message;
        
        if (message.Contains("[ERROR]") || message.Contains("[error]"))
        {
            level = "Error";
        }
        else if (message.Contains("[WARN]") || message.Contains("[warn]"))
        {
            level = "Warn";
        }
        else if (message.Contains("[DEBUG]") || message.Contains("[debug]"))
        {
            level = "Debug";
        }

        var entry = new LogEntryViewModel
        {
            Time = DateTime.Now.ToString("HH:mm:ss"),
            Level = level,
            Message = msg
        };

        _allLogs.Add(entry);
        if (MatchesFilter(entry))
        {
            Logs.Add(entry);
        }

        while (_allLogs.Count > MaxLogEntries)
        {
            var removed = _allLogs[0];
            _allLogs.RemoveAt(0);
            Logs.Remove(removed);
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
        await CopyTextToClipboardAsync(line);
    }

    [RelayCommand]
    private async Task CopyAllLogs()
    {
        if (Logs.Count == 0) return;

        var sb = new StringBuilder();
        foreach (var log in Logs)
        {
            sb.Append('[').Append(log.Time).Append("] [").Append(log.Level).Append("] ").Append(log.Message).AppendLine();
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

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return log.Message.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Level.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               log.Time.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }
}

public partial class LogEntryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _time = string.Empty;

    [ObservableProperty]
    private string _level = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;
}
