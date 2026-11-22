using System;
using System.Collections.Generic;
using Unicord.Universal.Pages.Settings;
using Unicord.Universal.Services;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Hosts any settings subpage inside the pure Windows Phone Metro layout (pitch-black/white background,
    /// large Segoe UI Light lowercase title, flat hairline dividers, turnstile transition).
    /// </summary>
    public sealed partial class MetroSettingsSectionPage : Page
    {
        private static readonly Dictionary<SettingsPageType, (Type PageType, string TitleKey)> _sections
            = new Dictionary<SettingsPageType, (Type, string)>
            {
                [SettingsPageType.Accounts] = (typeof(AccountsSettingsPage), "Settings_Account"),
                [SettingsPageType.Messaging] = (typeof(MessagingSettingsPage), "Settings_Messages"),
                [SettingsPageType.Notifications] = (typeof(NotificationsSettingsPage), "Settings_Notifications"),
                [SettingsPageType.Themes] = (typeof(ThemesSettingsPage), "Settings_Appearance"),
                [SettingsPageType.Media] = (typeof(MediaSettingsPage), "Settings_Media"),
                [SettingsPageType.Voice] = (typeof(VoiceSettingsPage), "Settings_Voice"),
                [SettingsPageType.Security] = (typeof(SecuritySettingsPage), "Settings_Security"),
                [SettingsPageType.About] = (typeof(AboutHubPage), "Settings_About"),
            };

        public MetroSettingsSectionPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);

            var section = e.Parameter is SettingsPageType t ? t : SettingsPageType.Accounts;
            if (!_sections.TryGetValue(section, out var entry))
                entry = _sections[SettingsPageType.Accounts];

            TitleText.Text = Loc.T(entry.TitleKey);
            try
            {
                SectionFrame.Navigate(entry.PageType, null, new SuppressNavigationTransitionInfo());
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log($"Настройки ({section})", ex);
            }
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            base.OnNavigatingFrom(e);
            try
            {
                if (SectionFrame.Content is INotifyOnExit notify)
                    notify.OnClosing();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Настройки (закрытие)", ex);
            }
        }
    }
}
