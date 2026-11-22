using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Microsoft.Extensions.Logging;
using Unicord.Universal.Extensions;
using Unicord.Universal.Integration;
using Unicord.Universal.Models;
using Unicord.Universal.Models.Channels;
using Unicord.Universal.Models.Guild;
using Unicord.Universal.Models.Messages;
using Unicord.Universal.Models.Voice;
using Unicord.Universal.Pages.Subpages;
using Unicord.Universal.Services;
using Unicord.Universal.Shared;
using Unicord.Universal.Utilities;
using Windows.Foundation.Metadata;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using MUXC = Microsoft.UI.Xaml.Controls;

namespace Unicord.Universal.Pages
{
    public sealed partial class DiscordPage : Page
    {
        public Frame MainFrame => mainFrame;
        public Frame LeftSidebarFrame => leftSidebarFrame;
        public Frame RightSidebarFrame => rightSidebarFrame;

        private ILogger<DiscordPage> _logger
            = Logger.GetLogger<DiscordPage>();
        private MainPageArgs _args;
        private bool _loaded;
        private bool _ready;

        internal DiscordPageViewModel Model { get; }
        internal bool IsWindowVisible { get; private set; }

        public DiscordPage()
        {
            InitializeComponent();
            Model = DataContext as DiscordPageViewModel;

            IsWindowVisible = Window.Current.Visible;
            Window.Current.VisibilityChanged += Current_VisibilityChanged;

            WeakReferenceMessenger.Default.Register<DiscordPage, ReadyEventArgs>(this, (r, e) => r.OnReady(e.Event));
            WeakReferenceMessenger.Default.Register<DiscordPage, MessageCreateEventArgs>(this, (r, e) => r.Notification_MessageCreated(e.Event));
        }

        private void Current_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            IsWindowVisible = e.Visible;
            UpdateTitleBar();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            UpdateTitleBar();

            if (e.Parameter is MainPageArgs args)
            {
                _args = args;

                if (_loaded && _ready && _args.ChannelId != 0)
                {
                    if (DiscordManager.Discord.TryGetCachedChannel(_args.ChannelId, out var channel))
                    {
                        var service = DiscordNavigationService.GetForCurrentView();
                        await service.NavigateAsync(channel);
                    }
                }
            }
        }

        private async Task OnReady(ReadyEventArgs e)
        {
            if (_ready) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () => await LoadAsync());
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (DiscordManager.Discord == null)
                return; // im not 100% sure why this gets called on logout but it does so

            if (_loaded) return;
            _loaded = true;

