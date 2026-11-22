using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Unicord.Universal.Models.User;

namespace Unicord.Universal.Services
{
    public sealed class RecentActivityItem
    {
        public ulong AuthorId { get; set; }
        public string ApplicationId { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string ExtraType { get; set; }
        public string IconUrl { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        public int? DurationSeconds { get; set; }
        public int? StreakDays { get; set; }
    }

    /// <summary>
    /// Fetches and caches Discord's Content Inventory feed (GET /content-inventory/users/@me),
    /// which backs "Recently Played / Недавно играл в…" entries for friends and the current user.
    /// </summary>
    internal static class ContentInventoryService
    {
        private static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(5);
        private static readonly object _lock = new object();

        private static DateTime _lastFetchUtc = DateTime.MinValue;
        private static Dictionary<ulong, List<RecentActivityItem>> _byUser;
        private static Task<Dictionary<ulong, List<RecentActivityItem>>> _inFlight;
        private static bool _loggedShape;

        public static async Task<IReadOnlyList<RecentActivityItem>> GetForUserAsync(ulong userId)
        {
            var map = await GetAllAsync().ConfigureAwait(false);
            if (map != null && map.TryGetValue(userId, out var list) && list.Count > 0)
                return list;
            return Array.Empty<RecentActivityItem>();
        }

        private static Task<Dictionary<ulong, List<RecentActivityItem>>> GetAllAsync()
        {
            lock (_lock)
            {
                if (_byUser != null && DateTime.UtcNow - _lastFetchUtc < CacheTime)
                    return Task.FromResult(_byUser);
                if (_inFlight != null)
                    return _inFlight;
                _inFlight = FetchAsync();
                return _inFlight;
            }
        }

        private static async Task<Dictionary<ulong, List<RecentActivityItem>>> FetchAsync()
        {
            try
            {
                var discord = DiscordManager.Discord;
                if (discord == null)
                    return _byUser ?? new Dictionary<ulong, List<RecentActivityItem>>();

                var json = await discord.GetContentInventoryRawAsync().ConfigureAwait(false);
                var root = JObject.Parse(json);
                var entries = root["entries"] as JArray
                    ?? (root["data"] as JObject)?["entries"] as JArray;

                var result = new Dictionary<ulong, List<RecentActivityItem>>();
                var allItems = new List<RecentActivityItem>();

                if (entries != null)
                {
                    if (!_loggedShape)
                    {
                        _loggedShape = true;
                        LogShape(root, entries);
                    }

                    foreach (var token in entries)
                    {
                        var item = ParseEntry(token as JObject);
                        if (item == null)
                            continue;
                        if (!result.TryGetValue(item.AuthorId, out var list))
                        {
                            list = new List<RecentActivityItem>();
                            result[item.AuthorId] = list;
                        }
                        // deduplicate same game/title per user (keep newest)
                        var dup = list.FirstOrDefault(x =>
                            (!string.IsNullOrEmpty(item.ApplicationId) && x.ApplicationId == item.ApplicationId) ||
                            (!string.IsNullOrEmpty(item.Title) && string.Equals(x.Title, item.Title, StringComparison.OrdinalIgnoreCase)));
                        if (dup != null)
                        {
                            if ((item.Timestamp ?? DateTimeOffset.MinValue) > (dup.Timestamp ?? DateTimeOffset.MinValue))
                            {
                                list.Remove(dup);
                                list.Add(item);
                                allItems.Add(item);
                            }
                        }
                        else
                        {
                            list.Add(item);
                            allItems.Add(item);
                        }
                    }

                    // resolve missing icons and titles via /applications/public
                    var appIds = allItems
                        .Where(i => !string.IsNullOrEmpty(i.ApplicationId))
                        .Select(i => i.ApplicationId)
                        .Distinct(StringComparer.Ordinal)
                        .ToList();
                    if (appIds.Count > 0)
                    {
                        var appMap = await UserProfileInfo.ResolveApplicationsAsync(appIds).ConfigureAwait(false);
                        foreach (var item in allItems)
                        {
                            if (!string.IsNullOrEmpty(item.ApplicationId) && appMap.TryGetValue(item.ApplicationId, out var meta))
                            {
                                if (string.IsNullOrWhiteSpace(item.Title))
                                    item.Title = meta.Name;
                                if (string.IsNullOrWhiteSpace(item.IconUrl))
                                    item.IconUrl = meta.IconUrl;
                            }
                        }
                    }

                    foreach (var kv in result)
                    {
                        kv.Value.RemoveAll(x => string.IsNullOrWhiteSpace(x.Title));
                        kv.Value.Sort((a, b) => (b.Timestamp ?? DateTimeOffset.MinValue).CompareTo(a.Timestamp ?? DateTimeOffset.MinValue));
                        if (kv.Value.Count > 6)
                            kv.Value.RemoveRange(6, kv.Value.Count - 6);
                    }
                }
                else if (!_loggedShape)
                {
                    _loggedShape = true;
                    LogShape(root, null);
                }

                lock (_lock)
                {
                    _byUser = result;
                    _lastFetchUtc = DateTime.UtcNow;
                }
                return result;
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.Log("Недавняя активность (content-inventory)", ex);
                lock (_lock)
                {
                    // short backoff on failure so we don't spam the endpoint on every profile open
                    if (_byUser == null)
                        _byUser = new Dictionary<ulong, List<RecentActivityItem>>();
                    _lastFetchUtc = DateTime.UtcNow - CacheTime + TimeSpan.FromMinutes(1);
                    return _byUser;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _inFlight = null;
                }
            }
        }

        private static RecentActivityItem ParseEntry(JObject entry)
        {
            if (entry == null)
                return null;

            var authorToken = entry["author_id"];
            if (authorToken == null || !ulong.TryParse(authorToken.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var authorId))
                return null;

            var extra = entry["extra"] as JObject;
            var extraType = Str(extra, "type") ?? "";

            // Handle nested content_inventory_entry shape if present
            if (extra == null && entry["content_inventory_entry"] is JObject nested)
            {
                extra = (nested["entries"] as JArray)?.FirstOrDefault() as JObject ?? nested;
                extraType = Str(nested, "type") ?? Str(extra, "type") ?? "";
            }

            var appId = extra?["application_id"]?.ToString();
            var title = NonEmpty(Str(extra, "game_name"))
                ?? NonEmpty(Str(extra, "application_name"))
                ?? NonEmpty(Str(extra, "media_title"))
                ?? NonEmpty(Str(extra, "activity_name"))
                ?? NonEmpty(Str(extra, "title"))
                ?? NonEmpty(Str(extra, "name"));

            // Spotify / media entries sometimes put entries[] inside extra.entries
            if (title == null && extra?["entries"] is JArray mediaEntries && mediaEntries.Count > 0 && mediaEntries[0] is JObject firstMedia)
            {
                var media = firstMedia["media"] as JObject ?? firstMedia;
                title = NonEmpty(Str(media, "title")) ?? NonEmpty(Str(media, "name"));
                if (string.IsNullOrEmpty(appId))
                    appId = media["application_id"]?.ToString();
            }

            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(appId))
                return null;

            var subtitle = NonEmpty(Str(extra, "artist"))
                ?? NonEmpty(Str(extra, "details"))
                ?? NonEmpty(Str(extra, "media_subtitle"));

            var timestamp = ParseDate(Str(entry, "ended_at"))
                ?? ParseDate(Str(entry, "started_at"))
                ?? ParseDate(Str(entry, "created_at"));

            int? durationSeconds = null;
            int? streakDays = null;
            if (entry["traits"] is JArray traits)
            {
                foreach (var t in traits)
                {
                    var tObj = t as JObject;
                    if (tObj == null) continue;
                    if (tObj["duration_seconds"] != null && int.TryParse(tObj["duration_seconds"].ToString(), out var dur) && dur > 0)
                        durationSeconds = dur;
                    if (tObj["streak_days"] != null && int.TryParse(tObj["streak_days"].ToString(), out var streak) && streak > 1)
                        streakDays = streak;
                }
            }

            return new RecentActivityItem
            {
                AuthorId = authorId,
                ApplicationId = NonEmpty(appId),
                Title = title,
                Subtitle = subtitle,
                ExtraType = extraType,
                Timestamp = timestamp,
                DurationSeconds = durationSeconds,
                StreakDays = streakDays
            };
        }

        private static void LogShape(JObject root, JArray entries)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("Недавняя активность (content-inventory): ");
                sb.Append("корневые ключи {").Append(string.Join(",", root.Properties().Select(p => p.Name))).Append("}, ");
                sb.Append("записей ").Append(entries?.Count ?? 0);
                if (entries != null && entries.Count > 0 && entries[0] is JObject first)
                {
                    sb.Append("; первый элемент {").Append(string.Join(",", first.Properties().Select(p => p.Name))).Append("}");
                    if (first["extra"] is JObject extra)
                        sb.Append(", extra {").Append(string.Join(",", extra.Properties().Select(p => p.Name))).Append("}");
                }
                VoiceDiagnostics.Log(sb.ToString());
            }
            catch (Exception) { }
        }

        private static string Str(JObject o, string name)
            => o != null && o[name] != null && o[name].Type == JTokenType.String ? (string)o[name] : null;

        private static string NonEmpty(string s)
            => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static DateTimeOffset? ParseDate(string s)
            => !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
                ? dt
                : (DateTimeOffset?)null;
    }
}
