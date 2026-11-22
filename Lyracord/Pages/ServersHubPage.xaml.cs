using System;
using System.Collections.Generic;
using System.Linq;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.User;
using Unicord.Universal.Pages.Overlay;
using Unicord.Universal.Services;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// "servers · direct · friends" (the hub's "start" tile): servers as square tiles in the user's own order,
    /// direct messages newest first, friends who are online. Tapping a server opens its Metro channel list.
    /// </summary>
    public sealed partial class ServersHubPage : Page
    {
        private const int Columns = 3;
        private const double Gap = 8;
        private const int MaxDirect = 60;

        public ServersHubPage()
        {
            InitializeComponent();
            SearchBox.PlaceholderText = Loc.T("Servers_SearchPlaceholder");
            ToolTipService.SetToolTip(SearchToggleButton, Loc.T("Common_Search"));
            ToolTipService.SetToolTip(AddFriendButton, Loc.T("Social_AddFriendTitle"));
            ToolTipService.SetToolTip(JoinServerButton, Loc.T("Social_JoinServerTitle"));

            // Only the visible tab is built (on the page showing up, also when coming back); the others wait until the
            // user swipes to them. Building all three at once made opening the page slow on the phone.
            Loaded += (s, e) => { _onScreen = true; _built.Clear(); BuildVisible(); };
            Unloaded += (s, e) => _onScreen = false;
            MainPivot.SelectionChanged += (s, e) => { if (_onScreen) BuildVisible(); };
        }

        private readonly HashSet<int> _built = new HashSet<int>();
        private bool _onScreen;
        private string _searchQuery = "";

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
            _searchQuery = "";
            SearchBar.Visibility = Visibility.Collapsed;
            _built.Clear();
            BuildVisible(animate: false);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchQuery = (SearchBox.Text ?? "").Trim();
            _built.Clear();
            BuildVisible(animate: false);
        }

        private async void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await MetroUi.ShowAddFriendDialogAsync();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Серверы: добавить друга", ex);
            }
        }

        private async void JoinServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var invite = await MetroUi.ShowJoinServerDialogAsync();
                if (invite != null)
                {
                    await System.Threading.Tasks.Task.Delay(600);
                    _built.Clear();
                    BuildVisible(animate: false);
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Серверы: войти по приглашению", ex);
            }
        }

        private void BuildVisible(bool animate = true)
        {
            var index = MainPivot.SelectedIndex < 0 ? 0 : MainPivot.SelectedIndex;
            if (!_built.Add(index))
                return;
            using (PerfTimings.Measure($"вкладка «{(index == 0 ? "серверы" : index == 1 ? "личные" : "друзья")}»"))
            {
                switch (index)
                {
                    case 0: Safe("серверы", BuildServers); break;
                    case 1: Safe("личные", BuildDirect); break;
                    case 2: Safe("друзья", BuildFriends); break;
                }
            }
            // the freshly built tab's tiles or rows swing in one by one
            if (animate)
                MetroTransitions.FeatherAfterLayout(index == 0 ? (Panel)ServersGrid : index == 1 ? DirectList : FriendsList, 30, 28, index == 0 ? 15 : 10);
        }

        private static void Safe(string part, Action action)
        {
            try { action(); }
            catch (Exception ex) { VoiceDiagnostics.Log($"Серверы: {part}", ex); }
        }

        // ------------------------------------------------------------------ servers

        /// <summary>Servers in the order of the user's sidebar (folders flattened), ungrouped ones first.</summary>
        private static List<DiscordGuild> OrderedGuilds(DiscordClient discord)
        {
            var result = new List<DiscordGuild>();
            var seen = new HashSet<ulong>();
            var folders = discord.UserSettings?.GuildFolders;
            var ordered = new List<DiscordGuild>();
            if (folders != null)
            {
                foreach (var folder in folders)
                {
                    foreach (var id in folder.GuildIds)
                    {
                        if (discord.Guilds.TryGetValue(id, out var guild) && seen.Add(id))
                            ordered.Add(guild);
                    }
                }
            }
            result.AddRange(discord.Guilds.Values.Where(g => !seen.Contains(g.Id)));
            result.AddRange(ordered);
            return result;
        }

        private void BuildServers()
        {
            ServersGrid.Children.Clear();
            ServersEmptyText.Visibility = Visibility.Collapsed;
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            var q = _searchQuery;
            IEnumerable<DiscordGuild> guilds = OrderedGuilds(discord);
            if (!string.IsNullOrEmpty(q))
                guilds = guilds.Where(g => !string.IsNullOrEmpty(g.Name) && g.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);

            foreach (var guild in guilds)
            {
                try
                {
                    ServersGrid.Children.Add(CreateServerTile(guild));
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Серверы: плитка", ex);
                }
            }

            if (ServersGrid.Children.Count == 0 && !string.IsNullOrEmpty(q))
            {
                ServersEmptyText.Text = Loc.T("Common_NothingFound");
                ServersEmptyText.Visibility = Visibility.Visible;
            }
            UpdateTileSize(ServersScroll.ActualWidth);
        }

        private FrameworkElement CreateServerTile(DiscordGuild guild)
        {
            var muted = SafeBool(() => guild.IsMuted());
            var channels = guild.Channels.Values;
            var unread = !muted && channels.Any(c => SafeBool(() => !c.IsMuted() && c.IsUnread()));
            var mentions = channels.Sum(c => c.ReadState?.MentionCount ?? 0);

            var icon = string.IsNullOrEmpty(guild.IconHash) ? null : $"https://cdn.discordapp.com/icons/{guild.Id}/{guild.IconHash}.png?size=128";
            var tile = new Grid { Margin = new Thickness(0, 0, Gap, Gap) };
            var photo = MetroUi.Photo(icon, guild.Name, guild.Id, 100, 128);
            photo.Width = double.NaN;
            photo.Height = double.NaN;
            tile.Children.Add(photo);

            // name strip at the bottom, readable over any icon
            tile.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Bottom,
                Padding = new Thickness(6, 3, 6, 4),
                Child = new TextBlock
                {
                    Text = guild.Name,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Colors.White),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            });

            // unread: a white bar on the left edge; mentions: a red counter in the corner
            if (unread)
                tile.Children.Add(new Border { Width = 4, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Colors.White) });
            if (mentions > 0)
                tile.Children.Add(MetroUi.Counter(mentions));
            if (muted)
                tile.Opacity = 0.55;

            MetroUi.Tilt(tile);
            tile.Tapped += (s, e) => Frame.Navigate(typeof(GuildHubPage), guild);
            return tile;
        }

        private void ServersScroll_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTileSize(e.NewSize.Width);

        // three square tiles per row on a phone, more on wide windows (at least 100 px each)
        private void UpdateTileSize(double width)
        {
            if (width <= 0)
                return;
            var columns = Math.Max(Columns, (int)(width / 140));
            var size = Math.Floor(width / columns);
            ServersGrid.ItemWidth = size;
            ServersGrid.ItemHeight = size;
        }

        // ------------------------------------------------------------------ direct messages

        private void BuildDirect()
        {
            DirectList.Children.Clear();
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            var q = _searchQuery;
            IEnumerable<DiscordDmChannel> channels = discord.PrivateChannels.Values
                .OrderByDescending(c => c.LastMessageId ?? 0);

            if (!string.IsNullOrEmpty(q))
            {
                channels = channels.Where(c => MatchesDm(c, q));
            }
            else
            {
                channels = channels.Take(MaxDirect);
            }

            foreach (var dm in channels)
            {
                try
                {
                    DirectList.Children.Add(CreateDirectRow(discord, dm));
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Личные: строка", ex);
                }
            }

            if (DirectList.Children.Count == 0 && !string.IsNullOrEmpty(q))
                DirectList.Children.Add(new TextBlock { Text = Loc.T("Common_NothingFound"), FontSize = 18, Foreground = MetroUi.Secondary(this) });
        }

        private static bool MatchesDm(DiscordDmChannel dm, string q)
        {
            if (!string.IsNullOrEmpty(dm.Name) && dm.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
                return true;
            if (dm.Recipients != null)
            {
                foreach (var r in dm.Recipients)
                {
                    if (r == null) continue;
                    if ((!string.IsNullOrEmpty(r.DisplayName) && r.DisplayName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.Username) && r.Username.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(r.GlobalName) && r.GlobalName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0))
                        return true;
                }
            }
            return false;
        }

        private FrameworkElement CreateDirectRow(DiscordClient discord, DiscordDmChannel dm)
        {
            var recipients = dm.Recipients ?? new List<DiscordUser>();
            var isGroup = dm.Type == ChannelType.Group || recipients.Count > 1;
            var other = recipients.FirstOrDefault();

            string name, image, subtitle;
            Brush status = null;
            if (isGroup)
            {
                name = !string.IsNullOrEmpty(dm.Name) ? dm.Name : string.Join(", ", recipients.Select(r => r.DisplayName));
                image = dm.IconUrl;
                subtitle = Loc.F("Servers_Group", Loc.Plural("Members", recipients.Count + 1));
            }
            else
            {
                name = other?.DisplayName ?? "?";
                image = other?.GetAvatarUrl(ImageFormat.Png, 64);
                subtitle = PresenceText(discord, other?.Id ?? 0, out status);
            }

            var mentions = dm.ReadState?.MentionCount ?? 0;
            if (mentions > 0)
                subtitle = Loc.Plural("NewMessages", mentions);

            var inCallCount = discord.PrivateVoiceStates?.Values.Count(v => v.Channel?.Id == dm.Id) ?? 0;
            if (inCallCount > 0)
            {
                subtitle = "📞 " + Loc.Plural("People", inCallCount);
                status = MetroUi.Solid(0x23A55A);
            }

            var photo = MetroUi.Photo(image, name, dm.Id, 56);
            var row = MetroUi.Row(this, photo, name, subtitle, mentions > 0, status, isGroup ? null : other?.PrimaryGuild);
            if (mentions > 0)
            {
                var counter = MetroUi.Counter(mentions);
                Grid.SetColumn(counter, 2);
                counter.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(counter);
            }
            row.Tapped += async (s, e) =>
            {
                try { await DiscordNavigationService.GetForCurrentView().NavigateAsync(dm); }
                catch (Exception ex) { VoiceDiagnostics.Log("Личные: открыть", ex); }
            };
            return row;
        }

        // ------------------------------------------------------------------ friends online

        private void BuildFriends()
        {
            FriendsList.Children.Clear();
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            var q = _searchQuery;
            var searching = !string.IsNullOrEmpty(q);

            var friends = discord.Relationships.Values
                .Where(r => r.RelationshipType == DiscordRelationshipType.Friend && r.User != null)
                .Select(r => r.User)
                .Where(u => !searching ||
                    (!string.IsNullOrEmpty(u.DisplayName) && u.DisplayName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(u.Username) && u.Username.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(u.GlobalName) && u.GlobalName.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0))
                .Select(u => new { User = u, Text = PresenceText(discord, u.Id, out var bar), Bar = bar })
                .Where(f => searching || f.Bar != null)
                .OrderByDescending(f => f.Bar != null)
                .ThenBy(f => f.User.DisplayName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var friend in friends)
            {
                var user = friend.User;
                var row = MetroUi.Row(this, MetroUi.Photo(user.GetAvatarUrl(ImageFormat.Png, 64), user.DisplayName, user.Id, 56),
                                      user.DisplayName, friend.Text, false, friend.Bar, user.PrimaryGuild);
                row.Tapped += async (s, e) =>
                {
                    try { await OverlayService.GetForCurrentView().ShowOverlayAsync<UserInfoOverlayPage>(new UserViewModel(user.Id, null)); }
                    catch (Exception ex) { VoiceDiagnostics.Log("Друзья: профиль", ex); }
                };
                FriendsList.Children.Add(row);
            }

            if (FriendsList.Children.Count == 0)
                FriendsList.Children.Add(new TextBlock
                {
                    Text = searching ? Loc.T("Common_NothingFound") : Loc.T("Servers_NobodyOnline"),
                    FontSize = 18,
                    Foreground = MetroUi.Secondary(this)
                });
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>"в сети" / game or status text, and the status bar colour (null when offline).</summary>
        internal static string PresenceText(DiscordClient discord, ulong userId, out Brush bar)
        {
            bar = null;
            if (userId == 0 || discord.Presences == null || !discord.Presences.TryGetValue(userId, out var p) || p == null)
                return Loc.T("Presence_Offline");

            switch (p.Status)
            {
                case UserStatus.Online: bar = MetroUi.Solid(0x23A55A); break;
                case UserStatus.Idle: bar = MetroUi.Solid(0xF0B232); break;
                case UserStatus.DoNotDisturb: bar = MetroUi.Solid(0xF23F43); break;
                default: return Loc.T("Presence_Offline");
            }

            var activity = p.Activities?.FirstOrDefault(a => a.ActivityType != ActivityType.Custom) ?? p.Activity;
            if (activity != null && !string.IsNullOrEmpty(activity.Name) && activity.ActivityType != ActivityType.Custom)
            {
                switch (activity.ActivityType)
                {
                    case ActivityType.Playing: return Loc.F("Presence_Playing", activity.Name);
                    case ActivityType.ListeningTo: return Loc.F("Presence_Listening", activity.Name);
                    case ActivityType.Watching: return Loc.F("Presence_Watching", activity.Name);
                    case ActivityType.Streaming: return Loc.F("Presence_Streaming", activity.Name);
                    default: return activity.Name;
                }
            }
            return p.Status == UserStatus.Idle ? Loc.T("Presence_Idle") : p.Status == UserStatus.DoNotDisturb ? Loc.T("Presence_DoNotDisturb") : Loc.T("Presence_Online");
        }

        internal static string Plural(int n, string one, string few, string many)
        {
            var d = n % 10;
            var dd = n % 100;
            if (d == 1 && dd != 11) return one;
            if (d >= 2 && d <= 4 && (dd < 12 || dd > 14)) return few;
            return many;
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch (Exception) { return false; }
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SearchBox.PlaceholderText = Loc.T("Servers_SearchPlaceholder");
            MetroTransitions.PageIn(this, e.NavigationMode);
        }
    }
}
