using System;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using Unicord.Universal.Services;
using Unicord.Universal.Utilities;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unicord.Universal.Pages
{
    /// <summary>Small building blocks shared by the Metro pages (hub, servers, server, friends).</summary>
    internal static class MetroUi
    {
        // The app sets one theme for everything at start-up (App: Application.RequestedTheme from the settings, or the
        // system theme). FrameworkElement.ActualTheme must not be used: on Windows 10 Mobile 15254 the cast to its
        // interface throws InvalidCastException, which left the Metro lists empty.
        public static bool IsLight(FrameworkElement element)
        {
            try
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Light;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static Brush Foreground(FrameworkElement e) => Solid(IsLight(e) ? 0x000000 : 0xFFFFFF);
        public static Brush Secondary(FrameworkElement e) => Solid(IsLight(e) ? 0x6B6B6B : 0x9A9A9A);
        public static Brush Dim(FrameworkElement e) => Solid(IsLight(e) ? 0xB0B0B0 : 0x555555);
        public static Brush PhotoBackground(FrameworkElement e) => Solid(IsLight(e) ? 0xE0E0E0 : 0x262626);

        public static SolidColorBrush Solid(int rgb) => new SolidColorBrush(MetroColors.C(rgb));

        /// <summary>A stable palette colour for things without a picture (by id).</summary>
        public static Color ColorFor(ulong id) => MetroColors.Palette[(int)(id % (ulong)MetroColors.Palette.Count)].Color;

        /// <summary>Square photo: the picture if there is one, otherwise initials on a palette colour.</summary>
        public static Grid Photo(string url, string name, ulong id, double size, int decode = 0)
        {
            var photo = new Grid { Width = size, Height = size, Background = new SolidColorBrush(ColorFor(id)) };
            if (!string.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                photo.Children.Add(new Image
                {
                    Stretch = Stretch.UniformToFill,
                    Source = Services.ImageDiskCache.Create(url, decode > 0 ? decode : (int)size)
                });
            }
            else
            {
                photo.Children.Add(new TextBlock
                {
                    Text = Initials(name),
                    FontSize = Math.Max(12, size * 0.36),
                    FontWeight = FontWeights.SemiLight,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            return photo;
        }

        public static string Initials(string name)
        {
            var words = (name ?? "?").Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            var letters = words.Take(2).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]));
            var text = string.Concat(letters);
            return text.Length > 0 ? text : "?";
        }

        /// <summary>Metro "tilt": the element sinks a little while pressed.</summary>
        public static void Tilt(FrameworkElement element, double pressed = 0.97)
        {
            element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            element.RenderTransform = new ScaleTransform();
            void Set(double scale)
            {
                var t = (ScaleTransform)element.RenderTransform;
                t.ScaleX = t.ScaleY = scale;
            }
            element.PointerPressed += (s, e) => Set(pressed);
            element.PointerReleased += (s, e) => Set(1);
            element.PointerCanceled += (s, e) => Set(1);
            element.PointerCaptureLost += (s, e) => Set(1);
            element.PointerExited += (s, e) => Set(1);
        }

        /// <summary>Square Metro server tag chip ([icon TAG]), or null if the user has no visible tag.</summary>
        public static Border GuildTag(FrameworkElement owner, DiscordPrimaryGuild primaryGuild, bool compact = false)
        {
            if (primaryGuild == null || !primaryGuild.HasTag)
                return null;

            var light = IsLight(owner);
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var iconSize = compact ? 13 : 15;
            var badgeUrl = primaryGuild.BadgeUrl;
            if (!string.IsNullOrEmpty(badgeUrl))
            {
                panel.Children.Add(new Image
                {
                    Width = iconSize,
                    Height = iconSize,
                    Margin = new Thickness(0, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = Stretch.Uniform,
                    Source = ImageDiskCache.Create(badgeUrl, 32)
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = primaryGuild.Tag,
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = FontWeights.SemiBold,
                FontSize = compact ? 11 : 12,
                CharacterSpacing = 40,
                Foreground = Solid(light ? 0x1A1A1A : 0xEBEBEB),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap
            });

            return new Border
            {
                Background = Solid(light ? 0xEAEAEA : 0x1E1E1E),
                BorderBrush = Solid(light ? 0xB0B0B0 : 0x4A4A4A),
                BorderThickness = new Thickness(1),
                Padding = compact ? new Thickness(4, 1, 5, 1) : new Thickness(5, 2, 6, 2),
                Margin = new Thickness(8, 2, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = panel
            };
        }

        /// <summary>A list row: square photo, a big name (with optional server tag) and a small line below.</summary>
        public static Grid Row(FrameworkElement owner, FrameworkElement photo, string title, string subtitle, bool bright, Brush statusBar = null, DiscordPrimaryGuild guildTag = null)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 12), Background = new SolidColorBrush(Colors.Transparent) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            if (statusBar != null)
                row.Children.Add(new Border { Background = statusBar });

            Grid.SetColumn(photo, 1);
            row.Children.Add(photo);

            var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var titleBlock = new TextBlock
            {
                Text = title ?? "",
                FontFamily = new FontFamily("Segoe UI"),
                FontWeight = bright ? FontWeights.Normal : FontWeights.SemiLight,
                FontSize = 22,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = bright ? Foreground(owner) : Foreground(owner)
            };
            var tagChip = GuildTag(owner, guildTag, compact: false);
            if (tagChip != null)
            {
                var titleRow = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                titleRow.Children.Add(titleBlock);
                Grid.SetColumn(tagChip, 1);
                titleRow.Children.Add(tagChip);
                text.Children.Add(titleRow);
            }
            else
            {
                text.Children.Add(titleBlock);
            }
            if (!string.IsNullOrEmpty(subtitle))
            {
                text.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 14,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Secondary(owner)
                });
            }
            Grid.SetColumn(text, 2);
            row.Children.Add(text);

            Tilt(row, 0.98);
            return row;
        }

        /// <summary>Small square counter (mentions) in the system-red of Windows Phone.</summary>
        public static Border Counter(int count)
            => new Border
            {
                Background = Solid(0xE51400),
                Padding = new Thickness(6, 1, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock { Text = count > 99 ? "99+" : count.ToString(), FontSize = 13, Foreground = new SolidColorBrush(Colors.White) }
            };

        /// <summary>Metro dialog to send a friend request by Discord username (modern or legacy user#1234).</summary>
        public static async Task<bool> ShowAddFriendDialogAsync()
        {
            var discord = DiscordManager.Discord;
            if (discord == null)
                return false;

            var input = new TextBox
            {
                PlaceholderText = Loc.T("Social_AddFriendPlaceholder"),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 8, 0, 0)
            };
            var status = new TextBlock
            {
                Text = Loc.T("Social_AddFriendHint"),
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var body = new StackPanel();
            body.Children.Add(input);
            body.Children.Add(status);

            var dialog = new ContentDialog
            {
                Title = Loc.T("Social_AddFriendTitle"),
                Content = body,
                PrimaryButtonText = Loc.T("Social_SendRequest"),
                CloseButtonText = Loc.T("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            UIUtilities.KeepClearOfSystemBars(dialog);

            var sent = false;
            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var raw = (input.Text ?? "").Trim().TrimStart('@');
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        status.Text = Loc.T("Social_AddFriendEmpty");
                        args.Cancel = true;
                        return;
                    }

                    string username = raw;
                    int? discriminator = null;
                    var hash = raw.LastIndexOf('#');
                    if (hash > 0 && hash < raw.Length - 1)
                    {
                        var discPart = raw.Substring(hash + 1).Trim();
                        if (int.TryParse(discPart, out var discNum) && discNum > 0)
                        {
                            username = raw.Substring(0, hash).Trim();
                            discriminator = discNum;
                        }
                    }

                    dialog.IsPrimaryButtonEnabled = false;
                    status.Text = Loc.T("Social_Sending");
                    await discord.SendFriendRequestAsync(username, discriminator);
                    sent = true;
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Друзья: добавить", ex);
                    status.Text = FormatSocialError(ex, isInvite: false);
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
            return sent;
        }

        /// <summary>Metro dialog to join a Discord server by invite link or code.</summary>
        public static async Task<DiscordInvite> ShowJoinServerDialogAsync()
        {
            var discord = DiscordManager.Discord;
            if (discord == null)
                return null;

            var input = new TextBox
            {
                PlaceholderText = Loc.T("Social_JoinServerPlaceholder"),
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 8, 0, 0)
            };
            var status = new TextBlock
            {
                Text = Loc.T("Social_JoinServerHint"),
                FontSize = 13,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var browserBtn = new Button
            {
                Content = Loc.T("Social_OpenInBrowser"),
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 8, 0, 0),
                BorderThickness = new Thickness(2),
                Background = new SolidColorBrush(Colors.Transparent)
            };
            string lastCode = null;
            browserBtn.Click += async (s, e) =>
            {
                if (!string.IsNullOrEmpty(lastCode))
                {
                    try { await Launcher.LaunchUriAsync(new Uri("https://discord.gg/" + lastCode)); }
                    catch (Exception) { }
                }
            };

            var body = new StackPanel();
            body.Children.Add(input);
            body.Children.Add(status);
            body.Children.Add(browserBtn);

            var dialog = new ContentDialog
            {
                Title = Loc.T("Social_JoinServerTitle"),
                Content = body,
                PrimaryButtonText = Loc.T("Social_JoinButton"),
                CloseButtonText = Loc.T("Common_Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            UIUtilities.KeepClearOfSystemBars(dialog);

            DiscordInvite joined = null;
            dialog.PrimaryButtonClick += async (s, args) =>
            {
                var deferral = args.GetDeferral();
                try
                {
                    var code = ExtractInviteCode(input.Text);
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        status.Text = Loc.T("Social_JoinServerEmpty");
                        args.Cancel = true;
                        return;
                    }

                    lastCode = code;
                    browserBtn.Visibility = Visibility.Collapsed;
                    dialog.IsPrimaryButtonEnabled = false;
                    status.Text = Loc.T("Social_Joining");
                    joined = await discord.AcceptInviteAsync(code);
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Серверы: присоединиться", ex);
                    var msg = FormatSocialError(ex, isInvite: true, out var needsCaptcha);
                    status.Text = msg;
                    browserBtn.Visibility = needsCaptcha ? Visibility.Visible : Visibility.Collapsed;
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
            return joined;
        }

        internal static string ExtractInviteCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var text = raw.Trim();
            var q = text.IndexOf('?');
            if (q >= 0)
                text = text.Substring(0, q);
            text = text.TrimEnd('/');

            var slash = text.LastIndexOf('/');
            if (slash >= 0 && slash < text.Length - 1)
                text = text.Substring(slash + 1);

            return text.Trim();
        }

        private static string FormatSocialError(Exception ex, bool isInvite)
            => FormatSocialError(ex, isInvite, out _);

        private static string FormatSocialError(Exception ex, bool isInvite, out bool needsCaptcha)
        {
            needsCaptcha = false;
            if (ex is DiscordException dex)
            {
                var raw = dex.WebResponse?.Response ?? "";
                if (raw.IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    needsCaptcha = true;
                    return Loc.T("Social_CaptchaRequired");
                }
                if (!string.IsNullOrWhiteSpace(dex.JsonMessage))
                    return dex.JsonMessage;
            }
            if (ex is NotFoundException)
                return isInvite ? Loc.T("Social_InviteNotFound") : Loc.T("Social_UserNotFound");
            return ex.Message;
        }
    }
}
