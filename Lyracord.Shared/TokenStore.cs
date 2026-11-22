using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace Unicord
{
    /// <summary>
    /// The copy of the token for the desktop notification process (Unicord.Universal.Background), which cannot read the
    /// app's credential locker. Unicord kept it in LocalSettings as plain text; it is now encrypted with Windows data
    /// protection bound to the signed-in Windows user ("LOCAL=user"), so the settings file alone does not give the
    /// account away. The app itself and its background tasks keep using the PasswordVault.
    /// </summary>
    internal static class TokenStore
    {
        private const string ProtectedKey = "TokenProtected";
        private const string PlainKey = "Token"; // the old plain-text copy, removed wherever it is found

        public static async Task SaveForBackgroundAsync(string token)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values.Remove(PlainKey);
            if (string.IsNullOrEmpty(token))
            {
                values.Remove(ProtectedKey);
                return;
            }

            try
            {
                var provider = new DataProtectionProvider("LOCAL=user");
                var plain = CryptographicBuffer.ConvertStringToBinary(token, BinaryStringEncoding.Utf8);
                var sealedBuffer = await provider.ProtectAsync(plain).AsTask().ConfigureAwait(false);
                values[ProtectedKey] = CryptographicBuffer.EncodeToBase64String(sealedBuffer);
            }
            catch (Exception)
            {
                // no protected copy: the desktop notification process just will not start; never fall back to plain text
                values.Remove(ProtectedKey);
            }
        }

        public static async Task<string> ReadForBackgroundAsync()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (!values.TryGetValue(ProtectedKey, out var stored) || !(stored is string base64) || base64.Length == 0)
                return null;
            try
            {
                var provider = new DataProtectionProvider(); // unprotecting reads the descriptor from the data
                var sealedBuffer = CryptographicBuffer.DecodeFromBase64String(base64);
                var plain = await provider.UnprotectAsync(sealedBuffer).AsTask().ConfigureAwait(false);
                return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Logout: no copy of the token stays in the settings.</summary>
        public static void Clear()
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values.Remove(PlainKey);
            values.Remove(ProtectedKey);
        }
    }
}
