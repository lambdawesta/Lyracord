using System;
using System.Collections.Generic;
using Windows.ApplicationModel.Resources.Core;
using Windows.Globalization;
using Windows.Storage;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Runtime language switcher for Lyracord's full localisations (English, Russian, Ukrainian) plus System default.
    /// Persists the override in both ApplicationLanguages.PrimaryLanguageOverride and LocalSettings so it applies
    /// immediately on the next navigation and survives restarts.
    /// </summary>
    internal static class AppLanguageService
    {
        public const string SettingKey = "AppLanguage";

        public static readonly (string Code, string NativeName)[] Supported =
        {
            ("en-US", "English"),
            ("ru-RU", "Русский"),
            ("uk-UA", "Українська"),
        };

        /// <summary>Current saved override ("" = follow Windows language, otherwise "en-US" / "ru-RU" / "uk-UA").</summary>
        public static string CurrentOverride
        {
            get
            {
                try
                {
                    var values = ApplicationData.Current.LocalSettings.Values;
                    if (values.TryGetValue(SettingKey, out var raw) && raw is string saved)
                        return saved;
                }
                catch (Exception) { }

                try
                {
                    return ApplicationLanguages.PrimaryLanguageOverride ?? string.Empty;
                }
                catch (Exception)
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>The effective full language code currently active ("en-US", "ru-RU", or "uk-UA").</summary>
        public static string ActiveLanguageCode
        {
            get
            {
                var ov = CurrentOverride;
                if (!string.IsNullOrEmpty(ov))
                    return Normalize(ov);

                try
                {
                    foreach (var lang in ApplicationLanguages.Languages)
                    {
                        var norm = Normalize(lang);
                        if (norm != null)
                            return norm;
                    }
                }
                catch (Exception) { }

                return "en-US";
            }
        }

        /// <summary>Applies any saved language override at startup (before XAML loads resources).</summary>
        public static void ApplySaved()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(SettingKey, out var raw) && raw is string saved)
                    ApplicationLanguages.PrimaryLanguageOverride = saved;
            }
            catch (Exception) { }
        }

        /// <summary>Sets the app language ("" = system) and resets UWP resource contexts so newly created pages switch immediately.</summary>
        public static void SetLanguage(string code)
        {
            code = code ?? string.Empty;
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = code;
            }
            catch (Exception) { }

            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = code;
            }
            catch (Exception) { }

            try { ResourceContext.GetForCurrentView().Reset(); } catch (Exception) { }
            try { ResourceContext.GetForViewIndependentUse().Reset(); } catch (Exception) { }
            Loc.Reset();
        }

        public static string DisplayName(string code)
        {
            if (string.IsNullOrEmpty(code))
                return Loc.T("Settings_LanguageSystem");
            foreach (var (c, name) in Supported)
            {
                if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
            return code;
        }

        private static string Normalize(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;
            if (tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                return "ru-RU";
            if (tag.StartsWith("uk", StringComparison.OrdinalIgnoreCase))
                return "uk-UA";
            if (tag.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                return "en-US";
            return null;
        }
    }
}
