using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unicord.Universal.Services;
using Windows.UI;

namespace Unicord.Universal.Models.User
{
    /// <summary>One profile badge (Nitro, server boost, HypeSquad, early supporter, ...).</summary>
    public sealed class ProfileBadge
    {
        public string Description { get; set; }
        public Uri Icon { get; set; }
    }

    /// <summary>One linked external account from connected_accounts (Steam, Spotify, YouTube, Xbox, ...).</summary>
    public sealed class ProfileConnection
    {
        public string Type { get; set; }
        public string Platform { get; set; }
        public string Name { get; set; }
        public bool Verified { get; set; }
    }

    /// <summary>One game entry inside a profile widget (current_games, favorite_games, played_games, want_to_play_games).</summary>
    public sealed class ProfileGameItem
    {
        public string GameId { get; set; }
        public string Name { get; set; }
        public string IconUrl { get; set; }
        public string Comment { get; set; }
    }

    /// <summary>One game widget on a Discord profile.</summary>
    public sealed class ProfileGameWidget
    {
        public string WidgetType { get; set; }
        public List<ProfileGameItem> Games { get; } = new List<ProfileGameItem>();
    }

    /// <summary>
    /// What the official client shows in a profile popout: banner (or accent colour), Nitro profile theme colours,
    /// bio, pronouns, badges, connected accounts and game widgets. Loaded from GET /users/{id}/profile and cached for a few minutes.
    /// </summary>
    public sealed class UserProfileInfo
    {
        private const string Cdn = "https://cdn.discordapp.com/";
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<string, (DateTime Time, UserProfileInfo Profile)> _cache
            = new ConcurrentDictionary<string, (DateTime, UserProfileInfo)>();
        private static readonly ConcurrentDictionary<string, (string Name, string IconUrl)> _appCache
            = new ConcurrentDictionary<string, (string Name, string IconUrl)>(StringComparer.Ordinal);

        public Uri Banner { get; private set; }
        public Color? AccentColor { get; private set; }
        public Color? ThemePrimary { get; private set; }
        public Color? ThemeAccent { get; private set; }
        public string Bio { get; private set; }
        public string Pronouns { get; private set; }
        public DateTimeOffset? PremiumSince { get; private set; }
        public List<ProfileBadge> Badges { get; } = new List<ProfileBadge>();
        public List<ProfileConnection> ConnectedAccounts { get; } = new List<ProfileConnection>();
        public List<ProfileGameWidget> GameWidgets { get; } = new List<ProfileGameWidget>();
        public string GuildTag { get; private set; }
        public Uri GuildTagBadge { get; private set; }

        /// <summary>premium_type: 0 none, 1 Nitro Classic, 2 Nitro, 3 Nitro Basic. Banners need 1 or 2.</summary>
        public int PremiumType { get; private set; }

        /// <summary>Drops the cached profile so the next load shows an edit straight away.</summary>
        public static void Invalidate(ulong userId)
        {
            foreach (var key in _cache.Keys)
            {
                if (key.StartsWith(userId + ":", StringComparison.Ordinal))
                    _cache.TryRemove(key, out _);
            }
        }

        public static async Task<UserProfileInfo> LoadAsync(ulong userId, ulong? guildId)
        {
            var key = $"{userId}:{guildId}";
            if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Time < CacheTime)
                return cached.Profile;

            var json = await DiscordManager.Discord.GetUserProfileRawAsync(userId, guildId).ConfigureAwait(false);
            var root = JObject.Parse(json);
            var profile = Parse(userId, root);

            // resolve game names & icons for profile game widgets in one batched call
            if (profile.GameWidgets.Count > 0)
            {
                try
                {
                    var allGames = profile.GameWidgets.SelectMany(w => w.Games).ToList();
                    var appMap = await ResolveApplicationsAsync(allGames.Select(g => g.GameId)).ConfigureAwait(false);
                    foreach (var g in allGames)
                    {
                        if (appMap.TryGetValue(g.GameId, out var meta))
                        {
                            g.Name = meta.Name;
                            g.IconUrl = meta.IconUrl;
                        }
                    }
                    // drop widgets where no game resolved to a name
                    foreach (var w in profile.GameWidgets)
                        w.Games.RemoveAll(g => string.IsNullOrWhiteSpace(g.Name));
                    profile.GameWidgets.RemoveAll(w => w.Games.Count == 0);
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Профиль (виджеты игр)", ex);
                }
            }

