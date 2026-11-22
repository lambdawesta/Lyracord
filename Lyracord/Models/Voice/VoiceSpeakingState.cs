using System.Collections.Concurrent;
using System.Linq;
using CommunityToolkit.Mvvm.Messaging;

namespace Unicord.Universal.Models.Voice
{
    /// <summary>Sent when a user in our voice call starts or stops speaking (UserId 0 = everyone stopped).</summary>
    public sealed class VoiceSpeakingMessage
    {
        public VoiceSpeakingMessage(ulong userId, bool speaking)
        {
            UserId = userId;
            Speaking = speaking;
        }

        public ulong UserId { get; }
        public bool Speaking { get; }
    }

    /// <summary>
    /// Who is speaking in the current voice call, fed by the native voice client. Kept here so voice member
    /// rows that are re-created (the list is rebuilt on every voice state update) start with the right state.
    /// </summary>
    internal static class VoiceSpeakingState
    {
        private static readonly ConcurrentDictionary<ulong, bool> _speaking = new ConcurrentDictionary<ulong, bool>();

        public static bool IsSpeaking(ulong userId) => _speaking.TryGetValue(userId, out var speaking) && speaking;

        public static void Set(ulong userId, bool speaking)
        {
            if (userId == 0)
                return;

            var previous = IsSpeaking(userId);
            _speaking[userId] = speaking;
            if (previous != speaking)
                WeakReferenceMessenger.Default.Send(new VoiceSpeakingMessage(userId, speaking));
        }

        /// <summary>Parses the native "&lt;user id&gt;|1" / "&lt;user id&gt;|0" notification.</summary>
        public static ulong SetFromNative(string value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            var separator = value.IndexOf('|');
            if (separator <= 0 || !ulong.TryParse(value.Substring(0, separator), out var userId))
                return 0;

            Set(userId, value.Substring(separator + 1) == "1");
            return userId;
        }

        public static void Clear()
        {
            foreach (var userId in _speaking.Where(p => p.Value).Select(p => p.Key).ToList())
                Set(userId, false);
            _speaking.Clear();
        }
    }
}
