using System;
using Newtonsoft.Json;

namespace DSharpPlus.Entities
{
    /// <summary>
    /// Represents a user's primary guild (server tag / clan badge).
    /// </summary>
    public sealed class DiscordPrimaryGuild : IEquatable<DiscordPrimaryGuild>
    {
        /// <summary>
        /// Gets the ID of the user's primary guild.
        /// </summary>
        [JsonProperty("identity_guild_id", NullValueHandling = NullValueHandling.Ignore)]
        public ulong? IdentityGuildId { get; internal set; }

        /// <summary>
        /// Gets whether the user is currently displaying this guild's server tag.
        /// </summary>
        [JsonProperty("identity_enabled", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IdentityEnabled { get; internal set; }

        /// <summary>
        /// Gets the short server tag text (up to 4 characters).
        /// </summary>
        [JsonProperty("tag", NullValueHandling = NullValueHandling.Ignore)]
        public string Tag { get; internal set; }

        /// <summary>
        /// Gets the guild tag badge image hash.
        /// </summary>
        [JsonProperty("badge", NullValueHandling = NullValueHandling.Ignore)]
        public string BadgeHash { get; internal set; }

        /// <summary>
        /// Gets whether this primary guild has a visible server tag.
        /// </summary>
        [JsonIgnore]
        public bool HasTag
            => this.IdentityEnabled == true && !string.IsNullOrWhiteSpace(this.Tag);

        /// <summary>
        /// Gets the CDN URL for the guild tag badge icon, or null if unavailable.
        /// </summary>
        [JsonIgnore]
        public string BadgeUrl
        {
            get
            {
                if (!this.IdentityGuildId.HasValue || string.IsNullOrWhiteSpace(this.BadgeHash))
                    return null;
                var ext = this.BadgeHash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";
                return $"https://cdn.discordapp.com/guild-tag-badges/{this.IdentityGuildId.Value}/{this.BadgeHash}.{ext}?size=32";
            }
        }

        public bool Equals(DiscordPrimaryGuild other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (other is null) return false;
            return this.IdentityGuildId == other.IdentityGuildId
                && this.IdentityEnabled == other.IdentityEnabled
                && string.Equals(this.Tag, other.Tag, StringComparison.Ordinal)
                && string.Equals(this.BadgeHash, other.BadgeHash, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
            => this.Equals(obj as DiscordPrimaryGuild);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = this.IdentityGuildId.GetHashCode();
                hash = (hash * 397) ^ this.IdentityEnabled.GetHashCode();
                hash = (hash * 397) ^ (this.Tag != null ? StringComparer.Ordinal.GetHashCode(this.Tag) : 0);
                hash = (hash * 397) ^ (this.BadgeHash != null ? StringComparer.Ordinal.GetHashCode(this.BadgeHash) : 0);
                return hash;
            }
        }
    }
}
