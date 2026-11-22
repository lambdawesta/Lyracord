using System;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using DSharpPlus.Entities;
using Unicord.Universal.Models.User;
using Unicord.Universal.Models.Voice;
using Unicord.Universal.Services;
using Windows.UI.Xaml;

namespace Unicord.Universal.Models.Channels
{
    /// <summary>
    /// One person connected to a voice channel, shown under the channel in the channel list.
    /// </summary>
    public sealed class VoiceMemberViewModel : INotifyPropertyChanged
    {
        private readonly UserViewModel _user;
        private bool _isSpeaking;
        private readonly DiscordChannel _channel;

        internal VoiceMemberViewModel(DiscordVoiceState state, ViewModelBase parent, DiscordUser fallbackUser = null, DiscordChannel fallbackChannel = null)
        {
            var user = state.User ?? fallbackUser;
            UserId = user?.Id ?? 0;
            _user = new UserViewModel(user, state.Guild?.Id, parent);

            IsMuted = state.IsSelfMuted || state.IsServerMuted || state.IsSuppressed;
            IsDeafened = state.IsSelfDeafened || state.IsServerDeafened;
            IsStreaming = state.IsSelfStream;
            IsVideo = state.IsSelfVideo;
            _isSpeaking = VoiceSpeakingState.IsSpeaking(UserId);
            _channel = state.Channel ?? fallbackChannel;
            // Tapping the LIVE badge opens the stream (Go Live) of this person.
            WatchCommand = new AsyncRelayCommand(() => DiscordNavigationService.GetForCurrentView().NavigateToStreamAsync(_channel, UserId, DisplayName));
        }

        internal VoiceMemberViewModel(DiscordUser user, DiscordChannel channel, bool isMuted, bool isDeafened, ViewModelBase parent)
        {
            UserId = user?.Id ?? 0;
            _user = new UserViewModel(user, channel?.Guild?.Id, parent);

            IsMuted = isMuted;
            IsDeafened = isDeafened;
            IsStreaming = false;
            IsVideo = false;
            _isSpeaking = VoiceSpeakingState.IsSpeaking(UserId);
            _channel = channel;
            WatchCommand = new AsyncRelayCommand(() => DiscordNavigationService.GetForCurrentView().NavigateToStreamAsync(_channel, UserId, DisplayName));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ICommand WatchCommand { get; }

        /// <summary>Green ring around the avatar while this person is talking in our current call (UI thread only).</summary>
        public bool IsSpeaking
        {
            get => _isSpeaking;
            set
            {
                if (_isSpeaking == value) return;
                _isSpeaking = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSpeaking)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpeakingVisibility)));
            }
        }

        public Visibility SpeakingVisibility => IsSpeaking ? Visibility.Visible : Visibility.Collapsed;

        public ulong UserId { get; }

        public bool IsMuted { get; }
        public bool IsDeafened { get; }
        public bool IsStreaming { get; }
        public bool IsVideo { get; }

        // UserViewModel throws when the guild or user is not cached yet, which is common for
        // voice states that arrive before the member list; fall back instead of breaking the list.
        public string DisplayName
        {
            get
            {
                try
                {
                    var name = _user.DisplayName;
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
                catch (Exception)
                {
                }
                return "…";
            }
        }

        public Uri AvatarUri
        {
            get
            {
                try
                {
                    var url = _user.AvatarUrl;
                    return string.IsNullOrEmpty(url) ? null : new Uri(url);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        // Deafened implies muted in Discord's UI, so only one of the two icons is shown.
        public Visibility MutedVisibility => IsMuted && !IsDeafened ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeafenedVisibility => IsDeafened ? Visibility.Visible : Visibility.Collapsed;
        public Visibility LiveVisibility => IsStreaming ? Visibility.Visible : Visibility.Collapsed;
        public Visibility VideoVisibility => IsVideo && !IsStreaming ? Visibility.Visible : Visibility.Collapsed;

        public string GuildTag
        {
            get { try { return _user.GuildTag; } catch (Exception) { return null; } }
        }

        public Visibility GuildTagVisibility
        {
            get { try { return _user.GuildTagVisibility; } catch (Exception) { return Visibility.Collapsed; } }
        }

        public string GuildTagBadgeUrl
        {
            get { try { return _user.GuildTagBadgeUrl; } catch (Exception) { return null; } }
        }

        public Visibility GuildTagBadgeVisibility
        {
            get { try { return _user.GuildTagBadgeVisibility; } catch (Exception) { return Visibility.Collapsed; } }
        }
    }
}
