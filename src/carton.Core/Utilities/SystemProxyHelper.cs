using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace carton.Core.Utilities;

/// <summary>
/// Provides helpers to set or clear the system proxy settings.
/// Supports Windows (registry + WinINet), GNOME (gsettings), and KDE (kwriteconfig5/6).
/// All calls are best-effort: errors are silently swallowed.
/// </summary>
public static class SystemProxyHelper
{
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private const string InternetSettingsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [SupportedOSPlatform("windows")]
    [DllImport("wininet.dll", SetLastError = true, EntryPoint = "InternetSetOptionW")]
    private static extern bool InternetSetOptionW(
        IntPtr hInternet,
        int dwOption,
        IntPtr lpBuffer,
        int dwBufferLength);

    public static void SetSystemProxy(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            SetWindowsProxy(host, port);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            SetLinuxProxy(host, port);
        }
    }

    public static void ClearSystemProxy()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ClearWindowsProxy();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            ClearLinuxProxy();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindowsProxy(string host, int port)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"{host}:{port}", RegistryValueKind.String);
            }
        }
        catch
        {
        }

        NotifyWindowsProxyChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void ClearWindowsProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (key != null)
            {
                key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", string.Empty, RegistryValueKind.String);
            }
        }
        catch
        {
        }

        NotifyWindowsProxyChanged();
    }

    [SupportedOSPlatform("windows")]
    private static void NotifyWindowsProxyChanged()
    {
        try
        {
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOptionW(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch
        {
        }
    }

    private static void SetLinuxProxy(string host, int port)
    {
        TrySetGnomeProxy(host, port);
        TrySetKdeProxy(host, port);
    }

    private static void ClearLinuxProxy()
    {
        TryClearGnomeProxy();
        TryClearKdeProxy();
    }

    private static void TrySetGnomeProxy(string host, int port)
    {
        try
        {
            RunCommand("gsettings", "set org.gnome.system.proxy mode manual");
            RunCommand("gsettings", $"set org.gnome.system.proxy.http host '{host}'");
            RunCommand("gsettings", $"set org.gnome.system.proxy.http port {port}");
            RunCommand("gsettings", $"set org.gnome.system.proxy.https host '{host}'");
            RunCommand("gsettings", $"set org.gnome.system.proxy.https port {port}");
        }
        catch
        {
        }
    }

    private static void TryClearGnomeProxy()
    {
        try
        {
            RunCommand("gsettings", "set org.gnome.system.proxy mode none");
        }
        catch
        {
        }
    }

    private static void TrySetKdeProxy(string host, int port)
    {
        var tool = FindExecutable("kwriteconfig6") ?? FindExecutable("kwriteconfig5");
        if (tool == null)
        {
            return;
        }

        try
        {
            var proxyValue = $"http://{host} {port}";
            RunCommand(tool, "--file kioslaverc --group \"Proxy Settings\" --key ProxyType 1");
            RunCommand(tool, $"--file kioslaverc --group \"Proxy Settings\" --key httpProxy \"{proxyValue}\"");
            RunCommand(tool, $"--file kioslaverc --group \"Proxy Settings\" --key httpsProxy \"{proxyValue}\"");
            RunCommandIgnoreResult("kbuildsycoca6");
            RunCommandIgnoreResult("kbuildsycoca5");
        }
        catch
        {
        }
    }

    private static void TryClearKdeProxy()
    {
        var tool = FindExecutable("kwriteconfig6") ?? FindExecutable("kwriteconfig5");
        if (tool == null)
        {
            return;
        }

        try
        {
            RunCommand(tool, "--file kioslaverc --group \"Proxy Settings\" --key ProxyType 0");
            RunCommandIgnoreResult("kbuildsycoca6");
            RunCommandIgnoreResult("kbuildsycoca5");
        }
        catch
        {
        }
    }

    private static void RunCommand(string executable, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit(3000);
    }

    private static void RunCommandIgnoreResult(string executable)
    {
        try
        {
            RunCommand(executable, string.Empty);
        }
        catch
        {
        }
    }

    private static string? FindExecutable(string name)
    {
        try
        {
            using var which = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = name,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            which.Start();
            var output = which.StandardOutput.ReadToEnd().Trim();
            which.WaitForExit(2000);
            return which.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
