using System;
using System.Collections.Generic;
using System.Linq;
using DSharpPlus.Entities;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Unicord.Universal.Models.User;
using Unicord.Universal.Pages;
using Unicord.Universal.Services;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unicord.Universal.Dialogs
{
    public sealed partial class ProfileOverlay : UserControl
    {
        private int _loadId;

        public UserViewModel User
        {
            get => (UserViewModel)GetValue(UserProperty);
            set => SetValue(UserProperty, value);
        }

        public static readonly DependencyProperty UserProperty =
            DependencyProperty.Register("User", typeof(UserViewModel), typeof(ProfileOverlay), new PropertyMetadata(null, OnUserChanged));

        private static void OnUserChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var overlay = (ProfileOverlay)d;
            overlay.Bindings.Update();
            overlay.LoadProfile();
        }

        public ProfileOverlay()
        {
            InitializeComponent();
        }

        private void CloseOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            OverlayService.GetForCurrentView().CloseOverlay();
        }

        private async void MessageButton_Click(object sender, RoutedEventArgs e)
        {
            var user = User;
            if (user == null || user.IsCurrent)
                return;

            try
            {
                var dm = await FindOrCreateDmChannelAsync(user.Id);
                if (dm != null)
                {
                    OverlayService.GetForCurrentView().CloseOverlay();
                    await DiscordNavigationService.GetForCurrentView().NavigateAsync(dm);
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Профиль: написать", ex);
            }
        }

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            var user = User;
            if (user == null || user.IsCurrent)
                return;

            try
            {
                var dm = await FindOrCreateDmChannelAsync(user.Id);
                if (dm != null)
                {
                    OverlayService.GetForCurrentView().CloseOverlay();
                    await DiscordNavigationService.GetForCurrentView().NavigateToVoiceCallAsync(dm);
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Профиль: позвонить", ex);
            }
        }

        private void UpdateFriendButton(UserViewModel user, DSharpPlus.Enums.DiscordRelationshipType? overrideType = null, bool hasOverride = false)
        {
            if (user == null || user.IsCurrent || user.IsBot)
            {
                FriendButton.Visibility = Visibility.Collapsed;
                return;
            }

            FriendButton.Visibility = Visibility.Visible;
            FriendButton.IsEnabled = true;

            DSharpPlus.Enums.DiscordRelationshipType? relType = overrideType;
            if (!hasOverride)
            {
                var discord = DiscordManager.Discord;
                if (discord?.Relationships != null && discord.Relationships.TryGetValue(user.Id, out var rel))
                    relType = rel.RelationshipType;
            }

            switch (relType)
            {
                case DSharpPlus.Enums.DiscordRelationshipType.Friend:
                    FriendButtonIcon.Glyph = "\uE8FB";
                    FriendButtonText.Text = Loc.T("Profile_FriendInList");
                    break;
                case DSharpPlus.Enums.DiscordRelationshipType.IncomingRequest:
                    FriendButtonIcon.Glyph = "\uE73E";
                    FriendButtonText.Text = Loc.T("Profile_FriendAccept");
                    break;
                case DSharpPlus.Enums.DiscordRelationshipType.OutgoingRequest:
                    FriendButtonIcon.Glyph = "\uE711";
                    FriendButtonText.Text = Loc.T("Profile_FriendPending");
                    break;
                case DSharpPlus.Enums.DiscordRelationshipType.Blocked:
                    FriendButtonIcon.Glyph = "\uE72E";
                    FriendButtonText.Text = Loc.T("Profile_FriendUnblock");
                    break;
                default:
                    FriendButtonIcon.Glyph = "\uE8FA";
                    FriendButtonText.Text = Loc.T("Profile_FriendAdd");
                    break;
            }
        }

        private async void FriendButton_Click(object sender, RoutedEventArgs e)
        {
            var user = User;
            var discord = DiscordManager.Discord;
            if (user == null || user.IsCurrent || discord == null)
                return;

            DSharpPlus.Enums.DiscordRelationshipType? relType = null;
            if (discord.Relationships != null && discord.Relationships.TryGetValue(user.Id, out var rel))
                relType = rel.RelationshipType;

            FriendButton.IsEnabled = false;
            DSharpPlus.Enums.DiscordRelationshipType? nextType = relType;
            var updated = false;
            try
            {
                if (relType == DSharpPlus.Enums.DiscordRelationshipType.Friend)
                {
                    var confirm = new ContentDialog
                    {
                        Title = Loc.T("Profile_RemoveFriendTitle"),
                        Content = Loc.F("Profile_RemoveFriendBody", user.DisplayName),
                        PrimaryButtonText = Loc.T("Profile_RemoveFriendConfirm"),
                        CloseButtonText = Loc.T("Common_Cancel")
                    };
                    Utilities.UIUtilities.KeepClearOfSystemBars(confirm);
                    if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                    {
                        await discord.DeleteRelationshipAsync(user.Id);
                        nextType = null;
                        updated = true;
                    }
                }
                else if (relType == DSharpPlus.Enums.DiscordRelationshipType.OutgoingRequest ||
                         relType == DSharpPlus.Enums.DiscordRelationshipType.Blocked)
                {
                    await discord.DeleteRelationshipAsync(user.Id);
                    nextType = null;
                    updated = true;
                }
                else
                {
                    await discord.PutRelationshipAsync(user.Id);
                    nextType = relType == DSharpPlus.Enums.DiscordRelationshipType.IncomingRequest
                        ? DSharpPlus.Enums.DiscordRelationshipType.Friend
                        : DSharpPlus.Enums.DiscordRelationshipType.OutgoingRequest;
                    updated = true;
                }
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Профиль: друзья", ex);
            }
            finally
            {
                UpdateFriendButton(User, nextType, hasOverride: updated);
            }
        }

        private static async System.Threading.Tasks.Task<DiscordDmChannel> FindOrCreateDmChannelAsync(ulong userId)
        {
            var discord = DiscordManager.Discord;
            if (discord == null || userId == 0)
                return null;

            var existing = discord.PrivateChannels.Values
                .FirstOrDefault(c => c.Type == DSharpPlus.ChannelType.Private &&
                                     c.Recipients != null &&
                                     c.Recipients.Count == 1 &&
                                     c.Recipients[0]?.Id == userId);
            if (existing != null)
                return existing;

            return await discord.CreateDmChannelAsync(userId);
        }

        /// <summary>Banner, profile colours, badges, pronouns, bio, live/recent activity, game widgets and connections.</summary>
        private async void LoadProfile()
        {
            var id = ++_loadId;
            var user = User;

            ProfileScroll?.ChangeView(null, 0, null, true);

            BannerImage.Source = null;
            BadgesList.ItemsSource = null;
            BioPanel.Visibility = Visibility.Collapsed;
            NitroText.Visibility = Visibility.Collapsed;
            CurrentActivityList.Children.Clear();
            CurrentActivityPanel.Visibility = Visibility.Collapsed;
            RecentActivityList.Children.Clear();
            RecentActivityPanel.Visibility = Visibility.Collapsed;
            WidgetsList.Children.Clear();
            WidgetsPanel.Visibility = Visibility.Collapsed;
            ConnectionsPanel.Children.Clear();

            CurrentActivityHeader.Text = Loc.T("Profile_ActivityHeader");
            RecentActivityHeader.Text = Loc.T("Profile_RecentActivityHeader");
            MessageButtonText.Text = Loc.T("Profile_MessageButton");
            CallButtonText.Text = Loc.T("Profile_CallButton");

            ApplyGuildTag(user?.GuildTag, user?.User?.PrimaryGuild?.BadgeUrl);
            UsernameText.Text = user?.Username ?? "";
            ActionsPanel.Visibility = user != null && !user.IsCurrent ? Visibility.Visible : Visibility.Collapsed;
            UpdateFriendButton(user);
            ApplyAvatarAndStatus(user);
            if (user == null)
                return;

            // Live Gateway presence activities show up immediately without waiting for network calls.
            BuildCurrentActivities(user);

            // Fetch "Recently played" from content-inventory in parallel.
            _ = LoadRecentActivityAsync(id, user.Id);

            UserProfileInfo profile;
            try
            {
                profile = await UserProfileInfo.LoadAsync(user.Id, user.IsMember ? user.Guild?.Id : null);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Профиль", ex);
                BuildConnections(null);
                return;
            }
            if (id != _loadId || profile == null)
                return; // another user was opened meanwhile

            var idText = user.Id.ToString();
            VoiceDiagnostics.Log($"Профиль {idText.Substring(0, Math.Min(6, idText.Length))}…{(user.IsCurrent ? " (свой)" : "")}: " +
                                 $"баннер {(profile.Banner != null ? "есть" : "нет")}, тема {(profile.ThemePrimary.HasValue ? "есть" : "нет")}, " +
                                 $"значков {profile.Badges.Count}, виджетов {profile.GameWidgets.Count}, связей {profile.ConnectedAccounts.Count}, " +
                                 $"Nitro {(profile.PremiumSince.HasValue ? "да" : "нет")}, bio {(profile.Bio != null ? "есть" : "нет")}");

            // banner picture, otherwise the profile colour (Nitro theme first, then the accent colour)
            var bannerColor = profile.ThemePrimary ?? profile.AccentColor;
            if (bannerColor.HasValue)
                BannerHost.Background = new SolidColorBrush(bannerColor.Value);
            if (profile.Banner != null)
                BannerImage.Source = new BitmapImage(profile.Banner) { DecodePixelWidth = 600, DecodePixelType = DecodePixelType.Logical };

            // Nitro profile themes colour the header strip (darkened, so light text stays readable)
            if (profile.ThemePrimary.HasValue)
            {
                var card = new SolidColorBrush(Darken(profile.ThemePrimary.Value, 0.35));
                HeaderBorder.Background = card;
                AvatarFrame.BorderBrush = card;
                HeaderBorder.RequestedTheme = ElementTheme.Dark;
            }

            if (profile.Badges.Count > 0)
                BadgesList.ItemsSource = profile.Badges;

            if (!string.IsNullOrEmpty(profile.GuildTag))
                ApplyGuildTag(profile.GuildTag, profile.GuildTagBadge?.AbsoluteUri);

            if (!string.IsNullOrEmpty(profile.Pronouns))
                UsernameText.Text = $"{user.Username} • {profile.Pronouns}";

            if (profile.PremiumSince.HasValue)
            {
                NitroText.Text = Loc.F("Overlay_NitroSince", profile.PremiumSince.Value.LocalDateTime.ToString("d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
                NitroText.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrEmpty(profile.Bio))
            {
                BioText.Text = profile.Bio;
                BioPanel.Visibility = Visibility.Visible;
            }

            BuildGameWidgets(profile.GameWidgets);
            BuildConnections(profile.ConnectedAccounts);
        }

        private void BuildCurrentActivities(UserViewModel user)
        {
            var presence = user.User?.Presence;
            if (presence == null)
                return;

            var list = new List<DiscordActivity>();
            if (presence.Activities != null && presence.Activities.Count > 0)
                list.AddRange(presence.Activities);
            else if (presence.Activity != null)
                list.Add(presence.Activity);

            if (list.Count == 0)
                return;

            var light = MetroUi.IsLight(this);
            var cardBg = MetroUi.Solid(light ? 0xF0F0F0 : 0x161616);
            var cardBorder = MetroUi.Solid(light ? 0xCCCCCC : 0x333333);
            var secondary = MetroUi.Secondary(this);

            foreach (var activity in list)
            {
                if (activity == null)
                    continue;

                if (activity.ActivityType == ActivityType.Custom)
                {
                    var customText = activity.CustomStatus?.Name;
                    if (string.IsNullOrWhiteSpace(customText))
                        continue;
                    CurrentActivityList.Children.Add(new Border
                    {
                        Background = cardBg,
                        BorderBrush = cardBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 6),
                        Child = new TextBlock
                        {
                            Text = customText,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 13
                        }
                    });
                    continue;
                }

                if (string.IsNullOrWhiteSpace(activity.Name))
                    continue;

                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var imageUrl = activity.RichPresence?.LargeImage?.Url?.AbsoluteUri;
                var iconBox = new Grid
                {
                    Width = 44,
                    Height = 44,
                    Margin = new Thickness(0, 0, 12, 0),
                    Background = MetroUi.PhotoBackground(this),
                    VerticalAlignment = VerticalAlignment.Top
                };
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    iconBox.Children.Add(new Image
                    {
                        Stretch = Stretch.UniformToFill,
                        Source = ImageDiskCache.Create(imageUrl, 64)
                    });
                }
                else
                {
                    iconBox.Children.Add(new FontIcon
                    {
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Glyph = ActivityGlyph(activity.ActivityType),
                        FontSize = 18,
                        Foreground = secondary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                row.Children.Add(iconBox);

                var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                infoStack.Children.Add(new TextBlock
                {
                    Text = ActivityVerb(activity.ActivityType),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    CharacterSpacing = 40,
                    Foreground = secondary
                });
                infoStack.Children.Add(new TextBlock
                {
                    Text = activity.Name,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                var details = activity.RichPresence?.Details;
                if (!string.IsNullOrWhiteSpace(details))
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = details,
                        FontSize = 12,
                        Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var state = activity.RichPresence?.State;
                if (!string.IsNullOrWhiteSpace(state))
                {
                    infoStack.Children.Add(new TextBlock
                    {
                        Text = state,
                        FontSize = 12,
                        Foreground = secondary,
                        TextWrapping = TextWrapping.Wrap
                    });
                }

                var start = activity.RichPresence?.StartTimestamp;
                if (start.HasValue)
                {
                    var elapsed = DateTimeOffset.UtcNow - start.Value;
                    if (elapsed.TotalSeconds >= 0 && elapsed.TotalDays < 30)
                    {
                        infoStack.Children.Add(new TextBlock
                        {
                            Text = FormatElapsed(elapsed),
                            FontSize = 11,
                            Foreground = secondary,
                            Margin = new Thickness(0, 2, 0, 0)
                        });
                    }
                }

                Grid.SetColumn(infoStack, 1);
                row.Children.Add(infoStack);

                CurrentActivityList.Children.Add(new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = row
                });
            }

            if (CurrentActivityList.Children.Count > 0)
                CurrentActivityPanel.Visibility = Visibility.Visible;
        }

        private async System.Threading.Tasks.Task LoadRecentActivityAsync(int loadId, ulong userId)
        {
            IReadOnlyList<RecentActivityItem> items;
            try
            {
                items = await ContentInventoryService.GetForUserAsync(userId);
            }
            catch (Exception)
            {
                return;
            }

            if (loadId != _loadId || items == null || items.Count == 0)
                return;

            var light = MetroUi.IsLight(this);
            var cardBg = MetroUi.Solid(light ? 0xF0F0F0 : 0x161616);
            var cardBorder = MetroUi.Solid(light ? 0xCCCCCC : 0x333333);
            var secondary = MetroUi.Secondary(this);

            RecentActivityList.Children.Clear();
            foreach (var item in items)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var iconBox = new Grid
                {
                    Width = 34,
                    Height = 34,
                    Margin = new Thickness(0, 0, 10, 0),
                    Background = MetroUi.PhotoBackground(this),
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!string.IsNullOrEmpty(item.IconUrl))
                {
                    iconBox.Children.Add(new Image
                    {
                        Stretch = Stretch.UniformToFill,
                        Source = ImageDiskCache.Create(item.IconUrl, 48)
                    });
                }
                else
                {
                    iconBox.Children.Add(new FontIcon
                    {
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Glyph = "\uE7FC",
                        FontSize = 15,
                        Foreground = secondary,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }
                row.Children.Add(iconBox);

                var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                textStack.Children.Add(new TextBlock
                {
                    Text = item.Title,
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                var metaParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(item.Subtitle))
                    metaParts.Add(item.Subtitle);
                if (item.Timestamp.HasValue)
                    metaParts.Add(FormatRelativeTime(item.Timestamp.Value));
                if (item.DurationSeconds.HasValue && item.DurationSeconds.Value >= 60)
                    metaParts.Add(FormatDuration(TimeSpan.FromSeconds(item.DurationSeconds.Value)));
                if (item.StreakDays.HasValue && item.StreakDays.Value > 1)
                    metaParts.Add(Loc.F("Profile_StreakDays", item.StreakDays.Value));

                if (metaParts.Count > 0)
                {
                    textStack.Children.Add(new TextBlock
                    {
                        Text = string.Join(" · ", metaParts),
                        FontSize = 11,
                        Foreground = secondary,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    });
                }

                Grid.SetColumn(textStack, 1);
                row.Children.Add(textStack);

                RecentActivityList.Children.Add(new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = row
                });
            }

            if (RecentActivityList.Children.Count > 0)
                RecentActivityPanel.Visibility = Visibility.Visible;
        }

        private void BuildGameWidgets(List<ProfileGameWidget> widgets)
        {
            WidgetsList.Children.Clear();
            if (widgets == null || widgets.Count == 0)
            {
                WidgetsPanel.Visibility = Visibility.Collapsed;
                return;
            }

            var light = MetroUi.IsLight(this);
            var chipBg = MetroUi.Solid(light ? 0xEAEAEA : 0x1E1E1E);
            var chipBorder = MetroUi.Solid(light ? 0xB8B8B8 : 0x444444);
            var secondary = MetroUi.Secondary(this);

            for (var wi = 0; wi < widgets.Count; wi++)
            {
                var widget = widgets[wi];
                if (widget.Games.Count == 0)
                    continue;

                WidgetsList.Children.Add(new TextBlock
                {
                    Text = FormatWidgetTitle(widget.WidgetType),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    CharacterSpacing = 60,
                    Foreground = secondary,
                    Margin = new Thickness(0, wi == 0 ? 0 : 10, 0, 6)
                });

                var wrap = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                const int maxShown = 12;
                foreach (var game in widget.Games.Take(maxShown))
                {
                    var chipPanel = new StackPanel { Orientation = Orientation.Horizontal };
                    if (!string.IsNullOrEmpty(game.IconUrl))
                    {
                        chipPanel.Children.Add(new Image
                        {
                            Width = 18,
                            Height = 18,
                            Margin = new Thickness(0, 0, 6, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Stretch = Stretch.UniformToFill,
                            Source = ImageDiskCache.Create(game.IconUrl, 32)
                        });
                    }
                    chipPanel.Children.Add(new TextBlock
                    {
                        Text = game.Name,
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = 220
                    });

                    var chip = new Border
                    {
                        Background = chipBg,
                        BorderBrush = chipBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(6, 4, 8, 4),
                        Child = chipPanel
                    };
                    if (!string.IsNullOrWhiteSpace(game.Comment))
                        ToolTipService.SetToolTip(chip, game.Comment);
                    wrap.Children.Add(chip);
                }

                if (widget.Games.Count > maxShown)
                {
                    wrap.Children.Add(new Border
                    {
                        Background = chipBg,
                        BorderBrush = chipBorder,
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(7, 4, 7, 4),
                        Child = new TextBlock
                        {
                            Text = $"+{widget.Games.Count - maxShown}",
                            FontSize = 12,
                            Foreground = secondary,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    });
                }

                WidgetsList.Children.Add(wrap);
            }

            if (WidgetsList.Children.Count > 0)
                WidgetsPanel.Visibility = Visibility.Visible;
        }

        private void BuildConnections(List<ProfileConnection> accounts)
        {
            ConnectionsPanel.Children.Clear();
            var secondary = MetroUi.Secondary(this);

            if (accounts == null || accounts.Count == 0)
            {
                ConnectionsPanel.Children.Add(new TextBlock
                {
                    Text = Loc.T("Profile_NoConnections"),
                    FontSize = 13,
                    Foreground = secondary,
                    Margin = new Thickness(0, 4, 0, 4)
                });
                return;
            }

            var light = MetroUi.IsLight(this);
            var cardBg = MetroUi.Solid(light ? 0xF0F0F0 : 0x161616);
            var cardBorder = MetroUi.Solid(light ? 0xCCCCCC : 0x333333);

            foreach (var acc in accounts)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel();
                textStack.Children.Add(new TextBlock
                {
                    Text = acc.Platform.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    CharacterSpacing = 60,
                    Foreground = secondary
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = acc.Name,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                row.Children.Add(textStack);

                if (acc.Verified)
                {
                    var check = new FontIcon
                    {
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        Glyph = "\uE73E",
                        FontSize = 13,
                        Foreground = MetroUi.Solid(0x23A55A),
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(check, 1);
                    row.Children.Add(check);
                }

                ConnectionsPanel.Children.Add(new Border
                {
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 7, 10, 7),
                    Margin = new Thickness(0, 0, 0, 6),
                    Child = row
                });
            }
        }

        private static string ActivityVerb(ActivityType type)
        {
            switch (type)
            {
                case ActivityType.Playing: return Loc.T("Profile_ActPlaying");
                case ActivityType.ListeningTo: return Loc.T("Profile_ActListening");
                case ActivityType.Watching: return Loc.T("Profile_ActWatching");
                case ActivityType.Streaming: return Loc.T("Profile_ActStreaming");
                case ActivityType.Competing: return Loc.T("Profile_ActCompeting");
                default: return Loc.T("Profile_ActivityHeader");
            }
        }

        private static string ActivityGlyph(ActivityType type)
        {
            switch (type)
            {
                case ActivityType.ListeningTo: return "\uE8D6";
                case ActivityType.Watching: return "\uE714";
                case ActivityType.Streaming: return "\uE7F4";
                default: return "\uE7FC";
            }
        }

        private static string FormatWidgetTitle(string widgetType)
        {
            switch ((widgetType ?? "").ToLowerInvariant())
            {
                case "current_games": return Loc.T("Profile_WidgetCurrentGames");
                case "favorite_games": return Loc.T("Profile_WidgetFavoriteGames");
                case "played_games": return Loc.T("Profile_WidgetPlayedGames");
                case "want_to_play_games": return Loc.T("Profile_WidgetWantToPlay");
                default:
                    return string.IsNullOrWhiteSpace(widgetType)
                        ? Loc.T("Profile_WidgetGamesFallback")
                        : widgetType.Replace('_', ' ').ToUpperInvariant();
            }
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => Loc.F("Profile_Elapsed", FormatDuration(elapsed));

        private static string FormatDuration(TimeSpan span)
        {
            var totalHours = (int)span.TotalHours;
            if (totalHours > 0)
                return Loc.F("Profile_DurationHoursMin", totalHours, span.Minutes);
            var mins = Math.Max(1, (int)span.TotalMinutes);
            return Loc.F("Profile_DurationMin", mins);
        }

        private static string FormatRelativeTime(DateTimeOffset time)
        {
            var diff = DateTimeOffset.UtcNow - time;
            if (diff.TotalMinutes < 5)
                return Loc.T("Profile_JustNow");
            if (diff.TotalHours < 1)
                return Loc.F("Profile_MinutesAgo", Math.Max(1, (int)diff.TotalMinutes));
            if (diff.TotalDays < 1)
                return Loc.F("Profile_HoursAgo", Math.Max(1, (int)diff.TotalHours));
            if (diff.TotalDays < 30)
                return Loc.F("Profile_DaysAgo", Math.Max(1, (int)diff.TotalDays));
            return time.LocalDateTime.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
        }

        private void ApplyAvatarAndStatus(UserViewModel user)
        {
            try { AvatarBitmap.UriSource = null; } catch (Exception) { }
            if (user == null)
            {
                StatusBar.Fill = new SolidColorBrush(Colors.Transparent);
                return;
            }

            try
            {
                var avatarUrl = user.User?.GetAvatarUrl(DSharpPlus.ImageFormat.Png, 128) ?? user.AvatarUrl;
                if (!string.IsNullOrEmpty(avatarUrl))
                    ImageDiskCache.Load(AvatarBitmap, avatarUrl);
            }
            catch (Exception) { }

            var status = user.User?.Presence?.Status ?? UserStatus.Offline;
            switch (status)
            {
                case UserStatus.Online:
                    StatusBar.Fill = MetroUi.Solid(0x23A55A);
                    break;
                case UserStatus.Idle:
                    StatusBar.Fill = MetroUi.Solid(0xF0B232);
                    break;
                case UserStatus.DoNotDisturb:
                    StatusBar.Fill = MetroUi.Solid(0xF23F43);
                    break;
                default:
                    StatusBar.Fill = new SolidColorBrush(Colors.Transparent);
                    break;
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
                ImageDiskCache.Load(GuildTagBadgeImage, badgeUrl);
            }
            else
            {
                GuildTagIcon.Visibility = Visibility.Collapsed;
            }
            GuildTagBorder.Visibility = Visibility.Visible;
        }

        private static Color Darken(Color c, double factor)
            => Color.FromArgb(255, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

        private void DropShadowPanel_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                OverlayService.GetForCurrentView()
                    .CloseOverlay();
            }
        }
    }
}
