using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Unicord.Universal.Dialogs;
using Unicord.Universal.Extensions;
using Unicord.Universal.Services;
using Unicord.Universal.Utilities;
using Windows.Security.Credentials;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unicord.Universal.Pages
{
    public sealed partial class LoginPage : Page
    {
        public LoginPage()
        {
            InitializeComponent();
            UpdateLanguageUi();
        }

        private void UpdateLanguageUi()
        {
            LanguageLabel.Text = Loc.T("Login_Language");
            var active = AppLanguageService.ActiveLanguageCode;
            var accent = Application.Current.Resources["SystemControlHighlightAccentBrush"] as Windows.UI.Xaml.Media.Brush;
            var transparent = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            var buttons = new[] { LangEnButton, LangRuButton, LangUkButton };
            foreach (var btn in buttons)
            {
                var isSelected = string.Equals(btn.Tag as string, active, StringComparison.OrdinalIgnoreCase);
                btn.BorderThickness = new Thickness(isSelected ? 2 : 1);
                if (isSelected && accent != null)
                    btn.BorderBrush = accent;
                else
                    btn.ClearValue(Control.BorderBrushProperty);
                if (!isSelected)
                    btn.Background = transparent;
                else
                    btn.ClearValue(Control.BackgroundProperty);
            }
        }

        private async void LanguageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is string code))
                return;
            if (string.Equals(code, AppLanguageService.ActiveLanguageCode, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(AppLanguageService.CurrentOverride))
                return;

            AppLanguageService.SetLanguage(code);
            await Task.Delay(50);
            if (Frame != null)
            {
                Frame.Navigate(typeof(LoginPage));
                if (Frame.BackStack.Count > 0)
                    Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
            }
            else
            {
                UpdateLanguageUi();
            }
        }

        private async void TokenLoginButton_Click(object sender, RoutedEventArgs e)
        {
            var mainPage = this.FindParent<MainPage>();
            mainPage?.ShowConnectingOverlay();

            var dialog = new TokenDialog();
            await dialog.ShowAsync();

            if (!string.IsNullOrWhiteSpace(dialog.Token))
            {
                await TryLoginAsync(dialog.Token);
            }
            else
            {
                mainPage?.HideConnectingOverlay();
            }
        }

        private async Task TryLoginAsync(string token)
        {
            Task OnReady(DiscordClient client, ReadyEventArgs e)
            {
                var vault = new PasswordVault();
                vault.Add(new PasswordCredential(Constants.TOKEN_IDENTIFIER, "Default", token));

                return Task.CompletedTask;
            }

            // Discord can reject a token (close code 4004) before LoginAsync even returns; the error handler then goes
            // back to this page, and navigating on afterwards left an empty main page without an account
            var failed = false;
            Task OnError(Exception ex)
            {
                failed = true;
                return App.LoginError(ex);
            }

            var mainPage = this.FindParent<MainPage>();

            try
            {
                token = token.Trim('"').Trim();

                if (string.IsNullOrWhiteSpace(token))
                    throw new ArgumentException("Your token cannot be empty! If you were logging in via the browser, try using your token.");

                mainPage.ShowConnectingOverlay();
                await DiscordManager.LoginAsync(token, OnReady, OnError, false);

                if (!failed && DiscordManager.Discord != null)
                    Frame.Navigate(typeof(DiscordPage));
            }
            catch (Exception ex)
            {
                await UIUtilities.ShowErrorDialogAsync("Failed to login!", ex.Message);
                mainPage.HideConnectingOverlay();
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            var mainPage = this.FindParent<MainPage>();
            mainPage.HideConnectingOverlay();
        }
    }
}
