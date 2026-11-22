using System;
using System.Collections.Generic;
using System.Linq;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Models.User;
using Unicord.Universal.Pages.Subpages;
using Unicord.Universal.Services;
using Windows.ApplicationModel;
using Windows.UI;
using Windows.UI.Popups;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// Start hub shown after sign-in, in the style of the Lumia start page: the signed-in profile with its badges,
    /// a tile for the current call, tiles to the channel list, friends, version info and (later) supporting the
    /// project, recent conversations and servers, and a few plain buttons.
    /// </summary>
    public sealed partial class HubPage : Page
    {
        private const int RecentCount = 10;

        private string _avatarUrl;
        private string _guildTagBadgeUrl;
        private DispatcherTimer _countsTimer;
        private DispatcherTimer _flipTimer;
        private bool _showingBack;
        private bool _flipping;
        private int _onlineAvatars;
        private readonly Dictionary<Grid, Color> _defaultColors = new Dictionary<Grid, Color>();

        public HubPage()
        {
            InitializeComponent();

            foreach (var tile in new[] { CallTile, StartTile, FriendsTile, AboutTile, SupportTile })
            {
                AddTilt(tile);
                _defaultColors[tile] = ((SolidColorBrush)tile.Background).Color;
            }
            AddTilt(ProfileBox);

            UpdateVersionTile();
        }

        private void UpdateVersionTile()
        {
            try
            {
                if (GitHubUpdateService.IsUpdateAvailable && GitHubUpdateService.LatestRelease != null)
                {
                    VersionLabel.Text = Loc.F("Hub_UpdateAvailable", GitHubUpdateService.LatestRelease.DisplayVersion);
                }
                else
                {
                    var v = Package.Current.Id.Version;
                    VersionLabel.Text = Loc.F("Hub_Version", $"{v.Major}.{v.Minor}.{v.Build}");
                }
            }
            catch (Exception) { }
        }

        private void OnUpdateStateChanged(object sender, EventArgs e)
        {
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, UpdateVersionTile);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            MetroTransitions.FeatherAfterLayout(TilesGrid, 60, 40);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (!_shownOnce)
                PerfTimings.Mark("хаб: начало построения");
            // every part on its own: one failure must not break the navigation back to the hub
            Safe("цвета", ApplyTileColors);
            Safe("профиль", LoadProfile);
            Safe("счётчики", Refresh);
            Safe("недавние", BuildRecent);

            // live-tile feel: counters and the call tile refresh while the hub is on screen
            _countsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _countsTimer.Tick += (s, a) => Refresh();
            _countsTimer.Start();

            _flipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _flipTimer.Tick += (s, a) => FlipFriendsTile();
            _flipTimer.Start();

            GitHubUpdateService.StateChanged -= OnUpdateStateChanged;
            GitHubUpdateService.StateChanged += OnUpdateStateChanged;

            // the hub is usually drawn before Discord's data arrives: fill it in as soon as READY comes
            try
            {
                if (_readyClient == null && DiscordManager.Discord != null)
                {
                    _readyClient = DiscordManager.Discord;
                    _readyClient.Ready += OnDiscordReady;
                }
            }
            catch (Exception) { }

            // on screen = the first frame rendered after this (layout of the hub is done by then)
            var first = !_shownOnce;
            _shownOnce = true;
            EventHandler<object> rendered = null;
            rendered = (s, a) =>
            {
                Windows.UI.Xaml.Media.CompositionTarget.Rendering -= rendered;
                if (first)
                    PerfTimings.StartupFinished($"хаб на экране (построение {watch.Elapsed.TotalMilliseconds:0} мс)");
                else
                    PerfTimings.Add("возврат в хаб", watch.Elapsed.TotalMilliseconds);
            };
            Windows.UI.Xaml.Media.CompositionTarget.Rendering += rendered;
        }

        private bool _shownOnce;

        private DSharpPlus.DiscordClient _readyClient;

        private System.Threading.Tasks.Task OnDiscordReady(DSharpPlus.DiscordClient sender, DSharpPlus.EventArgs.ReadyEventArgs e)
        {
            GitHubUpdateService.EnsureBackgroundCheckStarted();
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (Frame?.Content != this)
                    return; // not on screen; OnNavigatedTo rebuilds it on return
                Safe("профиль", LoadProfile);
                Safe("счётчики", Refresh);
                Safe("недавние", BuildRecent);
            });
            return System.Threading.Tasks.Task.CompletedTask;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            GitHubUpdateService.StateChanged -= OnUpdateStateChanged;
            try
            {
                if (_readyClient != null)
                    _readyClient.Ready -= OnDiscordReady;
            }
            catch (Exception) { }
            _readyClient = null;
            _countsTimer?.Stop();
            _countsTimer = null;
            _flipTimer?.Stop();
            _flipTimer = null;
        }

        private static void Safe(string part, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log($"Хаб: {part}", ex);
            }
        }

        // ------------------------------------------------------------------ profile

        private async void LoadProfile()
        {
            var me = DiscordManager.Discord?.CurrentUser;
            if (me == null)
                return;

            // Before READY (and before GET /users/@me, which no longer delays the connection) only the id is known:
            // show what was saved last time, and save what is known now for the next start.
            string name, username, avatar, guildTag, guildTagBadge;
            if (me.Username != null)
            {
                name = string.IsNullOrEmpty(me.GlobalName) ? me.Username : me.GlobalName;
                username = me.Username;
                avatar = me.GetAvatarUrl(ImageFormat.Png, 128);
                guildTag = me.PrimaryGuild?.HasTag == true ? me.PrimaryGuild.Tag : null;
                guildTagBadge = me.PrimaryGuild?.HasTag == true ? me.PrimaryGuild.BadgeUrl : null;
                try
                {
                    App.LocalSettings.Save("HubProfileName", name);
                    App.LocalSettings.Save("HubProfileUser", username);
                    App.LocalSettings.Save("HubProfileAvatar", avatar);
                    App.LocalSettings.Save("HubProfileGuildTag", guildTag ?? "");
                    App.LocalSettings.Save("HubProfileGuildTagBadge", guildTagBadge ?? "");
                }
                catch (Exception) { }
            }
            else
            {
                name = App.LocalSettings.Read<string>("HubProfileName", null) ?? "";
                username = App.LocalSettings.Read<string>("HubProfileUser", null) ?? "";
                avatar = App.LocalSettings.Read<string>("HubProfileAvatar", null);
                guildTag = App.LocalSettings.Read<string>("HubProfileGuildTag", null);
                guildTagBadge = App.LocalSettings.Read<string>("HubProfileGuildTagBadge", null);
            }

            NameText.Text = name;
            UsernameText.Text = username;
            ApplyGuildTag(guildTag, guildTagBadge);
            try
            {
                // the page stays in memory now: load the picture only when it actually changed
                if (!string.IsNullOrEmpty(avatar) && avatar != _avatarUrl)
                {
                    _avatarUrl = avatar;
                    ImageDiskCache.Load(AvatarImage, avatar);
                }
            }
            catch (Exception) { }

            try
            {
                var profile = await UserProfileInfo.LoadAsync(me.Id, null);
                BadgesList.ItemsSource = profile.Badges.Count > 0 ? profile.Badges : null;
                UsernameText.Text = string.IsNullOrEmpty(profile.Pronouns) ? username : $"{username} • {profile.Pronouns}";
                if (!string.IsNullOrEmpty(profile.GuildTag) || me.PrimaryGuild == null)
                {
                    var badgeUrl = profile.GuildTagBadge?.AbsoluteUri;
                    ApplyGuildTag(profile.GuildTag, badgeUrl);
                    try
                    {
                        App.LocalSettings.Save("HubProfileGuildTag", profile.GuildTag ?? "");
                        App.LocalSettings.Save("HubProfileGuildTagBadge", badgeUrl ?? "");
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Хаб: профиль", ex);
            }
        }

        private void ApplyGuildTag(string tag, string badgeUrl)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                GuildTagBorder.Visibility = Visibility.Collapsed;
                GuildTagIcon.Visibility = Visibility.Collapsed;
                GuildTagText.Text = "";
                return;
            }

            GuildTagText.Text = tag;
            if (!string.IsNullOrEmpty(badgeUrl))
            {
                GuildTagIcon.Visibility = Visibility.Visible;
                if (badgeUrl != _guildTagBadgeUrl)
                {
                    _guildTagBadgeUrl = badgeUrl;
                    ImageDiskCache.Load(GuildTagBadgeImage, badgeUrl);
                }
            }
            else
            {
                GuildTagIcon.Visibility = Visibility.Collapsed;
            }
            GuildTagBorder.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------ live counters, call tile, friends mosaic

        private void Refresh()
        {
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;
            try
            {
                var online = OnlineFriends(discord).ToList();
                FriendsLabel.Text = online.Count > 0 ? Loc.F("Hub_Online", online.Count) : Loc.T("Hub_People");
                UpdateFriendsMosaic(online);

                var unread = discord.PrivateChannels.Values.Count(c => c.IsUnread());
                StartLabel.Text = unread > 0 ? Loc.Plural("Unread", unread) : Loc.T("Hub_ChannelsAndMessages");

                UpdateCallTile(discord);
                UpdateVersionTile();
                GitHubUpdateService.EnsureBackgroundCheckStarted();
            }
            catch (Exception)
            {
                // counters are decoration; the cache can change under us
            }
        }

        private static IEnumerable<DiscordUser> OnlineFriends(DiscordClient discord)
            => discord.Relationships.Values
                .Where(r => r.RelationshipType == DiscordRelationshipType.Friend && r.User != null)
                .Where(r => discord.Presences.TryGetValue(r.User.Id, out var p) && p != null &&
                            p.Status != UserStatus.Offline && p.Status != UserStatus.Invisible)
                .Select(r => r.User);

        private void UpdateCallTile(DiscordClient discord)
        {
            var voice = (DiscordPage?.DataContext as DiscordPageViewModel)?.VoiceModel;
            var channel = voice?.Channel;
            var me = discord.CurrentUser;
            var inCall = channel != null && me != null && (
                (channel.Guild != null && channel.Guild.VoiceStates.TryGetValue(me.Id, out var state) && state?.Channel?.Id == channel.Id) ||
                (channel.Guild == null && (Models.Voice.VoiceConnectionModel.HasActiveInAppCall ||
                    (discord.PrivateVoiceStates != null && discord.PrivateVoiceStates.TryGetValue(me.Id, out var dmState) && dmState?.Channel?.Id == channel.Id))));

            CallTile.Visibility = inCall ? Visibility.Visible : Visibility.Collapsed;
            if (!inCall)
                return;

            var people = channel.Guild != null
                ? channel.Guild.VoiceStates.Values.Count(v => v.Channel?.Id == channel.Id)
                : Math.Max(1, discord.PrivateVoiceStates?.Values.Count(v => v.Channel?.Id == channel.Id) ?? 1);
            var title = Models.Voice.VoiceCallViewModel.GetChannelTitle(channel);
            CallName.Text = title;
            CallLabel.Text = Loc.F("Hub_InCall", channel.Guild?.Name ?? title, Loc.Plural("People", people));
        }

        private async void Call_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var channel = (DiscordPage?.DataContext as DiscordPageViewModel)?.VoiceModel?.Channel;
            if (channel == null)
                return;
            try
            {
                // already connected: the navigation service only shows the call view, it does not reconnect
                await DiscordNavigationService.GetForCurrentView().NavigateToVoiceCallAsync(channel);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Хаб: звонок", ex);
            }
        }

        private void UpdateFriendsMosaic(List<DiscordUser> online)
        {
            var count = Math.Min(6, online.Count);
            if (count == _onlineAvatars && FriendsBack.Children.Count == count)
                return;
            _onlineAvatars = count;

            FriendsBack.Children.Clear();
            for (var i = 0; i < count; i++)
            {
                var image = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = ImageDiskCache.Create(online[i].GetAvatarUrl(ImageFormat.Png, 64), 64)
                };
                Grid.SetColumn(image, i % 3);
                Grid.SetRow(image, i / 3);
                FriendsBack.Children.Add(image);
            }

            if (count == 0 && _showingBack)
                ShowFriendsSide(false);
        }

        // Windows Phone live tile flip: rotate to the edge, swap the side, rotate back
        private void FlipFriendsTile()
        {
            if (_flipping || (!_showingBack && FriendsBack.Children.Count == 0))
                return;
            _flipping = true;

            var half = TimeSpan.FromMilliseconds(220);
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
            var first = new Storyboard();
            var turnAway = new DoubleAnimation { From = 0, To = 90, Duration = half, EasingFunction = ease };
            Storyboard.SetTarget(turnAway, FriendsFlip);
            Storyboard.SetTargetProperty(turnAway, "RotationX");
            first.Children.Add(turnAway);
            first.Completed += (s, a) =>
            {
                ShowFriendsSide(!_showingBack);
                var second = new Storyboard();
                var turnBack = new DoubleAnimation { From = -90, To = 0, Duration = half, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                Storyboard.SetTarget(turnBack, FriendsFlip);
                Storyboard.SetTargetProperty(turnBack, "RotationX");
                second.Children.Add(turnBack);
                second.Completed += (s2, a2) => _flipping = false;
                second.Begin();
            };
            first.Begin();
        }

        private void ShowFriendsSide(bool back)
        {
            _showingBack = back;
            FriendsBack.Visibility = back ? Visibility.Visible : Visibility.Collapsed;
            FriendsFront.Visibility = back ? Visibility.Collapsed : Visibility.Visible;
        }

        // ------------------------------------------------------------------ recent

        /// <summary>Latest direct messages and servers by their newest message, like "recent" on Windows Phone.</summary>
        private void BuildRecent()
        {
            var discord = DiscordManager.Discord;
            RecentList.Children.Clear();
            if (discord == null)
                return;

            var items = new List<(ulong LastId, DiscordChannel Channel, string Name, string Image)>();
            try
            {
                foreach (var dm in discord.PrivateChannels.Values)
                {
                    if (dm.LastMessageId == null)
                        continue;
                    var other = dm.Recipients?.FirstOrDefault();
                    var isGroup = dm.Recipients != null && dm.Recipients.Count > 1;
                    var name = isGroup ? (string.IsNullOrEmpty(dm.Name) ? string.Join(", ", dm.Recipients.Select(r => r.DisplayName)) : dm.Name)
                                       : other?.DisplayName;
                    var image = isGroup ? dm.IconUrl : other?.GetAvatarUrl(ImageFormat.Png, 64);
                    items.Add((dm.LastMessageId.Value, dm, name ?? "?", image));
                }

                foreach (var guild in discord.Guilds.Values)
                {
                    DiscordChannel latest = null;
                    foreach (var channel in guild.Channels.Values)
                    {
                        if (channel.Type != ChannelType.Text || channel.LastMessageId == null)
                            continue;
                        if (latest != null && channel.LastMessageId <= latest.LastMessageId)
                            continue;
                        try
                        {
                            if ((channel.CurrentPermissions & Permissions.AccessChannels) != Permissions.AccessChannels)
                                continue;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        latest = channel;
                    }
                    if (latest != null)
                        items.Add((latest.LastMessageId.Value, latest, guild.Name, guild.IconUrl));
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Хаб: недавние", ex);
            }

            foreach (var item in items.OrderByDescending(i => i.LastId).Take(RecentCount))
            {
                try
                {
                    RecentList.Children.Add(CreateRecentItem(item.Channel, item.Name, item.Image));
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Хаб: недавний элемент", ex);
                }
            }
            RecentSection.Visibility = RecentList.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private FrameworkElement CreateRecentItem(DiscordChannel channel, string name, string image)
        {
            var photo = new Grid { Width = 72, Height = 72, Background = ThemeBrush(0x262626, 0xE0E0E0) };
            if (!string.IsNullOrEmpty(image) && Uri.TryCreate(image, UriKind.Absolute, out _))
            {
                photo.Children.Add(new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = ImageDiskCache.Create(image, 72)
                });
            }
            else
            {
                // no picture: initials, like the People hub
                var initials = string.Concat((name ?? "?").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
                photo.Children.Add(new TextBlock
                {
                    Text = initials,
                    FontSize = 26,
                    FontWeight = FontWeights.SemiLight,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var label = new TextBlock
            {
                Text = name,
                FontSize = 13,
                Width = 72,
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeBrush(0x9A9A9A, 0x6B6B6B)
            };

            var item = new StackPanel { Width = 72, Margin = new Thickness(0, 0, 12, 0), Background = new SolidColorBrush(Colors.Transparent) };
            item.Children.Add(photo);
            item.Children.Add(label);
            AddTilt(item);
            item.Tapped += async (s, e) =>
            {
                try
                {
                    await DiscordNavigationService.GetForCurrentView().NavigateAsync(channel);
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Хаб: недавние", ex);
                }
            };
            return item;
        }

        // ------------------------------------------------------------------ tile colours

        private void ApplyTileColors()
        {
            var color = MetroColors.TileOverride();
            foreach (var pair in _defaultColors)
                pair.Key.Background = new SolidColorBrush(color ?? pair.Value);
        }

        private void TileColor_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
            var panel = new StackPanel { Width = 280 };

            void Choose(string value)
            {
                App.LocalSettings.Save(MetroColors.TileColorSetting, value);
                ApplyTileColors();
                flyout.Hide();
            }

            var multi = new Button { Content = Loc.T("Hub_ColorsMulti"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 8) };
            multi.Click += (s, a) => Choose(MetroColors.Multicolor);
            var accent = new Button { Content = Loc.T("Hub_ColorsAccent"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
            accent.Click += (s, a) => Choose(MetroColors.SystemAccent);
            panel.Children.Add(multi);
            panel.Children.Add(accent);

            var swatches = new Microsoft.Toolkit.Uwp.UI.Controls.WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            foreach (var (colorName, color) in MetroColors.Palette)
            {
                var swatch = new Border { Width = 48, Height = 48, Background = new SolidColorBrush(color) };
                ToolTipService.SetToolTip(swatch, Loc.T("Color_" + colorName));
                swatch.Tapped += (s, a) => Choose(MetroColors.ToHex(color));
                swatches.Children.Add(swatch);
            }
            panel.Children.Add(swatches);

            flyout.Content = panel;
            flyout.ShowAt((FrameworkElement)sender);
        }

        // ------------------------------------------------------------------ helpers and navigation

        // the page's ThemeDictionaries are not reachable through Resources[...] from code
        private Brush ThemeBrush(int dark, int light)
            => new SolidColorBrush(MetroColors.C(MetroUi.IsLight(this) ? light : dark));

        private static string Plural(int n, string one, string few, string many)
        {
            var d = n % 10;
            var dd = n % 100;
            if (d == 1 && dd != 11) return one;
            if (d >= 2 && d <= 4 && (dd < 12 || dd > 14)) return few;
            return many;
        }

        // Metro "tilt": the tile sinks a little while pressed
        private static void AddTilt(FrameworkElement element)
        {
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform();
            void Set(double scale)
            {
                var t = (ScaleTransform)element.RenderTransform;
                t.ScaleX = t.ScaleY = scale;
            }
            element.PointerPressed += (s, e) => Set(0.97);
            element.PointerReleased += (s, e) => Set(1);
            element.PointerCanceled += (s, e) => Set(1);
            element.PointerCaptureLost += (s, e) => Set(1);
            element.PointerExited += (s, e) => Set(1);
        }

        private static DiscordPage DiscordPage => Window.Current.Content.FindChild<DiscordPage>();

        private async void Profile_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var me = DiscordManager.Discord?.CurrentUser;
            if (me == null)
                return;
            try
            {
                await OverlayService.GetForCurrentView()
                    .ShowOverlayAsync<Overlay.UserInfoOverlayPage>(new UserViewModel(me.Id, null));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Хаб: свой профиль", ex);
            }
        }

        // "Start": servers, direct messages and friends in the Metro style
        private void Start_Tapped(object sender, TappedRoutedEventArgs e) => OpenFromHub(typeof(ServersHubPage));

        private void Friends_Tapped(object sender, TappedRoutedEventArgs e) => OpenFromHub(typeof(FriendsHubPage));

        private void EditProfile_Click(object sender, RoutedEventArgs e) => OpenFromHub(typeof(ProfileEditPage));

        private void OpenFromHub(Type page)
        {
            var discordPage = DiscordPage;
            if (discordPage == null)
                return;
            SplitPaneService.GetForCurrentView().CloseAllPanes();
            discordPage.MainFrame.Navigate(page);
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => OpenFromHub(typeof(SettingsHubPage));

        // the Metro page: the old settings "about" page does not load on the phone
        private void About_Tapped(object sender, TappedRoutedEventArgs e) => OpenFromHub(typeof(AboutHubPage));

        private async void Support_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("https://t.me/lyracord"));
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Хаб: телеграм", ex);
            }
        }
    }
}
