using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace carton.GUI.Services;

public interface IStartupService
{
    void ApplyStartAtLoginPreference(bool enabled);
    bool IsStartAtLoginEnabled();
}

public sealed class StartupService : IStartupService
{
    private const string WindowsRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string AppName = "Carton";

    public void ApplyStartAtLoginPreference(bool enabled)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true) ??
                            Registry.CurrentUser.CreateSubKey(WindowsRunKey);
            if (key == null)
            {
                return;
            }

            if (enabled)
            {
                var executablePath = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(executablePath))
                {
                    return;
                }

                key.SetValue(AppName, $"\"{executablePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch
        {
            // Ignore registry failures because startup control is best-effort.
        }
    }

    public bool IsStartAtLoginEnabled()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: false);
            if (key == null)
            {
                return false;
            }

            return key.GetValue(AppName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }
}
