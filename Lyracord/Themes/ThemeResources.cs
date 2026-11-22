using System;
using Unicord.Universal.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls.Primitives;

namespace Unicord.Universal.Themes
{
    internal class ThemeResources : ResourceDictionary
    {
        /// <summary>The theme whose resources were actually loaded at start-up (settings may change later).</summary>
        internal static AppTheme? LoadedTheme { get; private set; }

        public ThemeResources()
        {
            var theme = ThemeService.GetForCurrentView()
                .GetTheme();
            LoadedTheme = theme;
            try { VoiceDiagnostics.Log($"Тема оформления: {theme}"); } catch (Exception) { }

            Uri uri = theme switch
            {
                AppTheme.OLED => new Uri("ms-appx:///Themes/Styles/OLED.xaml"),
                AppTheme.Fluent => new Uri("ms-appx:///Themes/Styles/Fluent.xaml"),
                AppTheme.Performance => new Uri("ms-appx:///Themes/Styles/Performance.xaml"),
                AppTheme.SunValley => new Uri("ms-appx:///Themes/Styles/SunValley.xaml"),
                _ => throw new InvalidOperationException("Unknown theme"),
            };

            Application.LoadComponent(this, uri, ComponentResourceLocation.Application);
        }
    }
}
