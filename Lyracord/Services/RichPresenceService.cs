using System;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using Windows.ApplicationModel;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Broadcasts an optional Rich Presence ("Playing Lyracord — Windows 10 Mobile") over Gateway OP 3.
    /// Controlled by LocalSettings["LyracordRichPresence"] and toggleable from Metro Settings.
    /// </summary>
    internal static class RichPresenceService
    {
        public const string SettingKey = "LyracordRichPresence";
        public const string AppIdSettingKey = "LyracordRpcAppId";
        public const ulong DefaultAppId = 1574144358930841670UL;

        private static readonly DateTimeOffset _sessionStart = DateTimeOffset.UtcNow;

        public static bool IsEnabled
        {
            get => App.LocalSettings.Read(SettingKey, true);
            set => App.LocalSettings.Save(SettingKey, value);
        }

        public static Task ApplyAsync(DiscordClient client = null)
        {
            client ??= DiscordManager.Discord;
            if (client == null)
                return Task.CompletedTask;

            return Task.Run(async () =>
            {
                try
                {
                    if (!IsEnabled)
                    {
                        await client.ClearRichPresenceAsync().ConfigureAwait(false);
                        VoiceDiagnostics.Log("Rich Presence: отключён");
                        return;
                    }

                    var (details, state) = BuildPresenceStrings();
                    ulong? appId = DefaultAppId;
                    string largeImage = null;
                    string largeText = null;
                    try
                    {
                        var rawAppId = App.LocalSettings.Read<string>(AppIdSettingKey, null);
                        if (!string.IsNullOrWhiteSpace(rawAppId) && ulong.TryParse(rawAppId.Trim(), out var parsed) && parsed > 0)
                            appId = parsed;
                    }
                    catch (Exception) { }

                    await client.UpdateRichPresenceAsync(
                        name: "Lyracord",
                        details: details,
                        state: state,
                        type: ActivityType.Playing,
                        startTimestamp: _sessionStart,
                        applicationId: appId,
                        largeImage: largeImage,
                        largeText: largeText).ConfigureAwait(false);

                    VoiceDiagnostics.Log($"Rich Presence: Lyracord ({details} · {state})");
                }
                catch (Exception ex)
                {
                    VoiceDiagnostics.Log("Rich Presence", ex);
                }
            });
        }

        private static (string Details, string State) BuildPresenceStrings()
        {
            var isMobile = string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Mobile", StringComparison.OrdinalIgnoreCase);
            var details = isMobile ? "Windows 10 Mobile" : "Windows Desktop";

            var version = "";
            try
            {
                var v = Package.Current.Id.Version;
                version = $"v{v.Major}.{v.Minor}.{v.Build}";
            }
            catch (Exception) { }

            var device = FriendlyDeviceName(isMobile);
            string state;
            if (!string.IsNullOrEmpty(device) && !string.IsNullOrEmpty(version))
                state = $"{device} · {version}";
            else if (!string.IsNullOrEmpty(device))
                state = device;
            else
                state = version;

            return (details, state);
        }

        private static string FriendlyDeviceName(bool isMobile)
        {
            try
            {
                var info = new EasClientDeviceInformation();
                var prod = (info.SystemProductName ?? "").Trim();
                var mfg = (info.SystemManufacturer ?? "").Trim();

                if (isMobile)
                {
                    if (prod.StartsWith("RM-1104", StringComparison.OrdinalIgnoreCase) ||
                        prod.StartsWith("RM-1105", StringComparison.OrdinalIgnoreCase) ||
                        prod.StartsWith("RM-1118", StringComparison.OrdinalIgnoreCase))
                        return "Lumia 950";
                    if (prod.StartsWith("RM-1085", StringComparison.OrdinalIgnoreCase) ||
                        prod.StartsWith("RM-1116", StringComparison.OrdinalIgnoreCase))
                        return "Lumia 950 XL";
                    if (prod.StartsWith("RM-1127", StringComparison.OrdinalIgnoreCase) ||
                        prod.StartsWith("RM-1128", StringComparison.OrdinalIgnoreCase))
                        return "Lumia 550";
                    if (prod.StartsWith("RM-1152", StringComparison.OrdinalIgnoreCase) ||
                        prod.StartsWith("RM-1154", StringComparison.OrdinalIgnoreCase))
                        return "Lumia 650";

                    if (!string.IsNullOrEmpty(prod))
                    {
                        var underscore = prod.IndexOf('_');
                        var clean = underscore > 0 ? prod.Substring(0, underscore) : prod;
                        return string.IsNullOrEmpty(mfg) ? clean : $"{mfg} {clean}";
                    }
                    return "Windows Phone";
                }

                return "UWP";
            }
            catch (Exception)
            {
                return isMobile ? "Windows Phone" : "UWP";
            }
        }
    }
}
