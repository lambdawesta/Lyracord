using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Models.Channels;
using Unicord.Universal.Models.Guild;
using Unicord.Universal.Models.Voice;
using Unicord.Universal.Pages;
using Unicord.Universal.Pages.Subpages;
using Unicord.Universal.Utilities;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Animation;

namespace Unicord.Universal.Services
{
    internal class NavigationEvent
    {
        public object args;
        public Action<NavigationEvent> action;
    }

    /// <summary>
    /// A service to handle navigation between channels and servers
    /// </summary>
    internal class DiscordNavigationService : BaseService<DiscordNavigationService>
    {
        private MainPage _mainPage;
        private DiscordPage _discordPage;
        private DiscordPageViewModel _discordPageModel;
        private SystemNavigationManager _navigation;
        private Stack<NavigationEvent> _navigationStack;

        protected override void Initialise()
        {
            base.Initialise();

            _mainPage = Window.Current.Content.FindChild<MainPage>();
            if (!(_mainPage.Arguments?.FullFrame ?? false))
            {
                _discordPage = Window.Current.Content.FindChild<DiscordPage>();
                _discordPageModel = _discordPage.DataContext as DiscordPageViewModel;

                _navigationStack = new Stack<NavigationEvent>();
                _navigation = SystemNavigationManager.GetForCurrentView();
                _navigation.BackRequested += OnBackRequested;
            }
        }

        /// <summary>Opens someone's Go Live stream from the voice channel we are in.</summary>
        internal async Task NavigateToStreamAsync(DiscordChannel channel, ulong userId, string displayName)
        {
            if (_discordPage == null)
                return;

            var voice = _discordPageModel?.VoiceModel;
            if (voice == null || voice.Channel?.Id != channel.Id)
            {
                await UIUtilities.ShowErrorDialogAsync(Loc.T("Nav_StreamTitle"), Loc.T("Nav_StreamJoinFirst"));
                return;
            }

            SplitPaneService.GetForCurrentView().CloseAllPanes();
            _discordPage.MainFrame.Navigate(typeof(StreamPage), new StreamPageArgs(channel, userId, displayName));
        }

