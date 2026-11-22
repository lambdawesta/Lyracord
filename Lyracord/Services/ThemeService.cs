using Microsoft.Toolkit.Extensions;
using Microsoft.Toolkit.Uwp.Helpers;

namespace Unicord.Universal.Services
{
    public enum AppTheme
    {
        Performance,
        Fluent,
        SunValley,
        OLED
    }

    internal class ThemeService : BaseService<ThemeService>
    {
        // Lyracord 1.14: the Metro theme becomes the phone default; existing installs switch once (the user can go back
        // in Settings > Appearance).
        private const string MetroMigrationKey = "MetroThemeDefaultV1";

        public AppTheme GetTheme()
        {
            if (!App.LocalSettings.TryRead<bool>(MetroMigrationKey, out _))
            {
                App.LocalSettings.Save(MetroMigrationKey, true);
                if (SystemInformation.Instance.DeviceFamily == "Windows.Mobile" && !App.LocalSettings.TryRead<int>("AppThemeSet", out _))
                    App.LocalSettings.Save("AppTheme", (int)AppTheme.OLED);
            }

            if (App.LocalSettings.TryRead<int>("AppThemeSet", out var value))
            {
                App.LocalSettings.Save("AppTheme", value);
                App.LocalSettings.Delete("AppThemeSet");
            }

            if (App.LocalSettings.TryRead<int>("AppTheme", out var theme))
                return (AppTheme)theme;

            var defaultTheme = GetDefaultAppTheme();
            App.LocalSettings.Save("AppTheme", (int)defaultTheme);

            return defaultTheme;
        }

        public AppTheme GetSettingsTheme()
        {
            if (App.LocalSettings.TryRead<int>("AppThemeSet", out var value))
            {
                return (AppTheme)value;
            }

            if (App.LocalSettings.TryRead<int>("AppTheme", out var theme))
                return (AppTheme)theme;

            return GetDefaultAppTheme();
        }

        public void SetThemeOnRelaunch(AppTheme theme)
        {
            App.LocalSettings.Save("AppThemeSet", (int)theme);
        }

        public AppTheme GetDefaultAppTheme()
        {
            var osBuild = SystemInformation.Instance.OperatingSystemVersion.Build;
            if (osBuild >= 22000)
                return AppTheme.SunValley;

            if (SystemInformation.Instance.DeviceFamily == "Windows.Mobile")
                return AppTheme.OLED; // Metro

            return AppTheme.Fluent;
        }
    }
}
