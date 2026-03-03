using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace carton.Core.Models;

public static class AppLanguageHelper
{
    public static AppLanguage GetSystemDefaultLanguage()
    {
        try
        {
            string? tag = GetRawLanguageTag();
            if (string.IsNullOrWhiteSpace(tag))
                return AppLanguage.English;

            // BCP47 "zh-CN" 或 POSIX "zh_CN.UTF-8" → 取第一段
            string lang = tag.Split('-', '_')[0].ToLowerInvariant();
            return MapLangCodeToLanguage(lang);
        }
        catch
        {
            return AppLanguage.English;
        }
    }

    private static string? GetRawLanguageTag()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return GetWindowsLanguage();

        // Linux / macOS 都有 locale 命令
        return RunLocaleCommand();
    }

    // ── Windows: GetUserDefaultLocaleName (kernel32) ──────────

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultLocaleName(
        [Out] StringBuilder lpLocaleName, int cchLocaleName);

    private static string? GetWindowsLanguage()
    {
        const int LOCALE_NAME_MAX_LENGTH = 85;
        var sb = new StringBuilder(LOCALE_NAME_MAX_LENGTH);
        int len = GetUserDefaultLocaleName(sb, LOCALE_NAME_MAX_LENGTH);
        return len > 0 ? sb.ToString() : null;
    }

    // ── Linux / macOS: locale 命令 ────────────────────────────

    /// <summary>
    /// 运行 <c>locale</c> 命令并从输出中提取 LANG 值。
    /// 输出示例：
    ///   LANG=zh_CN.UTF-8
    ///   LC_MESSAGES="zh_CN.UTF-8"
    /// </summary>
    private static string? RunLocaleCommand()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "locale",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            // 按优先级依次尝试 LANG / LC_MESSAGES / LC_ALL
            foreach (string key in new[] { "LANG", "LC_MESSAGES", "LC_ALL" })
            {
                string? val = ParseLocaleValue(output, key);
                if (!string.IsNullOrEmpty(val) && val != "C" && val != "POSIX")
                    return val;
            }
        }
        catch { /* locale 命令不存在时静默失败 */ }

        return null;
    }

    /// <summary>从 locale 输出中解析指定 key 的值，去除引号。</summary>
    private static string? ParseLocaleValue(string output, string key)
    {
        string prefix = key + "=";
        foreach (string line in output.Split('\n'))
        {
            ReadOnlySpan<char> s = line.AsSpan().Trim();
            if (s.StartsWith(prefix.AsSpan(), StringComparison.Ordinal))
            {
                string val = line[(line.IndexOf('=') + 1)..].Trim().Trim('"');
                return string.IsNullOrEmpty(val) ? null : val;
            }
        }
        return null;
    }

    // ── 语言映射 ──────────────────────────────────────────────

    private static AppLanguage MapLangCodeToLanguage(string twoLetterCode) =>
        twoLetterCode switch
        {
            "zh" => AppLanguage.SimplifiedChinese,
            // 按需添加更多语言
            _ => AppLanguage.English,
        };
}
