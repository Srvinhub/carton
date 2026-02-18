using System;
using carton.GUI.Services;

namespace carton.GUI.Models;

public enum NavigationPage
{
    Dashboard,
    Profiles,
    Groups,
    Connections,
    Logs,
    Settings
}

public static class NavigationPageExtensions
{
    public static string GetTitle(this NavigationPage page)
    {
        var (resourceKey, fallback) = page switch
        {
            NavigationPage.Dashboard => ("Navigation.Dashboard", "Dashboard"),
            NavigationPage.Profiles => ("Navigation.Profiles", "Profiles"),
            NavigationPage.Groups => ("Navigation.Groups", "Groups"),
            NavigationPage.Connections => ("Navigation.Connections", "Connections"),
            NavigationPage.Logs => ("Navigation.Logs", "Logs"),
            NavigationPage.Settings => ("Navigation.Settings", "Settings"),
            _ => throw new ArgumentOutOfRangeException(nameof(page))
        };

        var text = LocalizationService.Instance[resourceKey];
        return string.IsNullOrWhiteSpace(text) || text == resourceKey ? fallback : text;
    }

    public static string GetIcon(this NavigationPage page) => page switch
    {
        NavigationPage.Dashboard => "Home",
        NavigationPage.Profiles => "Profiles",
        NavigationPage.Groups => "Group",
        NavigationPage.Connections => "Connections",
        NavigationPage.Logs => "Logs",
        NavigationPage.Settings => "Settings",
        _ => throw new ArgumentOutOfRangeException(nameof(page))
    };
}
