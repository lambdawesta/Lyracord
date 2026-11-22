using System;
using System.Collections.Generic;
using System.Linq;
using DSharpPlus;
using DSharpPlus.Entities;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models;
using Unicord.Universal.Models.User;
using Unicord.Universal.Pages.Overlay;
using Unicord.Universal.Services;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Unicord.Universal.Pages
{
    /// <summary>
    /// One server: text channels grouped by category (bright when unread, grey when read, dim when muted, red
    /// counter for mentions) and voice channels with the people in them. Opened from the servers page.
    /// </summary>
    public sealed partial class GuildHubPage : Page
    {
        private DiscordGuild _guild;
        private bool _membersBuilt;

        public GuildHubPage()
        {
            InitializeComponent();
            Loaded += (s, e) => Build(); // build once the page is on screen
            GuildPivot.SelectionChanged += (s, e) =>
            {
                if (GuildPivot.SelectedIndex == 2 && !_membersBuilt && _guild != null)
                {
                    SafeBuildMembers(animate: true);
                }
            };
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            MetroTransitions.PageIn(this, e.NavigationMode);
            _featherPending = true;
            _membersBuilt = false;
            if (!(e.Parameter is DiscordGuild guild))
                return;
            _guild = guild;

            TitleText.Text = guild.Name;
            var icon = string.IsNullOrEmpty(guild.IconHash) ? null : $"https://cdn.discordapp.com/icons/{guild.Id}/{guild.IconHash}.png?size=64";
            IconHost.Child = MetroUi.Photo(icon, guild.Name, guild.Id, 48);

            // user accounts receive a server's member and voice details lazily; refresh once they are in
            if (!guild.IsSynced)
            {
                try
                {
                    await guild.SyncAsync();
                    Build();
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Сервер: синхронизация", ex);
                }
            }
        }

        private bool _featherPending;

        private void Build()
        {
            if (_guild == null)
                return;
            if (ChannelsList == null || VoiceList == null)
            {
                VoiceDiagnostics.Log("Сервер: списки страницы не созданы");
                return;
            }

            string channelsError = null, voiceError = null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { BuildChannels(); } catch (Exception ex) { channelsError = Describe(ex); VoiceDiagnostics.Log("Сервер: каналы", ex); }
            var channelsMs = watch.Elapsed.TotalMilliseconds;
            try { BuildVoice(); } catch (Exception ex) { voiceError = Describe(ex); VoiceDiagnostics.Log("Сервер: голос", ex); }
            if (GuildPivot.SelectedIndex == 2)
                SafeBuildMembers(animate: _featherPending);
            else
                _membersBuilt = false;
            if (_featherPending)
            {
                _featherPending = false; // Build runs more than once per visit; animate the first time only
                var panel = GuildPivot.SelectedIndex == 1 ? VoiceList : GuildPivot.SelectedIndex == 2 ? MembersList : ChannelsList;
                MetroTransitions.FeatherAfterLayout(panel, 30, 28, 10);
            }
            PerfTimings.Add($"страница сервера ({_guild.Channels.Count} каналов): каналы {channelsMs:0} мс, голос {watch.Elapsed.TotalMilliseconds - channelsMs:0} мс, всего", watch.Elapsed.TotalMilliseconds);

            string diag;
            try
            {
                var member = _guild.CurrentMember;
                var channels = _guild.Channels;
                diag = $"каналов {channels?.Count.ToString() ?? "null"}, текстовых {channels?.Values.Count(IsText) ?? 0}, " +
                       $"голосовых {channels?.Values.Count(IsVoice) ?? 0}, строк {ChannelsList.Children.Count}, " +
                       $"участник {(member != null ? "есть" : "НЕТ")}, синхронизирован {_guild.IsSynced}";
            }
            catch (Exception ex)
            {
                diag = "диагностика: " + Describe(ex);
            }
            VoiceDiagnostics.Log("Сервер: " + diag);

            // a failure is shown right on the page (a phone screenshot found the ActualTheme cast this way)
            if (channelsError != null)
                ChannelsList.Children.Add(DiagText(Loc.F("Guild_Error", channelsError)));
            if (voiceError != null)
                VoiceList.Children.Add(DiagText(Loc.F("Guild_Error", voiceError)));
        }

        private TextBlock DiagText(string text)
            => new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = MetroUi.Solid(0xF0A30A) };

        private static string Describe(Exception ex)
        {
            var line = ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim();
            return $"{ex.GetType().Name}: {ex.Message}" + (string.IsNullOrEmpty(line) ? "" : $" ({line})");
        }

        // like the sidebar: owners see everything; without our member object we cannot check, so show the channel
        private bool CanSee(DiscordChannel channel)
        {
            try
            {
                var member = _guild.CurrentMember;
                if (member == null || member.IsOwner)
                    return true;
                return channel.PermissionsFor(member).HasPermission(Permissions.AccessChannels);
            }
            catch (Exception)
            {
                return true;
            }
        }

        private static bool IsText(DiscordChannel c)
            => c.Type == ChannelType.Text || c.Type == ChannelType.Announcement || (int)c.Type == 15 /* forum */ || (int)c.Type == 16 /* media */;

        private static bool IsVoice(DiscordChannel c) => c.Type == ChannelType.Voice || c.Type == ChannelType.Stage;

        /// <summary>Channels of one kind grouped like the sidebar: uncategorised first, then categories by position.</summary>
        private List<(DiscordChannel Category, List<DiscordChannel> Channels)> Grouped(Func<DiscordChannel, bool> kind)
        {
            var result = new List<(DiscordChannel, List<DiscordChannel>)>();
            var all = _guild.Channels.Values.Where(c => kind(c) && CanSee(c)).ToList();
            var loose = all.Where(c => c.ParentId == null).OrderBy(c => c.Position).ToList();
            if (loose.Count > 0)
                result.Add((null, loose));

            foreach (var category in _guild.Channels.Values.Where(c => c.Type == ChannelType.Category).OrderBy(c => c.Position))
            {
                var inside = all.Where(c => c.ParentId == category.Id).OrderBy(c => c.Position).ToList();
                if (inside.Count > 0)
                    result.Add((category, inside));
            }
            return result;
        }

        // ------------------------------------------------------------------ text channels

        private void BuildChannels()
        {
            ChannelsList.Children.Clear();
            foreach (var (category, channels) in Grouped(IsText))
            {
                if (category != null)
                    ChannelsList.Children.Add(Section(category.Name));
                foreach (var channel in channels)
                {
                    try { ChannelsList.Children.Add(ChannelRow(channel)); }
                    catch (Exception ex) { VoiceDiagnostics.Log($"Сервер: канал {channel.Type}", ex); }
                }
            }
            if (ChannelsList.Children.Count == 0)
                ChannelsList.Children.Add(new TextBlock { Text = Loc.T("Guild_NoChannels"), FontSize = 18, Foreground = MetroUi.Secondary(this) });
        }

        private TextBlock Section(string name)
            => new TextBlock
            {
                Text = (name ?? "").ToUpperInvariant(),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 100,
                Foreground = MetroUi.Secondary(this),
                Margin = new Thickness(0, 16, 0, 6),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

        private FrameworkElement ChannelRow(DiscordChannel channel)
        {
            var muted = Try(() => channel.IsMuted());
            var unread = !muted && Try(() => channel.IsUnread());
            var mentions = channel.ReadState?.MentionCount ?? 0;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8), Background = new SolidColorBrush(Windows.UI.Colors.Transparent) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var prefix = channel.Type == ChannelType.Announcement ? Loc.T("Guild_Announcements") : (int)channel.Type == 15 ? Loc.T("Guild_Forum") : "";
            row.Children.Add(new TextBlock
            {
                Text = channel.Name,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = unread ? FontWeights.Normal : FontWeights.Light,
                FontSize = 24,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = muted ? MetroUi.Dim(this) : unread ? MetroUi.Foreground(this) : MetroUi.Secondary(this)
            });
            if (prefix.Length > 0 || !string.IsNullOrEmpty(channel.Topic))
            {
                // small line under the name: kind and topic
                var stack = new StackPanel();
                var name = (TextBlock)row.Children[0];
                row.Children.Clear();
                stack.Children.Add(name);
                stack.Children.Add(new TextBlock
                {
                    Text = prefix + (channel.Topic ?? "").Replace('\n', ' '),
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = MetroUi.Secondary(this),
                    Margin = new Thickness(0, -2, 0, 0)
                });
                row.Children.Add(stack);
            }
            if (mentions > 0)
            {
                var counter = MetroUi.Counter(mentions);
                counter.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(counter, 1);
                row.Children.Add(counter);
            }

            MetroUi.Tilt(row, 0.98);
            row.Tapped += async (s, e) =>
            {
                try { await DiscordNavigationService.GetForCurrentView().NavigateAsync(channel); }
                catch (Exception ex) { VoiceDiagnostics.Log("Сервер: открыть канал", ex); }
            };
            return row;
        }

        // ------------------------------------------------------------------ voice channels

        private void BuildVoice()
        {
            VoiceList.Children.Clear();
            var currentVoice = (Window.Current.Content.FindChild<DiscordPage>()?.DataContext as DiscordPageViewModel)?.VoiceModel?.Channel?.Id;

            foreach (var (category, channels) in Grouped(IsVoice))
            {
                if (category != null)
                    VoiceList.Children.Add(Section(category.Name));
                foreach (var channel in channels)
                {
                    try { VoiceList.Children.Add(VoiceRow(channel, channel.Id == currentVoice)); }
                    catch (Exception ex) { VoiceDiagnostics.Log("Сервер: голосовой", ex); }
                }
            }
            if (VoiceList.Children.Count == 0)
                VoiceList.Children.Add(new TextBlock { Text = Loc.T("Guild_NoVoice"), FontSize = 18, Foreground = MetroUi.Secondary(this) });
        }

        private FrameworkElement VoiceRow(DiscordChannel channel, bool current)
        {
            var people = new List<DiscordVoiceState>();
            foreach (var state in _guild.VoiceStates.Values)
            {
                try
                {
                    if (state?.Channel?.Id == channel.Id && state.User != null)
                        people.Add(state);
                }
                catch (Exception) { }
            }
            // people streaming first, like the official call view
            people = people.OrderByDescending(p => p.IsSelfStream).ThenBy(p => p.User.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12), Background = new SolidColorBrush(Windows.UI.Colors.Transparent) };
            row.Children.Add(new TextBlock
            {
                Text = channel.Name,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = people.Count > 0 ? FontWeights.Normal : FontWeights.Light,
                FontSize = 24,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = people.Count > 0 ? MetroUi.Foreground(this) : MetroUi.Secondary(this)
            });

            var streams = people.Count(p => p.IsSelfStream);
            var line = current ? Loc.T("Guild_YouAreHere") : people.Count > 0 ? Loc.Plural("People", people.Count) : Loc.T("Guild_Empty");
            if (streams > 0)
                line += streams == 1 ? Loc.T("Guild_OneStream") : Loc.F("Guild_Streams", streams);
            if (channel.Type == ChannelType.Stage)
                line = Loc.T("Guild_Stage") + line;
            row.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 13,
                Foreground = current ? new SolidColorBrush(MetroColors.Accent()) : MetroUi.Secondary(this)
            });

            // each person on its own line: photo, name, muted/deafened, and a red LIVE badge that opens the stream
            const int shown = 12;
            foreach (var state in people.Take(shown))
            {
                try { row.Children.Add(PersonLine(channel, state)); }
                catch (Exception ex) { VoiceDiagnostics.Log("Сервер: участник", ex); }
            }
            if (people.Count > shown)
                row.Children.Add(new TextBlock { Text = Loc.F("Guild_AndMore", people.Count - shown), FontSize = 14, Margin = new Thickness(0, 4, 0, 0), Foreground = MetroUi.Secondary(this) });

            MetroUi.Tilt(row, 0.98);
            row.Tapped += async (s, e) =>
            {
                // joins the channel, or just shows the call view when already in it
                try { await DiscordNavigationService.GetForCurrentView().NavigateAsync(channel); }
                catch (Exception ex) { VoiceDiagnostics.Log("Сервер: голосовой канал", ex); }
            };
            return row;
        }

        private FrameworkElement PersonLine(DiscordChannel channel, DiscordVoiceState state)
        {
            var user = state.User;
            // not state.Member: voice states from the initial guild load carry no member object and that getter
            // throws NullReferenceException, which hid every participant
            var name = user.DisplayName;
            try
            {
                if (_guild.Members.TryGetValue(user.Id, out var member) && member != null && !string.IsNullOrEmpty(member.DisplayName))
                    name = member.DisplayName;
            }
            catch (Exception) { }
            var line = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(MetroUi.Photo(user.GetAvatarUrl(ImageFormat.Png, 64), name, user.Id, 44));

            var text = new TextBlock
            {
                Text = name,
                FontSize = 18,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiLight,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = MetroUi.Foreground(this)
            };
            var tagChip = MetroUi.GuildTag(this, user.PrimaryGuild, compact: true);
            if (tagChip != null)
            {
                var nameRow = new Grid { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                nameRow.Children.Add(text);
                Grid.SetColumn(tagChip, 1);
                nameRow.Children.Add(tagChip);
                Grid.SetColumn(nameRow, 1);
                line.Children.Add(nameRow);
            }
            else
            {
                text.Margin = new Thickness(12, 0, 8, 0);
                Grid.SetColumn(text, 1);
                line.Children.Add(text);
            }

            var marks = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (state.IsSelfMuted || state.IsServerMuted || state.IsSuppressed)
                marks.Children.Add(Mark(""));
            if (state.IsSelfDeafened || state.IsServerDeafened)
                marks.Children.Add(Mark(""));
            if (state.IsSelfStream)
            {
                var live = new Border
                {
                    Background = MetroUi.Solid(0xE51400),
                    Padding = new Thickness(8, 2, 8, 3),
                    Margin = new Thickness(8, 0, 0, 0),
                    Child = new TextBlock { Text = "LIVE", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Windows.UI.Colors.White) }
                };
                MetroUi.Tilt(live);
                live.Tapped += async (s, e) =>
                {
                    e.Handled = true; // the stream, not joining the channel
                    try { await DiscordNavigationService.GetForCurrentView().NavigateToStreamAsync(channel, user.Id, name); }
                    catch (Exception ex) { VoiceDiagnostics.Log("Сервер: трансляция", ex); }
                };
                marks.Children.Add(live);
            }
            Grid.SetColumn(marks, 2);
            line.Children.Add(marks);
            return line;
        }

        private FontIcon Mark(string glyph)
            => new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = glyph,
                FontSize = 16,
                Margin = new Thickness(6, 0, 0, 0),
                Foreground = MetroUi.Secondary(this)
            };

        private static bool Try(Func<bool> f)
        {
            try { return f(); } catch (Exception) { return false; }
        }

        // ------------------------------------------------------------------ members

        private void SafeBuildMembers(bool animate)
        {
            try
            {
                BuildMembers();
                _membersBuilt = true;
                if (animate)
                    MetroTransitions.FeatherAfterLayout(MembersList, 30, 28, 10);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Сервер: участники", ex);
                MembersList?.Children.Add(DiagText(Loc.F("Guild_Error", Describe(ex))));
            }
        }

        private void BuildMembers()
        {
            if (MembersList == null || _guild == null)
                return;

            MembersList.Children.Clear();
            var discord = DiscordManager.Discord;
            if (discord == null)
                return;

            const int maxShown = 120;
            var allMembers = _guild.Members?.Values?.Where(m => m != null).ToList() ?? new List<DiscordMember>();
            if (allMembers.Count == 0)
            {
                MembersList.Children.Add(new TextBlock
                {
                    Text = Loc.T("Guild_NoMembers"),
                    FontSize = 18,
                    Foreground = MetroUi.Secondary(this)
                });
                return;
            }

            // Online / idle / DND members first (grouped by hoisted role, or "ONLINE"), then offline.
            var entries = allMembers.Select(m =>
            {
                var statusText = ServersHubPage.PresenceText(discord, m.Id, out var bar);
                DiscordRole hoisted = null;
                try
                {
                    hoisted = m.Roles?.Where(r => r != null && r.IsHoisted).OrderByDescending(r => r.Position).FirstOrDefault();
                }
                catch (Exception) { }
                return new
                {
                    Member = m,
                    Name = string.IsNullOrWhiteSpace(m.DisplayName) ? (m.Username ?? "?") : m.DisplayName,
                    StatusText = statusText,
                    Bar = bar,
                    Online = bar != null,
                    Hoisted = hoisted
                };
            }).ToList();

            var onlineGroups = entries
                .Where(x => x.Online)
                .GroupBy(x => x.Hoisted)
                .OrderByDescending(g => g.Key?.Position ?? int.MinValue);

            var offline = entries
                .Where(x => !x.Online)
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var rendered = 0;
            var total = entries.Count;

            void AddHeader(string title, int count, bool first)
            {
                MembersList.Children.Add(new TextBlock
                {
                    Text = $"{title} — {count}".ToUpperInvariant(),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    CharacterSpacing = 100,
                    Foreground = MetroUi.Secondary(this),
                    Margin = new Thickness(0, first ? 4 : 18, 0, 10)
                });
            }

            foreach (var group in onlineGroups)
            {
                if (rendered >= maxShown)
                    break;
                var items = group.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                var headerTitle = group.Key != null ? group.Key.Name : Loc.T("Guild_MembersOnline");
                AddHeader(headerTitle, items.Count, rendered == 0);

                foreach (var entry in items)
                {
                    if (rendered >= maxShown)
                        break;
                    AddMemberRow(entry.Member, entry.Name, entry.StatusText, entry.Bar);
                    rendered++;
                }
            }

            if (offline.Count > 0 && rendered < maxShown)
            {
                AddHeader(Loc.T("Guild_MembersOffline"), offline.Count, rendered == 0);
                foreach (var entry in offline)
                {
                    if (rendered >= maxShown)
                        break;
                    AddMemberRow(entry.Member, entry.Name, entry.StatusText, null);
                    rendered++;
                }
            }

            if (total > rendered)
            {
                MembersList.Children.Add(new TextBlock
                {
                    Text = Loc.F("Guild_AndMore", total - rendered),
                    FontSize = 14,
                    Margin = new Thickness(0, 8, 0, 0),
                    Foreground = MetroUi.Secondary(this)
                });
            }
        }

        private void AddMemberRow(DiscordMember member, string name, string subtitle, Brush statusBar)
        {
            var avatarUrl = member.GetAvatarUrl(ImageFormat.Png, 64);
            var photo = MetroUi.Photo(avatarUrl, name, member.Id, 52);
            var row = MetroUi.Row(this, photo, name, subtitle, false, statusBar, member.PrimaryGuild);
            var guildId = _guild.Id;
            var userId = member.Id;
            row.Tapped += async (s, e) =>
            {
                try
                {
                    await OverlayService.GetForCurrentView()
                        .ShowOverlayAsync<UserInfoOverlayPage>(new UserViewModel(userId, guildId));
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Сервер: профиль участника", ex);
                }
            };
            MembersList.Children.Add(row);
        }
    }
}