            try
            {
                UpdateTitleBar();

                // Metro pages animate themselves (turnstile, MetroTransitions); the frame's own entrance animation
                // would play on top of it
                if (Themes.ThemeResources.LoadedTheme == AppTheme.OLED)
                    MainFrame.ContentTransitions = new Windows.UI.Xaml.Media.Animation.TransitionCollection();

                // Start on the Metro hub (profile + tiles); its "start" tile opens the channel list.
                LeftSidebarFrame.Navigate(typeof(DMChannelsPage));
                MainFrame.Navigate(typeof(HubPage));
                SplitPaneService.GetForCurrentView().CloseAllPanes();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Something went wrong when loading DiscordPage");
                await UIUtilities.ShowErrorDialogAsync("An error has occured.", ex.Message);
            }
        }

        private async Task LoadAsync()
        {
            _ready = true;
            PerfTimings.Mark("экран подключения скрыт");

            this.FindParent<MainPage>()
                .HideConnectingOverlay();
            try
            {
                var service = DiscordNavigationService.GetForCurrentView();
                if (_args != null && _args.ChannelId != 0 && DiscordManager.Discord.TryGetCachedChannel(_args.ChannelId, out var channel))
                {
                    await service.NavigateAsync(channel);
                }
                else
                {
                    Model.IsFriendsSelected = true;
                    //LeftSidebarFrame.Navigate(typeof(DMChannelsPage));
                    //MainFrame.Navigate(typeof(FriendsPage));
                }

                var possibleConnection = await VoiceConnectionModel.FindExistingConnectionAsync();
                if (possibleConnection != null)
                {
                    (DataContext as DiscordPageViewModel).VoiceModel = possibleConnection;
                }

                var notificationService = BackgroundNotificationService.GetForCurrentView();
                await notificationService.StartupAsync();
                FriendsTileService.Push();

                // subscribed last, so the navigation service's own history and the call/stream pages go first
                SystemNavigationManager.GetForCurrentView().BackRequested += BackToHub;

                _ = Task.Run(ContactListManager.UpdateContactsListAsync);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failure when processing READY");
                await UIUtilities.ShowErrorDialogAsync("An error has occured.", ex.Message);
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
        }

        // Hardware/system back like Windows Phone: back through the pages opened in the main area
        // (chat -> server -> servers -> hub); on the hub itself the app closes as usual.
        // The call and stream pages handle their own back; the old channel-list pane closes first on phones.
        private void BackToHub(object sender, BackRequestedEventArgs e)
        {
            try
            {
                if (e.Handled || MainFrame.Content is VoiceChannelPage || MainFrame.Content is StreamPage)
                    return;

                var overlay = OverlayService.GetForCurrentView();
                if (overlay.IsOverlayVisible)
                {
                    e.Handled = true;
                    overlay.CloseOverlay();
                    return;
                }

                var panes = SplitPaneService.GetForCurrentView();
                if (panes.ViewMode == ViewMode.SinglePaneLeft)
                {
                    e.Handled = true;
                    panes.CloseAllPanes();
                    if (!(MainFrame.Content is HubPage) && !MainFrame.CanGoBack)
                        MainFrame.Navigate(typeof(HubPage));
                    return;
                }

                if (MainFrame.Content is HubPage)
                    return; // leave the app

                e.Handled = true;
                if (MainFrame.CanGoBack)
                    MainFrame.GoBack();
                else
                    MainFrame.Navigate(typeof(HubPage));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log($"Назад ({MainFrame.Content?.GetType().Name})", ex);
            }
        }

        // Tapping our own avatar/name at the bottom opens our profile (banner, badges, bio), like the official client.
        // The settings button inside the pill handles its own taps.
        private async void UserPill_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                var me = DiscordManager.Discord?.CurrentUser;
                if (me == null)
                    return;
                await OverlayService.GetForCurrentView()
                    .ShowOverlayAsync<Overlay.UserInfoOverlayPage>(new Models.User.UserViewModel(me.Id, null));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Свой профиль", ex);
            }
        }

        private async Task Notification_MessageCreated(MessageCreateEventArgs e)
        {
            if (!WindowingService.Current.IsChannelVisible(e.Channel.Id) &&
                NotificationUtils.WillShowToast(DiscordManager.Discord, e.Message) &&
                IsWindowVisible)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ShowNotification(e.Message));
            }
        }

        private void ShowNotification(DiscordMessage message)
        {
            notification.Show(new MessageViewModel(message), 7_000);
        }

        private async void Notification_Tapped(object sender, TappedRoutedEventArgs e)
        {
            //var message = (((InAppNotification)sender).Content as MessageControl)?.MessageViewModel;
            var message = notification.Content as MessageViewModel;
            if (message != null)
            {
                var service = DiscordNavigationService.GetForCurrentView();
                await service.NavigateAsync(message.Channel.Channel);
            }
        }

        private async void UnreadDms_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Model.Navigating)
                return;

            if (e.AddedItems.FirstOrDefault() is ChannelViewModel c)
            {
                await DiscordNavigationService.GetForCurrentView().NavigateAsync(c.Channel);
            }
        }

        private void mainFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var service = FullscreenService.GetForCurrentView();
            if (service.IsFullscreenMode)
            {
                service.LeaveFullscreen();
            }
        }

        private async void friendsItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var service = DiscordNavigationService.GetForCurrentView();
            await service.NavigateAsync(null);
        }

        private async void SettingsItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var service = SettingsService.GetForCurrentView();
            await service.OpenAsync();
        }

        private async void TreeView_ItemInvoked(MUXC.TreeView sender, MUXC.TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is GuildListFolderViewModel viewModel)
            {
                viewModel.IsExpanded = !viewModel.IsExpanded;
            }

            if (args.InvokedItem is GuildListViewModel guildVM)
            {
                if (Model.Navigating)
                    return;

                if (!guildVM.Guild.IsUnavailable)
                {
                    if (App.LocalSettings.Read(Constants.ENABLE_GUILD_BROWSING, Constants.ENABLE_GUILD_BROWSING_DEFAULT))
                    {
                        LeftSidebarFrame.Navigate(typeof(GuildChannelListPage), guildVM.Guild);
                    }
                    else
                    {
                        var channelId = App.RoamingSettings.Read($"GuildPreviousChannels::{guildVM.Guild.Id}", 0UL);
                        if (!guildVM.Guild.Channels.TryGetValue(channelId, out var channel) || (!channel.IsAccessible() || !channel.IsText()))
                        {
                            channel = guildVM.Guild.Channels.Values
                                .Where(c => c.IsAccessible())
                                .Where(c => c.IsText())
                                .OrderBy(c => c.Position)
                                .FirstOrDefault();
                        }

                        if (await WindowingService.Current.ActivateOtherWindowAsync(channel))
                            LeftSidebarFrame.Navigate(typeof(GuildChannelListPage), guildVM.Guild);
                        else
                            await DiscordNavigationService.GetForCurrentView()
                                .NavigateAsync(channel);
                    }

                    Model.IsFriendsSelected = false;

                    // Close sidebar in narrow mode after guild selection
                    if (Window.Current.Bounds.Width <= SplitPaneService.TWO_PANE_BREAKPOINT)
                    {
                        SplitPaneService.GetForCurrentView().ToggleLeftPane();
                    }
                }
                else
                {
                    await UIUtilities.ShowErrorDialogAsync("ServerUnavailableTitle", "ServerUnavailableMessage");
                }
            }

            sender.SelectedItem = null;
        }

        private void CloseItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SplitPaneService.GetForCurrentView()
                .ToggleLeftPane();
        }

        internal void SetViewMode(ViewMode viewMode)
        {
            Debug.WriteLine(viewMode);
            VisualStateManager.GoToState(this, viewMode.ToString(), true);
        }

        private void UpdateTitleBar()
        {
            if (ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                WindowingService.Current.HandleTitleBarForControl(sidebarMainGrid, true);
                WindowingService.Current.HandleTitleBarForControl(sidebarSecondaryGrid, true);
                WindowingService.Current.HandleTitleBarForControl(MainContent, true);
                TitleBarGrid.Visibility = Visibility.Collapsed;
            }
            else
            {
                WindowingService.Current.HandleTitleBarForControl(sidebarSecondaryGrid, true);
                WindowingService.Current.HandleTitleBarForControl(MainContent, true);
                TitleBarGrid.Visibility = Visibility.Visible;
            }
        }
    }
}