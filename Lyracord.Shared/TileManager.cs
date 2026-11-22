using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using Unicord.Universal.Extensions;
using Windows.UI.Notifications;

namespace Unicord.Universal.Shared
{
    internal class TileManager
    {
        private readonly DiscordClient _discord = null;
        private readonly TileUpdater _tileUpdater = null;
        private readonly List<DiscordMessage> _currentUnreads = null;
        private readonly SemaphoreSlim _semaphore;

        public TileManager(DiscordClient client)
        {
            _discord = client;
            _tileUpdater = TileUpdateManager.CreateTileUpdaterForApplication("App");
            _currentUnreads = new List<DiscordMessage>();
            _semaphore = new SemaphoreSlim(1, 1);
        }

        public async Task InitialiseAsync()
        {
            await _semaphore.WaitAsync();

            try
            {
                await foreach (var msg in this.FetchUnreadMessages())
                    _currentUnreads.Add(msg);

                Update();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task HandleMessageAsync(DiscordMessage message)
        {
            await _semaphore.WaitAsync();

            try
            {
                _currentUnreads.RemoveAll(m => m.Channel == message.Channel);
                _currentUnreads.Insert(0, message);

                Update();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task HandleAcknowledgeAsync(DiscordChannel channel)
        {
            await _semaphore.WaitAsync();

            try
            {
                _currentUnreads.RemoveAll(m => m.Channel == channel);

                Update();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private void Update()
        {
            // the queue cycles friends (People-hub style) and the latest unread messages
            _tileUpdater.EnableNotificationQueue(true);
            _tileUpdater.Clear();

            TileNotification friends = null;
            try
            {
                friends = FriendsTile.CreateFromSaved(); // avatars the app downloaded (null when turned off)
                if (friends != null)
                    _tileUpdater.Update(friends);
            }
            catch (Exception)
            {
                friends = null; // never let the friends tile block the unread messages
            }

            var i = 0;
            foreach (var message in _currentUnreads.OrderByDescending(d => d.CreationTimestamp).Take(friends != null ? 4 : 5))
            {
                var tileContent = NotificationUtils.CreateTileNotificationForMessage(_discord, message);
                tileContent.Tag = "msg" + i++;
                _tileUpdater.Update(tileContent);
            }
        }

        private async IAsyncEnumerable<DiscordMessage> FetchUnreadMessages()
        {
            var count = 0;
            foreach (var (_, item) in _discord.PrivateChannels)
            {
                if (!item.IsUnread())
                    continue;

                count++;
                if (count > 5)
                    break;

                var messages = await item.GetMessagesAroundAsync(item.ReadState.LastMessageId, 5);
                foreach (var msg in messages)
                    yield return msg;
            }

            var mentions = await _discord.GetMentionsAsync(5);
            foreach (var mention in mentions)
                yield return mention;
        }
    }
}
