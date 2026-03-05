namespace carton.Core.Utilities;

public static class FormatHelper
{
    private static readonly string[] ByteSuffixes = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(long bytes)
    {
        var index = 0;
        double value = bytes;
        while (value >= 1024 && index < ByteSuffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.##} {ByteSuffixes[index]}";
    }
}
