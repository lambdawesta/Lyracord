using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// The Windows Phone accent palette and the hub's tile colour setting: the original multicolour tiles,
    /// the system accent colour (like the Windows Phone start screen), or one chosen colour.
    /// </summary>
    internal static class MetroColors
    {
        public const string TileColorSetting = "HubTileColor";
        public const string Multicolor = "multi";
        public const string SystemAccent = "accent";

        public static readonly IReadOnlyList<(string Name, Color Color)> Palette = new List<(string, Color)>
        {
            ("lime", C(0xA4C400)), ("green", C(0x60A917)), ("emerald", C(0x008A00)), ("teal", C(0x00ABA9)),
            ("cyan", C(0x1BA1E2)), ("cobalt", C(0x0050EF)), ("indigo", C(0x6A00FF)), ("violet", C(0xAA00FF)),
            ("pink", C(0xF472D0)), ("magenta", C(0xD80073)), ("crimson", C(0xA20025)), ("red", C(0xE51400)),
            ("orange", C(0xFA6800)), ("amber", C(0xF0A30A)), ("yellow", C(0xE3C800)), ("brown", C(0x825A2C)),
            ("olive", C(0x6D8764)), ("steel", C(0x647687)), ("mauve", C(0x76608A)), ("taupe", C(0x87794E)),
        };

        public static Color C(int rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

        public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        public static bool TryParseHex(string text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var hex = text.Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                return false;
            color = C(rgb);
            return true;
        }

        public static Color Accent()
        {
            try
            {
                return new UISettings().GetColorValue(UIColorType.Accent);
            }
            catch (Exception)
            {
                return C(0x1BA1E2);
            }
        }

        /// <summary>The colour every tile should use, or null for the multicolour default.</summary>
        public static Color? TileOverride()
        {
            var mode = App.LocalSettings.Read(TileColorSetting, Multicolor);
            if (mode == SystemAccent)
                return Accent();
            return TryParseHex(mode, out var c) ? c : (Color?)null;
        }
    }
}
