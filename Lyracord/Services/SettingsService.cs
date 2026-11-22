using System.Threading.Tasks;
using Unicord.Universal.Extensions;
using Unicord.Universal.Pages;
using Unicord.Universal.Pages.Settings;
using Unicord.Universal.Themes;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;

namespace Unicord.Universal.Services
{
    internal enum SettingsPageType
    {
        Accounts,
        Messaging, 
        Notifications,
        Themes,
        Media,
        Voice,
        Security,
        About
    }

    internal class SettingsService : BaseService<SettingsService>
    {
        private OverlayService _overlayService;
        //private DiscordPage _discordPage;
        protected override void Initialise()
        {
            _overlayService = OverlayService.GetForCurrentView();
            //_discordPage = Window.Current.Content.FindChild<DiscordPage>();
        }

        public async Task OpenAsync(SettingsPageType page = SettingsPageType.Accounts)
        {
            var loader = ResourceLoader.GetForViewIndependentUse();
            if (await WindowsHelloManager.VerifyAsync(Constants.VERIFY_SETTINGS, loader.GetString("VerifySettingsDisplayReason")))
            {
                // In Metro (OLED) theme, open settings sections directly on the unified Metro page instead of the WinUI NavigationView overlay.
                if (ThemeResources.LoadedTheme == AppTheme.OLED)
                {
                    var mainFrame = Window.Current.Content.FindChild<DiscordPage>()?.MainFrame;
                    if (mainFrame != null)
                    {
                        if (page == SettingsPageType.About)
                            mainFrame.Navigate(typeof(AboutHubPage));
                        else
                            mainFrame.Navigate(typeof(MetroSettingsSectionPage), page);
                        return;
                    }
                }

                await _overlayService.ShowOverlayAsync<SettingsPage>(page);
            }
        }
    }
}
