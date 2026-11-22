using System;
using System.Collections.Generic;
using System.Globalization;
using DSharpPlus.Entities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DSharpPlus.Net.Serialization
{
    /// <summary>
    /// Fills the most numerous Discord objects (guild channels, their permission overwrites, roles) field by field,
    /// without the serializer's reflection. On the phone (.NET Native) creating objects through reflection was ~90% of
    /// the time spent parsing READY (thousands of channels and roles), while reading the JSON itself is cheap.
    /// Only the fields declared with [JsonProperty] on these types are read, the same ones the serializer sets.
    /// </summary>
    internal static class FastEntityReader
    {
        /// <summary>Switch to compare against the general serializer (tests).</summary>
        internal static bool Enabled = true;

        // ------------------------------------------------------------------ channels (from the JObject the channel converter already loads)

        /// <summary>Channel types read here: plain guild channels. Forums, threads and DMs keep the general path.</summary>
        internal static bool HandlesChannelType(ChannelType type)
            => type is ChannelType.Text or ChannelType.Voice or ChannelType.Category or ChannelType.Announcement
                or ChannelType.Store or ChannelType.Stage or ChannelType.Directory;

        internal static DiscordChannel ReadChannel(JObject job)
        {
            var channel = new DiscordChannel();
            foreach (var property in job.Properties())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "id": if (Snowflake(value) is ulong id) channel.Id = id; break;
                    case "guild_id": if (!IsNull(value)) channel.GuildId = Snowflake(value); break;
                    case "parent_id": channel.ParentId = Snowflake(value); break; // NullValueHandling.Include
                    case "name": if (!IsNull(value)) channel.Name = (string)value; break;
                    case "type": if (!IsNull(value)) channel.Type = (ChannelType)Int(value); break;
                    case "position": if (!IsNull(value)) channel.Position = Int(value); break;
                    case "permission_overwrites":
                        if (value is JArray overwrites)
                        {
                            var list = new List<DiscordOverwrite>(overwrites.Count);
                            foreach (var item in overwrites)
                            {
                                if (item is JObject overwrite)
                                    list.Add(ReadOverwrite(overwrite));
                            }
                            channel._permissionOverwrites = list;
                        }
                        break;
                    case "topic": if (!IsNull(value)) channel.Topic = (string)value; break;
                    case "last_message_id": if (!IsNull(value)) channel.LastMessageId = Snowflake(value); break;
                    case "bitrate": if (!IsNull(value)) channel.Bitrate = Int(value); break;
                    case "user_limit": if (!IsNull(value)) channel.UserLimit = Int(value); break;
                    case "rate_limit_per_user": channel.PerUserRateLimit = IsNull(value) ? (int?)null : Int(value); break;
                    case "video_quality_mode": if (!IsNull(value)) channel.QualityMode = (VideoQualityMode)Int(value); break;
                    case "last_pin_timestamp": if (!IsNull(value)) channel.LastPinTimestamp = Timestamp(value); break;
                    case "nsfw": channel.IsNSFW = !IsNull(value) && (bool)value; break;
                    case "rtc_region": if (!IsNull(value)) channel.RtcRegionId = (string)value; break;
                    case "permissions": channel.UserPermissions = IsNull(value) ? Permissions.None : PermissionsOf(value); break;
                }
            }
            return channel;
        }

        private static DiscordOverwrite ReadOverwrite(JObject job)
        {
            var overwrite = new DiscordOverwrite();
            foreach (var property in job.Properties())
            {
                var value = property.Value;
                switch (property.Name)
                {
                    case "id": if (Snowflake(value) is ulong id) overwrite.Id = id; break;
                    case "type":
                        if (IsNull(value)) break;
                        if (value.Type == JTokenType.String)
                            overwrite.Type = string.Equals((string)value, "member", StringComparison.OrdinalIgnoreCase) ? OverwriteType.Member : OverwriteType.Role;
                        else
                            overwrite.Type = (OverwriteType)Int(value);
                        break;
                    case "allow": if (!IsNull(value)) overwrite.Allowed = PermissionsOf(value); break;
                    case "deny": if (!IsNull(value)) overwrite.Denied = PermissionsOf(value); break;
                }
            }
            return overwrite;
        }

        // ------------------------------------------------------------------ roles (streamed straight from the reader)

        internal static DiscordRole ReadRole(JsonReader reader, JsonSerializer serializer)
        {
            var role = new DiscordRole();
            while (reader.Read() && reader.TokenType == JsonToken.PropertyName)
            {
                var name = (string)reader.Value;
                if (!reader.Read())
                    break;
                var isNull = reader.TokenType is JsonToken.Null or JsonToken.Undefined;
                switch (name)
                {
                    case "id": if (!isNull) role.Id = ReaderSnowflake(reader); break;
                    case "name": if (!isNull) role.Name = Convert.ToString(reader.Value, CultureInfo.InvariantCulture); break;
                    case "color": if (!isNull) role._color = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); break;
                    case "hoist": if (!isNull) role.IsHoisted = Convert.ToBoolean(reader.Value, CultureInfo.InvariantCulture); break;
                    case "icon": role.IconHash = isNull ? null : Convert.ToString(reader.Value, CultureInfo.InvariantCulture); break;
                    case "unicode_emoji": role._emoji = isNull ? null : Convert.ToString(reader.Value, CultureInfo.InvariantCulture); break;
                    case "position": if (!isNull) role.Position = Convert.ToInt32(reader.Value, CultureInfo.InvariantCulture); break;
                    case "permissions":
                        if (!isNull)
                            role.Permissions = reader.TokenType == JsonToken.String
                                ? DiscordPermissionsJsonConverter.Parse((string)reader.Value)
                                : (Permissions)unchecked((ulong)Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture));
                        break;
                    case "managed": if (!isNull) role.IsManaged = Convert.ToBoolean(reader.Value, CultureInfo.InvariantCulture); break;
                    case "mentionable": if (!isNull) role.IsMentionable = Convert.ToBoolean(reader.Value, CultureInfo.InvariantCulture); break;
                    case "tags":
                        if (!isNull) role.Tags = serializer.Deserialize<DiscordRoleTags>(reader);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return role;
        }

        // ------------------------------------------------------------------ values

        private static bool IsNull(JToken token)
            => token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined;

        private static ulong? Snowflake(JToken token)
        {
            if (IsNull(token))
                return null;
            if (token.Type == JTokenType.Integer)
                return unchecked((ulong)(long)token);
            return ulong.TryParse((string)token, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (ulong?)null;
        }

        private static ulong ReaderSnowflake(JsonReader reader)
        {
            if (reader.TokenType == JsonToken.Integer)
                return unchecked((ulong)Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture));
            return ulong.TryParse(Convert.ToString(reader.Value, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        private static int Int(JToken token)
            => token.Type == JTokenType.String
                ? int.Parse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture)
                : (int)token;

        private static Permissions PermissionsOf(JToken token)
            => token.Type == JTokenType.String
                ? DiscordPermissionsJsonConverter.Parse((string)token)
                : (Permissions)unchecked((ulong)(long)token);

        private static DateTimeOffset? Timestamp(JToken token)
        {
            if (token.Type == JTokenType.Date)
            {
                var value = ((JValue)token).Value;
                if (value is DateTimeOffset offset) return offset;
                if (value is DateTime date) return new DateTimeOffset(date);
            }
            return DateTimeOffset.TryParse((string)token, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : (DateTimeOffset?)null;
        }
    }

    /// <summary>Reads roles with <see cref="FastEntityReader"/>; writing goes through the normal serializer.</summary>
    internal sealed class DiscordRoleJsonConverter : JsonConverter
    {
        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType) => objectType == typeof(DiscordRole);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            if (!FastEntityReader.Enabled || existingValue != null)
            {
                // populate an existing role / comparison runs: the general way
                var role = existingValue as DiscordRole ?? new DiscordRole();
                serializer.Populate(reader, role);
                return role;
            }

            return FastEntityReader.ReadRole(reader, serializer);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => throw new NotSupportedException();
    }
}
