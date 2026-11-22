using System;
using DSharpPlus.Entities;
using Unicord.Universal.Models;
using Unicord.Universal.Models.Relationships;
using Unicord.Universal.Pages.Overlay;
using Unicord.Universal.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Pages
{
    /// <summary>Friends in the style of the Windows Phone People hub (opened from the start hub's "friends" tile).</summary>
    public sealed partial class FriendsHubPage : Page
    {
        public FriendsHubPage()
        {
            InitializeComponent();
            SearchBox.PlaceholderText = Loc.T("Friends_SearchPlaceholder");
            ToolTipService.SetToolTip(SearchToggleButton, Loc.T("Common_Search"));
            ToolTipService.SetToolTip(AddFriendButton, Loc.T("Social_AddFriendTitle"));
        }

        private FriendsPageViewModel ViewModel => DataContext as FriendsPageViewModel;

        private void SearchToggle_Click(object sender, RoutedEventArgs e)
        {
            if (SearchBar.Visibility == Visibility.Visible)
            {
                CloseSearch_Click(sender, e);
            }
            else
            {
                SearchBar.Visibility = Visibility.Visible;
                SearchBox.Focus(FocusState.Programmatic);
            }
        }

        private void CloseSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            if (ViewModel != null)
                ViewModel.SearchQuery = "";
            SearchBar.Visibility = Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.SearchQuery = SearchBox.Text ?? "";
        }

        private async void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await MetroUi.ShowAddFriendDialogAsync();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Друзья: окно добавления", ex);
            }
        }

        private async void AcceptRequest_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is ulong userId))
                return;
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            btn.IsEnabled = false;
            try
            {
                await discord.PutRelationshipAsync(userId);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Друзья: принять запрос", ex);
                btn.IsEnabled = true;
            }
        }

        private async void DeclineRequest_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button btn) || !(btn.Tag is ulong userId))
                return;
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            btn.IsEnabled = false;
            try
            {
                await discord.DeleteRelationshipAsync(userId);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Друзья: отклонить запрос", ex);
                btn.IsEnabled = true;
            }
        }

        private async void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (!(e.ClickedItem is RelationshipViewModel rel))
                return;
            try
            {
                // the profile shows banner and badges and has the "send message" button
                await OverlayService.GetForCurrentView().ShowOverlayAsync<UserInfoOverlayPage>(rel.User);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Друзья: профиль", ex);
            }
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SearchBox.PlaceholderText = Loc.T("Friends_SearchPlaceholder");
            MetroTransitions.PageIn(this, e.NavigationMode);
        }
    }

    /// <summary>Presence → colour of the thin bar next to a friend's photo (transparent when offline).</summary>
    public sealed class StatusBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush Online = new SolidColorBrush(Color.FromArgb(255, 0x23, 0xA5, 0x5A));
        private static readonly SolidColorBrush Idle = new SolidColorBrush(Color.FromArgb(255, 0xF0, 0xB2, 0x32));
        private static readonly SolidColorBrush Busy = new SolidColorBrush(Color.FromArgb(255, 0xF2, 0x3F, 0x43));
        private static readonly SolidColorBrush None = new SolidColorBrush(Colors.Transparent);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (!(value is UserStatus status))
                return None;
            switch (status)
            {
                case UserStatus.Online: return Online;
                case UserStatus.Idle: return Idle;
                case UserStatus.DoNotDisturb: return Busy;
                default: return None;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
