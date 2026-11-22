using System;
using DSharpPlus.Entities;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Models.Voice;
using Unicord.Universal.Pages.Subpages;
using Unicord.Universal.Services;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Voice call view: one tile per person (green frame while speaking) plus a tile per Go Live stream, with its own
    /// toolbar (back, mute, deafen, settings, disconnect) because on phones the sidebar's voice panel is hidden here.
    /// </summary>
    public sealed partial class VoiceChannelPage : Page
    {
        private VoiceCallViewModel _model;
        private VoiceConnectionModel _subscribedVoice;

        public VoiceChannelPage()
        {
            InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.T("Voice_Back"));
            ToolTipService.SetToolTip(MuteToggle, Loc.T("Voice_Microphone"));
            ToolTipService.SetToolTip(DeafenToggle, Loc.T("Voice_Sound"));
            ToolTipService.SetToolTip(VoiceSettingsButton, Loc.T("Voice_Settings"));
            ToolTipService.SetToolTip(DisconnectButton, Loc.T("Voice_Disconnect"));
        }

        private static VoiceConnectionModel CurrentVoice =>
            (Window.Current.Content.FindChild<DiscordPage>()?.DataContext as DiscordPageViewModel)?.VoiceModel;

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            if (!(e.Parameter is DiscordChannel channel))
                return;

            _model?.Dispose();
            _model = new VoiceCallViewModel(channel);
            _model.Tiles.CollectionChanged += OnTilesCollectionChanged;
            TitleText.Text = _model.Title;
            TilesView.ItemsSource = _model.Tiles;
            UpdateSubtitle();
            UpdateTileSize(TilesView.ActualWidth);
            SyncToggles();
            AttachAudioRoute();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            DetachAudioRoute();
            if (_model != null)
            {
                _model.Tiles.CollectionChanged -= OnTilesCollectionChanged;
                _model.Dispose();
                _model = null;
            }
        }

        private void OnTilesCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateSubtitle();
        }

        private void UpdateSubtitle()
        {
            var channel = _model?.Channel;
            if (channel == null)
            {
                SubtitleText.Visibility = Visibility.Collapsed;
                return;
            }

            var personCount = 0;
            foreach (var tile in _model.Tiles)
            {
                if (!tile.IsStream)
                    personCount++;
            }

            if (channel.Guild == null)
            {
                SubtitleText.Text = personCount <= 1
                    ? Loc.T("Voice_WaitingForPeer")
                    : Loc.Plural("People", personCount);
                SubtitleText.Visibility = Visibility.Visible;
            }
            else
            {
                SubtitleText.Text = Loc.Plural("People", Math.Max(1, personCount));
                SubtitleText.Visibility = Visibility.Visible;
            }
        }

        private void AttachAudioRoute()
        {
            DetachAudioRoute();
            if (!VoiceConnectionModel.IsAudioRoutingSupported)
            {
                AudioRouteButton.Visibility = Visibility.Collapsed;
                return;
            }

            AudioRouteButton.Visibility = Visibility.Visible;
            _subscribedVoice = CurrentVoice;
            if (_subscribedVoice != null)
                _subscribedVoice.AudioRoutingChanged += OnAudioRoutingChanged;
            UpdateAudioRouteButton();
        }

        private void DetachAudioRoute()
        {
            if (_subscribedVoice != null)
            {
                _subscribedVoice.AudioRoutingChanged -= OnAudioRoutingChanged;
                _subscribedVoice = null;
            }
        }

        private void OnAudioRoutingChanged(object sender, EventArgs e)
        {
            _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, UpdateAudioRouteButton);
        }

        private void UpdateAudioRouteButton()
        {
            if (!VoiceConnectionModel.IsAudioRoutingSupported)
                return;

            var voice = _subscribedVoice ?? CurrentVoice;
            var route = voice?.CurrentAudioRoute ?? CallAudioRoute.Speakerphone;
            switch (route)
            {
                case CallAudioRoute.Earpiece:
                    AudioRouteIcon.Glyph = "\uE717";
                    ToolTipService.SetToolTip(AudioRouteButton, Loc.T("Voice_AudioEarpiece"));
                    break;
                case CallAudioRoute.Headset:
                    AudioRouteIcon.Glyph = "\uE7F6";
                    ToolTipService.SetToolTip(AudioRouteButton, Loc.T("Voice_AudioHeadset"));
                    break;
                case CallAudioRoute.Bluetooth:
                    AudioRouteIcon.Glyph = "\uE702";
                    ToolTipService.SetToolTip(AudioRouteButton, Loc.T("Voice_AudioBluetooth"));
                    break;
                default:
                    AudioRouteIcon.Glyph = "\uE767";
                    ToolTipService.SetToolTip(AudioRouteButton, Loc.T("Voice_AudioSpeaker"));
                    break;
            }
        }

        private void AudioRoute_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var voice = CurrentVoice;
                if (voice != null && _subscribedVoice != voice)
                    AttachAudioRoute();
                voice?.CycleAudioRoute();
                UpdateAudioRouteButton();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: переключение звука", ex);
            }
        }

        private void SyncToggles()
        {
            var voice = CurrentVoice;
            MuteToggle.IsChecked = voice?.Muted ?? false;
            DeafenToggle.IsChecked = voice?.Deafened ?? false;
        }

        // Hardware/system back must leave this page, not close the app (the navigation service's own stack
        // does not know about it).
        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (e.Handled)
                return;
            e.Handled = true;
            GoBack();
        }

        private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

        private void GoBack()
        {
            if (Frame == null)
                return;
            if (Frame.CanGoBack)
                Frame.GoBack();
            else if (Themes.ThemeResources.LoadedTheme == AppTheme.OLED)
                Frame.Navigate(typeof(HubPage));
            else
                Frame.Navigate(typeof(FriendsPage));
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var voice = CurrentVoice;
                VoiceDiagnostics.Log($"Плитки: нажат микрофон (подключение {(voice != null ? "есть" : "нет")})");
                if (voice != null)
                    voice.Muted = MuteToggle.IsChecked == true;
                SyncToggles();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: микрофон", ex);
            }
        }

        private void Deafen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var voice = CurrentVoice;
                VoiceDiagnostics.Log($"Плитки: нажат звук (подключение {(voice != null ? "есть" : "нет")})");
                if (voice != null)
                    voice.Deafened = DeafenToggle.IsChecked == true;
                SyncToggles();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: звук", ex);
            }
        }

        private async void Settings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VoiceDiagnostics.Log("Плитки: открываю настройки голоса");
                await SettingsService.GetForCurrentView().OpenAsync(SettingsPageType.Voice);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: настройки", ex);
            }
        }

        private async void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DisconnectButton.IsEnabled = false;
                var voice = CurrentVoice;
                VoiceDiagnostics.Log($"Плитки: нажато отключение (подключение {(voice != null ? "есть" : "нет")})");
                if (voice != null)
                    await voice.DisconnectAsync();
                GoBack();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: отключение", ex);
                DisconnectButton.IsEnabled = true;
            }
        }

        private void TilesView_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileSize(e.NewSize.Width);

        // Tapping a person opens their profile (banner, badges, bio); stream tiles have their own "watch" button.
        private async void TilesView_ItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                if (!(e.ClickedItem is VoiceTile tile) || tile.IsStream || tile.Member.UserId == 0)
                    return;
                var guildId = _model?.Channel?.Guild?.Id;
                var user = new Models.User.UserViewModel(tile.Member.UserId, guildId);
                await OverlayService.GetForCurrentView().ShowOverlayAsync<Overlay.UserInfoOverlayPage>(user);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Плитки: профиль", ex);
            }
        }

        // As many 16:9 columns as fit at >= 300 px (one column on a phone), filling the width.
        private void UpdateTileSize(double width)
        {
            if (width <= 0 || !(TilesView.ItemsPanelRoot is ItemsWrapGrid grid))
                return;

            var available = width - 24 - 8; // view padding + scrollbar room
            var columns = Math.Max(1, (int)(available / 300));
            var tileWidth = Math.Floor(available / columns) - 8; // item margin
            grid.ItemWidth = tileWidth + 8;
            grid.ItemHeight = Math.Floor(tileWidth * 9 / 16) + 8;
        }
    }
}
