using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using carton.Core.Models;
using carton.Core.Services;
using carton.GUI.Models;
using carton.GUI.Services;
using carton.ViewModels;
using carton.Views;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

namespace carton;

public partial class App : Application
{
    private TrayMenuService? _trayMenuService;
    private static IPreferencesService? _preferencesService;

    public static IPreferencesService PreferencesService =>
        _preferencesService ?? throw new InvalidOperationException("Preferences service has not been initialized.");

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        var appVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "1.0";
        // strip build metadata (e.g. "+sha")
        var plusIndex = appVersion.IndexOf('+');
        if (plusIndex >= 0) appVersion = appVersion[..plusIndex];
        HttpClientFactory.Initialize(appVersion);
        var preferences = LoadOrCreatePreferences();
        LocalizationService.Instance.Initialize(preferences.Language);
        ThemeService.Instance.Initialize(preferences.Theme);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var viewModel = new MainViewModel();
            var mainWindow = new MainWindow
            {
                DataContext = viewModel,
            };
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = mainWindow;
            SingleInstanceService.RegisterMainWindow(mainWindow);
            _trayMenuService = new TrayMenuService();
            _trayMenuService.Initialize(this, desktop, mainWindow, viewModel);
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        _trayMenuService?.Dispose();
        _trayMenuService = null;

        if (desktop.MainWindow?.DataContext is MainViewModel viewModel)
        {
            viewModel.ShutdownAsync().GetAwaiter().GetResult();
        }

        SingleInstanceService.Dispose();

        Environment.Exit(0);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "This Avalonia validation plugin removal path has been verified in published AOT builds.")]
    private void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }

    private static AppPreferences LoadOrCreatePreferences()
    {
        if (_preferencesService == null)
        {
            var workingDirectory = ResolveWorkingDirectory();
            _preferencesService = new PreferencesService(workingDirectory);
        }

        return _preferencesService.Load();
    }

    private static string ResolveWorkingDirectory()
    {
        var appDataPath = carton.Core.Utilities.PathHelper.GetAppDataPath();
        var workingDirectory = Path.Combine(appDataPath, "data");
        Directory.CreateDirectory(workingDirectory);
        return workingDirectory;
    }
}

