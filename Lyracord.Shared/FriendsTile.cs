using System;
using System.Collections.Generic;
using System.Linq;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Enums;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace Unicord.Universal.Shared
{
    /// <summary>
    /// Start tile like the People hub: friends' avatars rolling around as circles (the system "people" tile
    /// presentation, Medium/Wide/Large, Desktop and Mobile). Friends who are online come first.
    /// The app downloads the avatars into LocalState\friendtile first: with remote images the tile system drops
    /// the whole notification as soon as one picture fails, so the friends never showed up.
    /// </summary>
    internal static class FriendsTile
    {
        public const string Tag = "friends";
        public const string EnabledSetting = "FriendsLiveTile";
        public const string FolderName = "friendtile";
        public const string SavedImagesSetting = "FriendsTileImages";

        // Medium 9, Wide 12, Large 16 is all the template shows; the notification must stay under 5 KB.
        public const int MediumPeople = 9, WidePeople = 12, LargePeople = 16;
        private const string LocalBase = "ms-appdata:///local/" + FolderName + "/";

        /// <summary>Friends for the tile, online first, otherwise random.</summary>
        public static List<DiscordUser> SelectFriends(DiscordClient client, int count)
        {
            if (client?.Relationships == null)
                return new List<DiscordUser>();

            var random = new Random();
            return client.Relationships.Values
                .Where(r => r.RelationshipType == DiscordRelationshipType.Friend)
                .Select(r => r.User)
                .Where(u => u != null)
                .Select(u => new { User = u, Online = IsOnline(client, u.Id), Order = random.Next() })
                .OrderByDescending(p => p.Online)
                .ThenBy(p => p.Order)
                .Take(count)
                .Select(p => p.User)
                .ToList();
        }

        /// <summary>Tile notification for already downloaded avatars (file names inside LocalState\friendtile).</summary>
        public static TileNotification Create(IList<string> fileNames)
        {
            if (fileNames == null || fileNames.Count == 0)
                return null;

            var tile = new TileContent
            {
                Visual = new TileVisual
                {
                    Branding = TileBranding.Name,
                    BaseUri = new Uri(LocalBase),
                    TileMedium = new TileBinding { Content = People(fileNames, MediumPeople) },
                    TileWide = new TileBinding { Content = People(fileNames, WidePeople) },
                    TileLarge = new TileBinding { Content = People(fileNames, LargePeople) },
                }
            };

            var doc = new XmlDocument();
            doc.LoadXml(tile.GetContent());
            return new TileNotification(doc) { Tag = Tag };
        }

        /// <summary>The tile from the avatars the app downloaded last time (used by the tile manager).</summary>
        public static TileNotification CreateFromSaved()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (values.TryGetValue(EnabledSetting, out var enabled) && enabled is bool on && !on)
                    return null;
                if (!values.TryGetValue(SavedImagesSetting, out var saved) || !(saved is string list) || list.Length == 0)
                    return null;
                return Create(list.Split('|'));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static TileBindingContentPeople People(IList<string> fileNames, int count)
        {
            var content = new TileBindingContentPeople();
            foreach (var name in fileNames.Take(count))
                content.Images.Add(new TileBasicImage { Source = name });
            return content;
        }

        private static bool IsOnline(DiscordClient client, ulong id)
        {
            if (client.Presences == null || !client.Presences.TryGetValue(id, out var presence) || presence == null)
                return false;
            return presence.Status is UserStatus.Online or UserStatus.Idle or UserStatus.DoNotDisturb;
        }
    }
}
