using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DSharpPlus;
using Unicord.Universal.Shared;
using Windows.Storage;
using Windows.UI.Notifications;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Downloads friends' avatars into LocalState\friendtile and puts the People-hub style notification into the
    /// Start tile's queue. It carries its own tag, so it only replaces the previous friends tile and leaves
    /// unread-message tiles alone. The file list is saved so the tile manager can rebuild the same tile.
    /// </summary>
    internal static class FriendsTileService
    {
        private static int _running;

        public static void Push() => _ = PushAsync();

        public static async Task PushAsync()
        {
            if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1)
                return;
            try
            {
                if (!App.LocalSettings.Read(FriendsTile.EnabledSetting, true))
                    return;

                var friends = FriendsTile.SelectFriends(DiscordManager.Discord, FriendsTile.LargePeople);
                var total = DiscordManager.Discord?.Relationships?.Count ?? 0;
                if (friends.Count == 0)
                {
                    VoiceDiagnostics.Log($"Живая плитка: друзей нет (связей {total})");
                    return;
                }

                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FriendsTile.FolderName, CreationCollisionOption.OpenIfExists);
                var names = new List<string>();
                var failed = 0;
                using (var http = new Windows.Web.Http.HttpClient())
                {
                    foreach (var user in friends)
                    {
                        // the hash in the name: a new avatar is a new file, unchanged ones are not downloaded again
                        var name = $"{user.Id}_{(string.IsNullOrEmpty(user.AvatarHash) ? "default" : user.AvatarHash)}.png";
                        try
                        {
                            if (await folder.TryGetItemAsync(name) == null)
                            {
                                var buffer = await http.GetBufferAsync(new Uri(user.GetAvatarUrl(ImageFormat.Png, 128)));
                                var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                                await FileIO.WriteBufferAsync(file, buffer);
                            }
                            names.Add(name);
                        }
                        catch (Exception)
                        {
                            failed++;
                        }
                    }
                }

                // drop avatars that are no longer used
                foreach (var file in await folder.GetFilesAsync())
                {
                    if (!names.Contains(file.Name))
                    {
                        try { await file.DeleteAsync(); } catch (Exception) { }
                    }
                }

                if (names.Count == 0)
                {
                    VoiceDiagnostics.Log($"Живая плитка: ни одна аватарка не скачалась ({failed} ошибок)");
                    return;
                }

                App.LocalSettings.Save(FriendsTile.SavedImagesSetting, string.Join("|", names));
                var notification = FriendsTile.Create(names);
                var updater = TileUpdateManager.CreateTileUpdaterForApplication("App");
                updater.EnableNotificationQueue(true);
                updater.Update(notification);
                VoiceDiagnostics.Log($"Живая плитка: друзей {names.Count} из {total} связей, ошибок скачивания {failed}, " +
                                     $"XML {notification.Content.GetXml().Length} символов");
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Живая плитка", ex);
            }
            finally
            {
                _running = 0;
            }
        }
    }
}
