using DSharpPlus.Entities;
using DSharpPlus.EventArgs;

namespace Unicord.Universal.Models.Guild
{
    internal class GuildListViewModel : GuildViewModel, IGuildListViewModel
    {
        private GuildListFolderViewModel _parent;
        private bool _isSelected;

        public GuildListViewModel(DiscordGuild guild, GuildListFolderViewModel parent = null) 
            : base(guild.Id)
        {
            _parent = parent;
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => OnPropertySet(ref _isSelected, value);
        }

        public int MentionCount
        {
            get
            {
                if (Muted)
                    return -1;

                var count = 0;
                try
                {
                    foreach (var channel in AccessibleChannels)
                    {
                        try
                        {
                            if (channel.Muted)
                                continue;

                            if (discord.ReadStates.TryGetValue(channel.Id, out var rs))
                                count += rs.MentionCount;
                        }
                        catch (System.InvalidOperationException)
                        {
                            // a thread we have not joined: no channel object, nothing to count
                        }
                    }
                }
                catch (System.Exception)
                {
                    // the channel list changed while counting; the next update recounts
                }

                return count == 0 ? -1 : count;
            }
        }

        public bool TryGetModelForGuild(DiscordGuild guild, out GuildListViewModel model)
        {
            if (Guild == guild)
            {
                model = this;
                return true;
            }

            model = null;
            return false;
        }

        protected override void OnReadStateUpdatedCore(ReadStateUpdateEventArgs e)
        {
            InvokePropertyChanged(nameof(MentionCount));
        }
    }
}