            // own profile: write the response's shape (no values) to the log, to see where new badges are
            try
            {
                if (DiscordManager.Discord?.CurrentUser?.Id == userId)
                    VoiceDiagnostics.Log($"Профиль (свой): найдено значков {profile.Badges.Count}; структура ответа: {Describe(root)}");
            }
            catch (Exception) { }
            _cache[key] = (DateTime.UtcNow, profile);
            return profile;
        }

        /// <summary>Resolves Discord application / game IDs to (Name, IconUrl) via GET /applications/public, with in-memory session caching.</summary>
        public static async Task<Dictionary<string, (string Name, string IconUrl)>> ResolveApplicationsAsync(IEnumerable<string> appIds)
        {
            var result = new Dictionary<string, (string Name, string IconUrl)>(StringComparer.Ordinal);
            if (appIds == null)
                return result;

            var missing = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in appIds)
            {
                var id = NonEmpty(raw);
                if (id == null || !seen.Add(id))
                    continue;
                if (_appCache.TryGetValue(id, out var cached))
                    result[id] = cached;
                else
                    missing.Add(id);
            }

            var discord = DiscordManager.Discord;
            if (discord == null || missing.Count == 0)
                return result;

            const int batchSize = 20;
            for (var i = 0; i < missing.Count; i += batchSize)
            {
                var batch = missing.Skip(i).Take(batchSize).ToList();
                try
                {
                    var rawJson = await discord.GetPublicApplicationsRawAsync(batch).ConfigureAwait(false);
                    var arr = JArray.Parse(rawJson);
                    foreach (var item in arr)
                    {
                        var obj = item as JObject;
                        var id = obj?["id"]?.ToString();
                        var name = NonEmpty(Str(obj, "name"));
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                            continue;
                        var iconHash = NonEmpty(Str(obj, "icon"));
                        var iconUrl = iconHash != null
                            ? $"{Cdn}app-icons/{id}/{iconHash}.png?size=64"
                            : null;
                        var entry = (name, iconUrl);
                        _appCache[id] = entry;
                        result[id] = entry;
                    }
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Разрешение игр (/applications/public)", ex);
                }
            }