        // TODO: this is kinda bad and should really be more in DiscordPageModel
        // TODO: messageID currently unused, here for forward compatbility
        internal async Task NavigateAsync(DiscordChannel channel, bool skipPreviousDm = false, ulong messageID = 0)
        {
            // TODO: handle this navigation in the main window
            var window = WindowingService.Current.GetHandle(_mainPage);
            if (_discordPage == null)
            {
                if (_mainPage.Arguments?.FullFrame != true)
                    return;

                if (channel.IsVoice())
                    throw new InvalidOperationException();


                if (await WindowingService.Current.ActivateOtherWindowAsync(channel, window))
                    return;

                if (channel.Guild != null)
                    await channel.Guild.SyncAsync();

                if (channel.IsNSFW)
                {
                    var loader = ResourceLoader.GetForViewIndependentUse();
                    if (await WindowsHelloManager.VerifyAsync(Constants.VERIFY_NSFW, loader.GetString("VerifyNSFWDisplayReason")))
                    {
                        if (App.RoamingSettings.Read($"NSFW_{channel.Id}", false) == false || !App.RoamingSettings.Read($"NSFW_All", false))
                            _mainPage.RootFrame.Navigate(typeof(AgeGatePage), channel);
                        else
                            _mainPage.RootFrame.Navigate(typeof(ChannelPage), channel);
                    }
                }
                else
                {
                    if (channel is DiscordForumChannel forum)
                        _mainPage.RootFrame.Navigate(typeof(ForumChannelPage), forum);
                    else
                        _mainPage.RootFrame.Navigate(typeof(ChannelPage), channel);
                }

                _mainPage.HideCustomOverlay();
                _mainPage.HideConnectingOverlay();

                return;
            }

            OverlayService.GetForCurrentView().CloseOverlay();
            _mainPage.HideCustomOverlay();

            var page = _discordPage.MainFrame.Content as ChannelPage;
            if (channel == null)
            {

                if (_discordPageModel.SelectedGuild != null)
                    _discordPageModel.SelectedGuild.IsSelected = false;
                _discordPageModel.SelectedGuild = null;
                _discordPageModel.IsFriendsSelected = true;
                _discordPageModel.CurrentChannel = null;

                if (_discordPageModel.PreviousDM != null && (page?.ViewModel.Channel != _discordPageModel.PreviousDM.Channel) && !skipPreviousDm)
                {
                    _discordPageModel.SelectedDM = _discordPageModel.PreviousDM;
                    _discordPage.MainFrame.Navigate(typeof(ChannelPage), _discordPageModel.PreviousDM);
                    _discordPage.LeftSidebarFrame.Navigate(typeof(DMChannelsPage), _discordPageModel.PreviousDM, new DrillInNavigationTransitionInfo());
                }
                else if (page != null || !(_discordPage.LeftSidebarFrame.Content is DMChannelsPage))
                {
                    _discordPageModel.PreviousDM = null;
                    _discordPage.MainFrame.Navigate(typeof(FriendsPage));
                    _discordPage.LeftSidebarFrame.Navigate(typeof(DMChannelsPage), null, new DrillInNavigationTransitionInfo());
                }

                return;
            }

            if (_discordPageModel.CurrentChannel != channel && !channel.IsVoice())
            {

                _discordPageModel.Navigating = true;

                SplitPaneService.GetForCurrentView()
                    .CloseAllPanes();

                if (_discordPageModel.SelectedGuild != null)
                    _discordPageModel.SelectedGuild.IsSelected = false;

                _discordPageModel.SelectedGuild = null;
                _discordPageModel.SelectedDM = null;
                _discordPageModel.IsFriendsSelected = false;

                if (await WindowingService.Current.ActivateOtherWindowAsync(channel, window))
                    return;

                GuildListViewModel guildVm;
                if (channel is DiscordDmChannel dm)
                {
                    _discordPageModel.SelectedDM = _discordPageModel.PreviousDM = new ChannelViewModel(dm.Id);
                    _discordPageModel.IsFriendsSelected = true;
                    _discordPage.LeftSidebarFrame.Navigate(typeof(DMChannelsPage), channel, new DrillInNavigationTransitionInfo());
                }
                else if (channel.Guild != null && (guildVm = _discordPageModel.ViewModelFromGuild(channel.Guild)) != null)
                {
                    _discordPageModel.SelectedGuild = guildVm;
                    _discordPageModel.SelectedGuild.IsSelected = true;

                    if (_discordPage.LeftSidebarFrame.Content is not GuildChannelListPage p || p.Guild != channel.Guild)
                        _discordPage.LeftSidebarFrame.Navigate(typeof(GuildChannelListPage), channel.Guild, new DrillInNavigationTransitionInfo());

                    if (_discordPage.LeftSidebarFrame.Content is GuildChannelListPage g)
                    {
                        g.SetSelectedChannel(channel);
                    }

                    if (!channel.Guild.IsSynced)
                        await channel.Guild.SyncAsync();
                }

                if (channel.IsNSFW)
                {
                    var loader = ResourceLoader.GetForViewIndependentUse();
                    if (await WindowsHelloManager.VerifyAsync(Constants.VERIFY_NSFW, loader.GetString("VerifyNSFWDisplayReason")))
                    {
                        if (App.RoamingSettings.Read($"NSFW_{channel.Id}", false) == false || !App.RoamingSettings.Read($"NSFW_All", false))
                        {
                            _discordPage.MainFrame.Navigate(typeof(AgeGatePage), channel);
                        }
                        else
                        {
                            _discordPage.MainFrame.Navigate(typeof(ChannelPage), channel);
                        }
                    }
                }
                else
                {
                    if (channel is DiscordForumChannel forum)
                    {
                        _discordPage.MainFrame.Navigate(typeof(ForumChannelPage), forum);
                    }
                    else
                    {
                        _discordPage.MainFrame.Navigate(typeof(ChannelPage), channel);
                    }
                }

                _discordPageModel.Navigating = false;
                _discordPageModel.CurrentChannel = channel;
            }
            else if (channel != null && channel.IsVoice())
            {
                await NavigateToVoiceCallAsync(channel);
            }

            if (_mainPage.IsOverlayShown)
                _mainPage.HideConnectingOverlay();
        }

        /// <summary>
        /// Opens the voice call view (<see cref="VoiceChannelPage"/>) and connects to the channel (guild voice, 1-on-1 DM, or Group DM).
        /// </summary>
        internal async Task NavigateToVoiceCallAsync(DiscordChannel channel)
        {
            if (channel == null || _discordPage == null)
                return;

            OverlayService.GetForCurrentView().CloseOverlay();
            _mainPage.HideCustomOverlay();

            // show the call view (tiles) for the channel, like the official client
            SplitPaneService.GetForCurrentView().CloseAllPanes();
            _discordPage.MainFrame.Navigate(typeof(VoiceChannelPage), channel);

            // already in this channel: just show it, do not reconnect
            if (_discordPageModel.VoiceModel?.Channel?.Id == channel.Id)
            {
                if (_mainPage.IsOverlayShown)
                    _mainPage.HideConnectingOverlay();
                return;
            }

            if (_discordPageModel.VoiceModel != null)
                await _discordPageModel.VoiceModel.DisconnectAsync();

            try
            {
                var voice = new VoiceConnectionModel(channel);
                _discordPageModel.VoiceModel = voice;
                await voice.ConnectAsync();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex);
                await UIUtilities.ShowErrorDialogAsync("Failed to connect to voice!", ex.Message);
            }

            if (_mainPage.IsOverlayShown)
                _mainPage.HideConnectingOverlay();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            var overlay = OverlayService.GetForCurrentView();
            if (overlay.IsOverlayVisible)
            {
                e.Handled = true;
                overlay.CloseOverlay();
                return;
            }

            if (_navigationStack.TryPop(out var ev))
            {
                e.Handled = true;
                ev.action(ev);
            }
        }
    }
}
