using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Input;
using DSharpPlus.Entities;
using Unicord.Universal.Models.Channels;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>One tile of the voice call view: a person, or the Go Live stream of a person.</summary>
    public sealed class VoiceTile
    {
        // muted, dark tones (deterministic per user) so white names and avatars stay readable
        private static readonly Color[] Palette =
        {
            Color.FromArgb(255, 0x3B, 0x42, 0x52), Color.FromArgb(255, 0x4C, 0x3A, 0x51), Color.FromArgb(255, 0x2E, 0x4A, 0x3F),
            Color.FromArgb(255, 0x4A, 0x3B, 0x2E), Color.FromArgb(255, 0x2E, 0x3F, 0x4A), Color.FromArgb(255, 0x4A, 0x2E, 0x35),
            Color.FromArgb(255, 0x3A, 0x3F, 0x2E), Color.FromArgb(255, 0x35, 0x2E, 0x4A),
        };

        public VoiceTile(VoiceMemberViewModel member, bool isStream)
        {
            Member = member;
            IsStream = isStream;
            Background = new SolidColorBrush(isStream ? Color.FromArgb(255, 0x18, 0x19, 0x1C) : Palette[member.UserId % (ulong)Palette.Length]);
        }

        public VoiceMemberViewModel Member { get; }
        public bool IsStream { get; }
        public Brush Background { get; }
        public ICommand WatchCommand => Member.WatchCommand;

        public Visibility PersonVisibility => IsStream ? Visibility.Collapsed : Visibility.Visible;
        public Visibility StreamVisibility => IsStream ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// People in a voice/stage channel as tiles (like the official client's call view). Built on the channel list's
    /// voice members, which already track voice states, speaking and Go Live.
    /// </summary>
    public sealed class VoiceCallViewModel : IDisposable
    {
        private readonly ChannelListViewModel _channel;

        public VoiceCallViewModel(DiscordChannel channel)
        {
            Channel = channel;
            _channel = new ChannelListViewModel(channel);
            _channel.VoiceMembers.CollectionChanged += OnMembersChanged;
            Rebuild();
        }

        public DiscordChannel Channel { get; }
        public string Title => GetChannelTitle(Channel);
        public ObservableCollection<VoiceTile> Tiles { get; } = new ObservableCollection<VoiceTile>();

        public static string GetChannelTitle(DiscordChannel channel)
        {
            if (channel == null)
                return string.Empty;
            if (!string.IsNullOrWhiteSpace(channel.Name))
                return channel.Name;
            if (channel is DiscordDmChannel dm && dm.Recipients != null && dm.Recipients.Count > 0)
            {
                var names = System.Linq.Enumerable.Where(dm.Recipients, r => r != null && !string.IsNullOrWhiteSpace(r.DisplayName))
                    .Select(r => r.DisplayName);
                var joined = string.Join(", ", names);
                if (!string.IsNullOrWhiteSpace(joined))
                    return joined;
            }
            return string.Empty;
        }

        private void OnMembersChanged(object sender, NotifyCollectionChangedEventArgs e) => Rebuild();

        private void Rebuild()
        {
            // VoiceMembers is replaced as a whole on every voice state change (UI thread)
            Tiles.Clear();
            foreach (var member in _channel.VoiceMembers)
            {
                if (member.IsStreaming)
                    Tiles.Add(new VoiceTile(member, true));
            }
            foreach (var member in _channel.VoiceMembers)
                Tiles.Add(new VoiceTile(member, false));
        }

        public void Dispose()
        {
            _channel.VoiceMembers.CollectionChanged -= OnMembersChanged;
        }
    }
}
