using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using Unicord.Universal.Extensions;
using Unicord.Universal.Models.Guild;
using Unicord.Universal.Models.Voice;

namespace Unicord.Universal.Models.Channels
{
    public class ChannelListViewModel : ChannelViewModel
    {
        private readonly GuildChannelListViewModel _guildChannelList;
        private bool _hasVoiceMembers;

        public ChannelListViewModel(DiscordChannel channel, GuildChannelListViewModel guildChannelList = null)
            : base(channel.Id)
        {
            _guildChannelList = guildChannelList;

            if (IsVoiceLike(channel))
            {
                VoiceMembers = new ObservableCollection<VoiceMemberViewModel>();
                RefreshVoiceMembers();

                WeakReferenceMessenger.Default.Register<ChannelListViewModel, VoiceStateUpdateEventArgs>(this, static (r, m) => r.OnVoiceStateUpdated(m.Event));
                // Names and avatars of people already in the channel often arrive later in member chunks.
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, GuildMembersChunkEventArgs>(this, static (r, m) => r.OnGuildMembersChunked(m.Event));
                // Green "speaking" ring for people in our current call.
                WeakReferenceMessenger.Default.Register<ChannelListViewModel, VoiceSpeakingMessage>(this, static (r, m) => r.OnSpeaking(m));
            }
        }

        /// <summary>People currently connected to this voice or stage channel; null for other channel types.</summary>
        public ObservableCollection<VoiceMemberViewModel> VoiceMembers { get; }

        public bool HasVoiceMembers
        {
            get => _hasVoiceMembers;
            private set => OnPropertySet(ref _hasVoiceMembers, value);
        }

        private static bool IsVoiceLike(DiscordChannel channel)
            => channel.Type == ChannelType.Voice
            || channel.Type == ChannelType.Stage
            || channel.Type == ChannelType.Private
            || channel.Type == ChannelType.Group;

        private void OnVoiceStateUpdated(VoiceStateUpdateEventArgs e)
        {
            // A move between channels raises one event whose Before and After point at different channels.
            if (e.Before?.ChannelId == Id || e.Before?.Channel?.Id == Id ||
                e.After?.ChannelId == Id || e.After?.Channel?.Id == Id)
            {
                RefreshVoiceMembers();
            }
        }

        private void OnSpeaking(VoiceSpeakingMessage m)
        {
            if (!HasVoiceMembers)
                return;

            syncContext.Post(_ =>
            {
                foreach (var member in VoiceMembers)
                {
                    if (member.UserId == m.UserId)
                        member.IsSpeaking = m.Speaking;
                }
            }, null);
        }

        private void OnGuildMembersChunked(GuildMembersChunkEventArgs e)
        {
            if (HasVoiceMembers && e.Guild?.Id == Channel?.GuildId)
                RefreshVoiceMembers();
        }

        private void RefreshVoiceMembers()
        {
            var channel = Channel;
            var guild = channel?.Guild;
            if (guild != null)
            {
                var states = guild.VoiceStates.Values
                    .Where(v => v.Channel?.Id == Id && v.User != null)
                    .ToArray();

                // ObservableCollection must be changed on the UI thread; gateway events arrive on a worker thread.
                syncContext.Post(_ =>
                {
                    var members = states
                        .Select(v => new VoiceMemberViewModel(v, this))
                        .OrderBy(m => m.DisplayName, System.StringComparer.CurrentCultureIgnoreCase)
                        .ToList();

                    VoiceMembers.Clear();
                    foreach (var member in members)
                        VoiceMembers.Add(member);

                    HasVoiceMembers = VoiceMembers.Count > 0;
                }, null);
                return;
            }

            // Direct / Group DM voice call: only show participants who are actually connected in this call
            var dmStates = discord?.PrivateVoiceStates?.Values
                .Where(v => (v.ChannelId == Id || v.Channel?.Id == Id) && v.User != null)
                .ToList() ?? new System.Collections.Generic.List<DiscordVoiceState>();

            syncContext.Post(_ =>
            {
                var members = dmStates
                    .Select(v => new VoiceMemberViewModel(v, this, null, channel))
                    .ToList();

                var me = discord?.CurrentUser;
                if (me != null && !members.Any(m => m.UserId == me.Id))
                {
                    members.Add(new VoiceMemberViewModel(me, channel, false, false, this));
                }

                var ordered = members
                    .OrderBy(m => m.DisplayName, System.StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                VoiceMembers.Clear();
                foreach (var member in ordered)
                    VoiceMembers.Add(member);

                HasVoiceMembers = VoiceMembers.Count > 0;
            }, null);
        }
    }
}