public class ConnectionStatusConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isConnected)
        {
            return isConnected ? Brushes.Green : Brushes.Red;
        }
        return Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NavigationIconConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is NavigationPage page)
        {
            return page switch
            {
                NavigationPage.Dashboard => "M12 3 L3 10 V21 H9 V15 H15 V21 H21 V10 Z",
                NavigationPage.Profiles => "M3 7.5 A1.5 1.5 0 0 1 4.5 6 H8.88 A1.5 1.5 0 0 1 9.94 6.44 L11.62 8.12 A1.5 1.5 0 0 0 12.68 8.56 H19.5 A1.5 1.5 0 0 1 21 10.06 V18.5 A1.5 1.5 0 0 1 19.5 20 H4.5 A1.5 1.5 0 0 1 3 18.5 Z",
                NavigationPage.Groups => "M11.12 2.6c.56-.26 1.2-.26 1.76 0l8.2 3.79c.32.15.52.47.52.82s-.2.67-.52.82l-8.2 3.79c-.56.26-1.2.26-1.76 0L2.92 8.03c-.32-.15-.52-.47-.52-.82s.2-.67.52-.82zM4.2 10.59l6.16 2.85c1.04.48 2.24.48 3.27 0L19.8 10.59l1.28.59c.32.15.52.47.52.82s-.2.67-.52.82l-8.2 3.79c-.56.26-1.2.26-1.76 0l-8.2-3.79c-.32-.15-.52-.47-.52-.82s.2-.67.52-.82l1.28-.59zM2.92 15.98l1.28-.59 6.16 2.85c1.04.48 2.24.48 3.27 0l6.16-2.85 1.28.59c.32.15.52.47.52.82s-.2.67-.52.82l-8.2 3.79c-.56.26-1.2.26-1.76 0l-8.19-3.79c-.32-.15-.52-.47-.52-.82s.2-.67.52-.82",
                NavigationPage.Connections => "M6 9.5a3 3 0 1 0 0-6a3 3 0 0 0 0 6Zm2.5 6H3.5v5h5z M12.84 6.5H21v11.5h-8.09",
                NavigationPage.Logs => "M6 3 H18 A2 2 0 0 1 20 5 V19 A2 2 0 0 1 18 21 H6 A2 2 0 0 1 4 19 V5 A2 2 0 0 1 6 3 Z M8 8 H16 V10 H8 Z M8 12 H16 V14 H8 Z M8 16 H13 V18 H8 Z",
                NavigationPage.Settings => "M10.825 22q-.675 0-1.162-.45t-.588-1.1L8.85 18.8q-.325-.125-.612-.3t-.563-.375l-1.55.65q-.625.275-1.25.05t-.975-.8l-1.175-2.05q-.35-.575-.2-1.225t.675-1.075l1.325-1Q4.5 12.5 4.5 12.337v-.675q0-.162.025-.337l-1.325-1Q2.675 9.9 2.525 9.25t.2-1.225L3.9 5.975q.35-.575.975-.8t1.25.05l1.55.65q.275-.2.575-.375t.6-.3l.225-1.65q.1-.65.588-1.1T10.825 2h2.35q.675 0 1.163.45t.587 1.1l.225 1.65q.325.125.613.3t.562.375l1.55-.65q.625-.275 1.25-.05t.975.8l1.175 2.05q.35.575.2 1.225t-.675 1.075l-1.325 1q.025.175.025.338v.674q0 .163-.05.338l1.325 1q.525.425.675 1.075t-.2 1.225l-1.2 2.05q-.35.575-.975.8t-1.25-.05l-1.5-.65q-.275.2-.575.375t-.6.3l-.225 1.65q-.1.65-.587 1.1t-1.163.45zm1.225-6.5q1.45 0 2.475-1.025T15.55 12t-1.025-2.475T12.05 8.5q-1.475 0-2.488 1.025T8.55 12t1.013 2.475T12.05 15.5",
                _ => ""
            };
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NavigationTitleConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is NavigationPage page)
        {
            return page.GetTitle();
        }
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class NavigationIndicatorMarginConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is double offset && !double.IsNaN(offset) && !double.IsInfinity(offset))
        {
            return new Thickness(0, offset, 0, 0);
        }

        return new Thickness(0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class ResponsiveItemWidthConverter : Avalonia.Data.Converters.IValueConverter
{
    private const double MinCardWidth = 200;
    private const double CardGap = 8;
    private const int MinColumns = 2;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double availableWidth || double.IsNaN(availableWidth) || availableWidth <= 0)
        {
            return 220d;
        }

        var reservedWidth = 0d;
        if (parameter is string parameterText &&
            double.TryParse(parameterText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedReservedWidth) &&
            parsedReservedWidth > 0)
        {
            reservedWidth = parsedReservedWidth;
        }

        availableWidth = Math.Max(0, availableWidth - reservedWidth);

        var columns = Math.Max(
            MinColumns,
            (int)Math.Floor((availableWidth + CardGap) / (MinCardWidth + CardGap)));

        return Math.Floor((availableWidth - (columns - 1) * CardGap) / columns);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}

public class LatencyForegroundConverter : Avalonia.Data.Converters.IValueConverter
{
    private static readonly IBrush LowLatencyBrush = new SolidColorBrush(Color.Parse("#16A34A"));
    private static readonly IBrush MediumLatencyBrush = new SolidColorBrush(Color.Parse("#CA8A04"));
    private static readonly IBrush HighLatencyBrush = new SolidColorBrush(Color.Parse("#DC2626"));
    private static readonly IBrush EmptyLatencyBrush = Brushes.Gray;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int latency || latency <= 0)
        {
            return EmptyLatencyBrush;
        }

        if (latency < 400)
        {
            return LowLatencyBrush;
        }

        if (latency < 800)
        {
            return MediumLatencyBrush;
        }

        return HighLatencyBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
