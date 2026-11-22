using System;
using Unicord.Universal.Extensions;
using Unicord.Universal.Services;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Metro settings list opened from the hub's "settings" button: each item opens that section of the regular
    /// settings (or the Metro profile editor), instead of always landing on the account page.
    /// </summary>
    public sealed partial class SettingsHubPage : Page
    {
        public SettingsHubPage()
        {
            InitializeComponent();

            Add(Loc.T("Settings_Profile"), Loc.T("Settings_Profile_Desc"), () => OpenPage(typeof(ProfileEditPage)));
            Add(Loc.T("Settings_Account"), Loc.T("Settings_Account_Desc"), () => Open(SettingsPageType.Accounts));
            Add(Loc.T("Settings_Messages"), Loc.T("Settings_Messages_Desc"), () => Open(SettingsPageType.Messaging));
            Add(Loc.T("Settings_Notifications"), Loc.T("Settings_Notifications_Desc"), () => Open(SettingsPageType.Notifications));
            Add(Loc.T("Settings_Appearance"), Loc.T("Settings_Appearance_Desc"), () => Open(SettingsPageType.Themes));
            Add(Loc.T("Settings_Media"), Loc.T("Settings_Media_Desc"), () => Open(SettingsPageType.Media));
            Add(Loc.T("Settings_Voice"), Loc.T("Settings_Voice_Desc"), () => Open(SettingsPageType.Voice));
            Add(Loc.T("Settings_Security"), Loc.T("Settings_Security_Desc"), () => Open(SettingsPageType.Security));
            Add(Loc.T("Settings_About"), Loc.T("Settings_About_Desc"), () => OpenPage(typeof(AboutHubPage)));
            var animations = Add(Loc.T("Settings_Animations"), AnimationsText(), null);
            animations.Tapped += (s, e) =>
            {
                // Windows Phone turnstile and feather on page changes (MetroTransitions)
                var on = !App.LocalSettings.Read(MetroTransitions.SettingKey, true);
                App.LocalSettings.Save(MetroTransitions.SettingKey, on);
                ((TextBlock)animations.Children[1]).Text = AnimationsText();
            };
            var rpc = Add(Loc.T("Settings_RichPresence"), RichPresenceText(), null);
            rpc.Tapped += (s, e) =>
            {
                RichPresenceService.IsEnabled = !RichPresenceService.IsEnabled;
                ((TextBlock)rpc.Children[1]).Text = RichPresenceText();
                _ = RichPresenceService.ApplyAsync();
            };
            var language = Add(Loc.T("Settings_Language"), LanguageText(), null);
            language.Tapped += (s, e) => ShowLanguageFlyout(language);
            var cache = Add(Loc.T("Settings_Cache"), Loc.T("Settings_CacheLoading"), null);
            _cacheDesc = (TextBlock)cache.Children[1];
            cache.Tapped += async (s, e) =>
            {
                try
                {
                    await ImageDiskCache.ClearAsync();
                    _cacheDesc.Text = Loc.T("Settings_CacheCleared");
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Очистка кэша", ex);
                }
            };
            Add(Loc.T("Settings_Report"), Loc.T("Settings_Report_Desc"), CopyReport);
            var build = Add(Loc.T("Settings_Build"), BuildText(), null);
            build.Tapped += async (s, e) =>
            {
                await AskBuildNumberAsync();
                ((TextBlock)build.Children[1]).Text = BuildText();
            };
            var repo = Add(Loc.T("Settings_GitHubRepo"), GitHubRepoText(), null);
            repo.Tapped += async (s, e) =>
            {
                await AskGitHubRepoAsync();
                ((TextBlock)repo.Children[1]).Text = GitHubRepoText();
            };
        }

        private readonly TextBlock _cacheDesc;

        private async void RefreshCacheStats()
        {
            try
            {
                var (files, bytes) = await ImageDiskCache.GetStatsAsync();
                var mb = bytes / (1024.0 * 1024.0);
                _cacheDesc.Text = Loc.F("Settings_CacheStats", files, mb.ToString("0.0"));
            }
            catch (Exception)
            {
            }
        }

        private static string BuildText()
        {
            var saved = App.LocalSettings.Read(DiscordManager.ClientBuildSetting, 0);
            return saved > 0
                ? Loc.F("Settings_BuildCustom", saved)
                : Loc.F("Settings_BuildBuiltIn", DSharpPlus.DiscordClient.DefaultClientBuildNumber);
        }

        /// <summary>
        /// The official client build number Lyracord presents itself as. When Discord hides something new again, the
        /// current number (web client: settings, bottom of the left menu, "Stable NNNNNN") can be entered here.
        /// </summary>
        private static async System.Threading.Tasks.Task AskBuildNumberAsync()
        {
            var box = new TextBox
            {
                PlaceholderText = DSharpPlus.DiscordClient.DefaultClientBuildNumber.ToString(),
                InputScope = new Windows.UI.Xaml.Input.InputScope { Names = { new Windows.UI.Xaml.Input.InputScopeName(Windows.UI.Xaml.Input.InputScopeNameValue.Number) } }
            };
            var saved = App.LocalSettings.Read(DiscordManager.ClientBuildSetting, 0);
            if (saved > 0)
                box.Text = saved.ToString();
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("Settings_BuildHelp"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(box);
            var dialog = new ContentDialog
            {
                Title = Loc.T("Settings_Build"),
                Content = panel,
                PrimaryButtonText = Loc.T("Common_Save"),
                SecondaryButtonText = Loc.T("Common_Cancel")
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            var text = box.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                App.LocalSettings.Save(DiscordManager.ClientBuildSetting, 0);
            else if (int.TryParse(text, out var number) && number > 100000 && number < 10000000)
                App.LocalSettings.Save(DiscordManager.ClientBuildSetting, number);
        }

        private static string GitHubRepoText()
        {
            var repo = GitHubUpdateService.Repository;
            if (GitHubUpdateService.IsUpdateAvailable && GitHubUpdateService.LatestRelease != null)
                return $"github.com/{repo} · {Loc.F("Hub_UpdateAvailable", GitHubUpdateService.LatestRelease.DisplayVersion)}";
            return $"github.com/{repo} ({Loc.T("Settings_GitHubRepoDesc")})";
        }

        private static async System.Threading.Tasks.Task AskGitHubRepoAsync()
        {
            var box = new TextBox
            {
                PlaceholderText = GitHubUpdateService.DefaultRepository,
                Text = GitHubUpdateService.Repository
            };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("Settings_GitHubRepoHelp"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            });
            panel.Children.Add(box);
            var dialog = new ContentDialog
            {
                Title = Loc.T("Settings_GitHubRepo"),
                Content = panel,
                PrimaryButtonText = Loc.T("Common_Save"),
                SecondaryButtonText = Loc.T("Common_Cancel")
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;
            GitHubUpdateService.Repository = box.Text;
            _ = GitHubUpdateService.CheckForUpdatesAsync(forceRefresh: true);
        }

        private static string LanguageText()
            => AppLanguageService.DisplayName(AppLanguageService.CurrentOverride);

        private static string RichPresenceText()
            => RichPresenceService.IsEnabled ? Loc.T("Settings_RichPresenceOn") : Loc.T("Settings_RichPresenceOff");

        private void ShowLanguageFlyout(FrameworkElement anchor)
        {
            var flyout = new MenuFlyout();
            var current = AppLanguageService.CurrentOverride;

            void AddOption(string code, string label)
            {
                var selected = string.Equals(current, code, StringComparison.OrdinalIgnoreCase);
                var item = new MenuFlyoutItem { Text = selected ? "● " + label : label };
                item.Click += async (s, e) =>
                {
                    if (selected)
                        return;
                    AppLanguageService.SetLanguage(code);
                    await System.Threading.Tasks.Task.Delay(50);
                    var frame = Window.Current.Content.FindChild<DiscordPage>()?.MainFrame;
                    if (frame != null)
                    {
                        // Flush cached pages (HubPage, ServersHubPage) so they rebuild in the new language on Back.
                        var cacheSize = frame.CacheSize;
                        frame.CacheSize = 0;
                        frame.CacheSize = cacheSize;
                        frame.Navigate(typeof(SettingsHubPage));
                        if (frame.BackStack.Count > 0)
                            frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
                    }
                };
                flyout.Items.Add(item);
            }

            AddOption(string.Empty, Loc.T("Settings_LanguageSystem"));
            foreach (var (code, name) in AppLanguageService.Supported)
                AddOption(code, name);

            flyout.ShowAt(anchor);
        }

        private static string AnimationsText()
            => App.LocalSettings.Read(MetroTransitions.SettingKey, true)
                ? Loc.T("Settings_AnimationsOn")
                : Loc.T("Settings_AnimationsOff");

        private StackPanel Add(string title, string description, Action open)
        {
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 18), Background = new SolidColorBrush(Windows.UI.Colors.Transparent) };
            item.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiLight,
                FontSize = 30,
                Foreground = Brush(0xFFFFFF, 0x000000)
            });
            item.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush(0x9A9A9A, 0x6B6B6B)
            });

            item.RenderTransformOrigin = new Windows.Foundation.Point(0, 0.5);
            item.RenderTransform = new ScaleTransform();
            void Tilt(double scale) { var t = (ScaleTransform)item.RenderTransform; t.ScaleX = t.ScaleY = scale; }
            item.PointerPressed += (s, e) => Tilt(0.98);
            item.PointerReleased += (s, e) => Tilt(1);
            item.PointerExited += (s, e) => Tilt(1);
            item.PointerCanceled += (s, e) => Tilt(1);
            if (open != null)
                item.Tapped += (s, e) => open();

            Items.Children.Add(item);
            return item;
        }

        private Brush Brush(int dark, int light)
            => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? light : dark));

        private static async void Open(SettingsPageType page)
        {
            try
            {
                await SettingsService.GetForCurrentView().OpenAsync(page);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Настройки", ex);
            }
        }

        private static async void CopyReport()
        {
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(await VoiceDiagnostics.BuildReportAsync());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                await new Windows.UI.Popups.MessageDialog(Loc.T("Settings_ReportCopied"), Loc.T("Settings_ReportCopiedTitle")).ShowAsync();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Отчёт", ex);
            }
        }

        private static void OpenPage(Type page)
            => Window.Current.Content.FindChild<DiscordPage>()?.MainFrame.Navigate(page);

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            MetroTransitions.FeatherAfterLayout(Items);
            RefreshCacheStats();
        }
    }
}
