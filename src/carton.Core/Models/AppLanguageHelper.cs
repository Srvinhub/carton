using System;
using System.Globalization;

namespace carton.Core.Models;

public static class AppLanguageHelper
{
    public static AppLanguage GetSystemDefaultLanguage()
    {
        try
        {
            var culture = CultureInfo.CurrentUICulture;
            return MapCultureToLanguage(culture);
        }
        catch
        {
            return AppLanguage.English;
        }
    }

    private static AppLanguage MapCultureToLanguage(CultureInfo culture)
    {
        if (culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.SimplifiedChinese;
        }

        return AppLanguage.English;
    }
}