            return result;
        }

        internal static UserProfileInfo Parse(ulong userId, JObject root)
        {
            var info = new UserProfileInfo();
            var user = root["user"] as JObject;
            var profile = root["user_profile"] as JObject;
            var member = root["guild_member_profile"] as JObject;

            // a server profile overrides the global one where it has its own values
            var memberBanner = Str(member, "banner");
            var memberGuild = Str(member, "guild_id");
            if (memberBanner != null && memberGuild != null)
            {
                info.Banner = new Uri($"{Cdn}guilds/{memberGuild}/users/{userId}/banners/{memberBanner}.{Ext(memberBanner)}?size=600");
            }
            else if ((Str(profile, "banner") ?? Str(user, "banner")) is string banner)
            {
                info.Banner = new Uri($"{Cdn}banners/{userId}/{banner}.{Ext(banner)}?size=600");
            }

            info.AccentColor = ColorFrom(member?["accent_color"]) ?? ColorFrom(profile?["accent_color"]) ?? ColorFrom(user?["accent_color"]);

            if ((member?["theme_colors"] ?? profile?["theme_colors"]) is JArray theme && theme.Count >= 2)
            {
                info.ThemePrimary = ColorFrom(theme[0]);
                info.ThemeAccent = ColorFrom(theme[1]);
            }

            info.Bio = NonEmpty(Str(member, "bio")) ?? NonEmpty(Str(profile, "bio")) ?? NonEmpty(Str(user, "bio"));
            info.Pronouns = NonEmpty(Str(member, "pronouns")) ?? NonEmpty(Str(profile, "pronouns"));

            if (Str(root, "premium_since") is string since &&
                DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                info.PremiumSince = date;

            if (root["premium_type"] != null && root["premium_type"].Type == JTokenType.Integer)
                info.PremiumType = (int)root["premium_type"];
            else if (user?["premium_type"] != null && user["premium_type"].Type == JTokenType.Integer)
                info.PremiumType = (int)user["premium_type"];

            var primaryGuild = user?["primary_guild"] as JObject ?? user?["clan"] as JObject;
            if (primaryGuild != null &&
                primaryGuild["identity_enabled"]?.Type == JTokenType.Boolean &&
                (bool)primaryGuild["identity_enabled"])
            {
                var tag = NonEmpty(Str(primaryGuild, "tag"));
                if (tag != null)
                {
                    info.GuildTag = tag;
                    var badgeHash = NonEmpty(Str(primaryGuild, "badge"));
                    var guildIdToken = primaryGuild["identity_guild_id"];
                    var guildIdStr = guildIdToken != null && (guildIdToken.Type == JTokenType.String || guildIdToken.Type == JTokenType.Integer)
                        ? NonEmpty(guildIdToken.ToString())
                        : null;
                    if (badgeHash != null && guildIdStr != null &&
                        Uri.TryCreate($"{Cdn}guild-tag-badges/{guildIdStr}/{badgeHash}.{Ext(badgeHash)}?size=32", UriKind.Absolute, out var tagBadgeUri))
                    {
                        info.GuildTagBadge = tagBadgeUri;
                    }
                }
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddBadges(info, root["badges"] as JArray, seen);
            AddBadges(info, root["guild_badges"] as JArray, seen);

            // Discord's newer badges (Game Time, Game Variety, Streaming, Account Age, Nov 2026) are not documented;
            // look for badge-shaped lists anywhere else in the response too.
            foreach (var array in BadgeLikeArrays(root))
                AddBadges(info, array, seen);

            if (root["connected_accounts"] is JArray accounts)
            {
                foreach (var a in accounts)
                {
                    var obj = a as JObject;
                    var type = NonEmpty(Str(obj, "type"));
                    var name = NonEmpty(Str(obj, "name"));
                    if (type == null || name == null)
                        continue;
                    info.ConnectedAccounts.Add(new ProfileConnection
                    {
                        Type = type,
                        Platform = FormatConnectionPlatform(type),
                        Name = name,
                        Verified = obj["verified"]?.Type == JTokenType.Boolean && (bool)obj["verified"]
                    });
                }
            }

            if (root["widgets"] is JArray widgets)
            {
                foreach (var w in widgets)
                {
                    var data = (w as JObject)?["data"] as JObject;
                    var wType = NonEmpty(Str(data, "type"));
                    var games = data?["games"] as JArray;
                    if (wType == null || games == null || games.Count == 0)
                        continue;
                    var widget = new ProfileGameWidget { WidgetType = wType };
                    foreach (var g in games)
                    {
                        var gObj = g as JObject;
                        var gameId = gObj?["game_id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(gameId))
                            continue;
                        widget.Games.Add(new ProfileGameItem
                        {
                            GameId = gameId,
                            Comment = NonEmpty(Str(gObj, "comment"))
                        });
                    }
                    if (widget.Games.Count > 0)
                        info.GameWidgets.Add(widget);
                }
            }

            return info;
        }

        private static string FormatConnectionPlatform(string type)
        {
            switch ((type ?? "").ToLowerInvariant())
            {
                case "steam": return "Steam";
                case "spotify": return "Spotify";
                case "youtube": return "YouTube";
                case "twitch": return "Twitch";
                case "github": return "GitHub";
                case "xbox": return "Xbox";
                case "playstation": return "PlayStation";
                case "epicgames": return "Epic Games";
                case "battlenet": return "Battle.net";
                case "riotgames": return "Riot Games";
                case "reddit": return "Reddit";
                case "twitter": return "X / Twitter";
                case "tiktok": return "TikTok";
                case "instagram": return "Instagram";
                case "facebook": return "Facebook";
                case "crunchyroll": return "Crunchyroll";
                case "roblox": return "Roblox";
                case "bluesky": return "Bluesky";
                case "domain": return "Web";
                default:
                    return string.IsNullOrEmpty(type)
                        ? ""
                        : char.ToUpperInvariant(type[0]) + type.Substring(1);
            }
        }

        /// <summary>Arrays named like badges, or whose items carry an icon and a description, outside the known fields.</summary>
        private static IEnumerable<JArray> BadgeLikeArrays(JToken token, int depth = 0)
        {
            if (depth > 6 || token == null)
                yield break;
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (depth == 0 && (property.Name == "badges" || property.Name == "guild_badges"))
                        continue;
                    if (property.Value is JArray array && LooksLikeBadges(property.Name, array))
                        yield return array;
                    else
                        foreach (var inner in BadgeLikeArrays(property.Value, depth + 1))
                            yield return inner;
                }
            }
        }

        private static bool LooksLikeBadges(string name, JArray array)
        {
            if (array.Count == 0 || !(array[0] is JObject first))
                return false;
            var hasIcon = IconOf(first) != null;
            return hasIcon && (name.IndexOf("badge", StringComparison.OrdinalIgnoreCase) >= 0 || first["description"] != null);
        }

        private static string IconOf(JObject badge)
            => Str(badge, "icon") ?? Str(badge, "icon_url") ?? Str(badge, "image_url") ?? Str(badge, "asset");

        private static void AddBadges(UserProfileInfo info, JArray badges, HashSet<string> seen)
        {
            if (badges == null)
                return;
            foreach (var b in badges)
            {
                var badge = b as JObject;
                var icon = IconOf(badge);
                if (icon == null)
                    continue;
                // a hash on the badge-icons CDN (classic badges), a path on the CDN, or a full address
                string url;
                if (icon.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || icon.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    url = icon;
                else if (icon.IndexOf('/') >= 0)
                    url = Cdn + icon.TrimStart('/');
                else
                    url = $"{Cdn}badge-icons/{icon}.png";
                if (!seen.Add(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    continue;
                info.Badges.Add(new ProfileBadge
                {
                    Description = Str(badge, "description") ?? Str(badge, "name") ?? "",
                    Icon = uri
                });
            }
        }

        /// <summary>
        /// For the report: the shape of a profile response — field names, array sizes and the keys of their items, no
        /// values — plus the id and icon of every badge. Shows where Discord puts badges this app does not know yet.
        /// </summary>
        internal static string Describe(JObject root)
        {
            var sb = new System.Text.StringBuilder();
            void Walk(JToken token, string path, int depth)
            {
                if (depth > 3 || token == null)
                    return;
                if (token is JObject obj)
                {
                    foreach (var p in obj.Properties())
                    {
                        var child = path.Length == 0 ? p.Name : path + "." + p.Name;
                        if (p.Value is JArray arr)
                        {
                            var keys = arr.Count > 0 && arr[0] is JObject o ? "{" + string.Join(",", System.Linq.Enumerable.Select(o.Properties(), x => x.Name)) + "}" : "";
                            sb.Append(child).Append('[').Append(arr.Count).Append(']').Append(keys).Append("; ");
                        }
                        else if (p.Value is JObject)
                        {
                            Walk(p.Value, child, depth + 1);
                        }
                        else
                        {
                            sb.Append(child).Append("; ");
                        }
                    }
                }
            }
            Walk(root, "", 0);

            // profile widgets (new in 2026): the newer badges may live here; ids and the shape of "data", no values
            if (root["widgets"] is JArray widgets && widgets.Count > 0)
            {
                sb.Append("\n   widgets: ");
                foreach (var w in widgets)
                {
                    var widget = w as JObject;
                    sb.Append(Str(widget, "id") ?? "?").Append(" {");
                    Shape(widget?["data"], sb, 0);
                    sb.Append("} ");
                }
            }

            foreach (var name in new[] { "badges", "guild_badges" })
            {
                if (root[name] is JArray badges && badges.Count > 0)
                {
                    sb.Append("\n   ").Append(name).Append(": ");
                    foreach (var b in badges)
                        sb.Append(Str(b as JObject, "id") ?? "?").Append('(').Append(IconOf(b as JObject) ?? "-").Append(") ");
                }
            }
            return sb.ToString();
        }

        /// <summary>Keys and nesting of a JSON value without the values (strings show as "s", numbers as "n").</summary>
        private static void Shape(JToken token, System.Text.StringBuilder sb, int depth)
        {
            if (token == null || depth > 3)
                return;
            switch (token)
            {
                case JObject obj:
                    var first = true;
                    foreach (var p in obj.Properties())
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(p.Name);
                        if (p.Value is JObject || p.Value is JArray)
                        {
                            sb.Append(p.Value is JArray ? "[" : "{");
                            Shape(p.Value, sb, depth + 1);
                            sb.Append(p.Value is JArray ? "]" : "}");
                        }
                    }
                    break;
                case JArray arr:
                    sb.Append(arr.Count);
                    if (arr.Count > 0 && (arr[0] is JObject || arr[0] is JArray))
                    {
                        sb.Append(':');
                        Shape(arr[0], sb, depth + 1);
                    }
                    break;
            }
        }

        private static string Str(JObject o, string name)
            => o != null && o[name] != null && o[name].Type == JTokenType.String ? (string)o[name] : null;

        // animated banners ("a_" hashes) are GIFs; Image plays those on Windows 10 Mobile too
        private static string Ext(string hash) => hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";

        private static string NonEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static Color? ColorFrom(JToken token)
        {
            if (token == null || token.Type != JTokenType.Integer)
                return null;
            var v = (long)token;
            return Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
    }
}
