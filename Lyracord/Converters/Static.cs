using Unicord.Universal.Models.Messages;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Converters
{
    public class Static
    {
        public static bool NotNull(object obj)
            => obj != null;

        public static bool Not(bool b)
            => !b;

        public static bool Is(MessageViewModelState state, MessageViewModelState other)
            => state == other;

        public static bool IsNot(MessageViewModelState state, MessageViewModelState other)
            => state != other;

        // ---- Metro direct-message bubbles (x:Bind functions; own messages on the right in the accent colour)

        public static HorizontalAlignment BubbleAlignment(bool mine)
            => mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        /// <summary>Keeps a gap on the other side so long messages still read as "theirs" or "mine".</summary>
        public static Thickness BubbleMargin(bool mine)
            => mine ? new Thickness(48, 0, 0, 0) : new Thickness(0, 0, 48, 0);

        // Brushes are created once and shared by every bubble: asking the system for the accent colour and making new
        // brushes per message made scrolling a long conversation noticeably slower on the phone.
        private static Brush _mineBrush, _otherBrush, _mineText, _otherText;

        public static Brush BubbleBackground(bool mine)
        {
            if (mine)
                return _mineBrush ?? (_mineBrush = new SolidColorBrush(Services.MetroColors.Accent()));
            return _otherBrush ?? (_otherBrush = Find("MetroBubbleOtherBrush") ?? new SolidColorBrush(Color.FromArgb(255, 0x2B, 0x2B, 0x2B)));
        }

        public static Brush BubbleForeground(bool mine)
        {
            if (mine)
                return _mineText ?? (_mineText = new SolidColorBrush(Colors.White));
            return _otherText ?? (_otherText = Find("MetroBubbleOtherForeground") ?? new SolidColorBrush(Colors.White));
        }

        private static Brush Find(string key)
        {
            try
            {
                return Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>The little triangle under a bubble points to its side: right for mine, left for theirs.</summary>
        public static PointCollection BubbleTail(bool mine)
            => mine
                ? new PointCollection { new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(14, 0), new Windows.Foundation.Point(14, 12) }
                : new PointCollection { new Windows.Foundation.Point(0, 0), new Windows.Foundation.Point(14, 0), new Windows.Foundation.Point(0, 12) };

        /// <summary>In group conversations and server channels the other people's bubbles carry their avatar, name and server tag.</summary>
        public static bool ShowBubbleAuthor(DSharpPlus.Entities.DiscordChannel channel, bool mine)
            => !mine && channel != null && (channel.Type == DSharpPlus.ChannelType.Group || channel.GuildId != null);

        public static Visibility BubbleAuthorVisibility(DSharpPlus.Entities.DiscordChannel channel, bool mine)
            => ShowBubbleAuthor(channel, mine) ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Role colour for a bubble author in a server channel, or the system accent colour when the user has no role colour.</summary>
        public static Brush BubbleAuthorForeground(DSharpPlus.Entities.DiscordColor color)
            => color.Value != 0
                ? new SolidColorBrush(Color.FromArgb(255, color.R, color.G, color.B))
                : (_mineBrush ?? (_mineBrush = new SolidColorBrush(Services.MetroColors.Accent())));

        /// <summary>Quoted reply line inside a Metro bubble ("↩ Author: text").</summary>
        public static string BubbleReplyText(MessageViewModel reply)
            => reply == null
                ? ""
                : string.IsNullOrWhiteSpace(reply.Content)
                    ? $"↩ {reply.Author?.DisplayName}"
                    : $"↩ {reply.Author?.DisplayName}: {reply.Content}";

        public static Visibility VisibleIf(bool value)
            => value ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility NotNullVisibility(object obj)
            => obj != null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>
        /// Safely converts a URL string (including null/empty) to an ImageSource for compiled {x:Bind} on Image.Source.
        /// Direct {x:Bind} of string -> ImageSource calls XamlBindingHelper.ConvertValue, which throws ArgumentException on null.
        /// </summary>
        public static ImageSource SmallImageFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            if (!System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri))
                return null;
            try
            {
                return new Windows.UI.Xaml.Media.Imaging.BitmapImage(uri)
                {
                    DecodePixelWidth = 32,
                    DecodePixelHeight = 32,
                    DecodePixelType = Windows.UI.Xaml.Media.Imaging.DecodePixelType.Logical
                };
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
