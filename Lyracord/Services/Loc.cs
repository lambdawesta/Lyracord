using System;
using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace Unicord.Universal.Services
{
    /// <summary>
    /// Texts of the Metro pages, from Resources/&lt;language&gt;/Metro.resw (English is the default, plus Russian and
    /// Ukrainian). XAML uses x:Uid="/Metro/Name" with keys "Name.Property"; code uses the keys with underscores.
    /// A missing key shows the key itself, so it is easy to spot.
    /// </summary>
    internal static class Loc
    {
        private static ResourceLoader _loader;
        private static bool? _slavic;

        private static ResourceLoader Loader
            => _loader ??= ResourceLoader.GetForViewIndependentUse("Metro");

        public static void Reset()
        {
            _loader = null;
            _slavic = null;
        }

        public static string T(string key)
        {
            try
            {
                var value = Loader.GetString(key);
                return string.IsNullOrEmpty(value) ? key : value;
            }
            catch (Exception)
            {
                return key;
            }
        }

        public static string F(string key, params object[] args)
        {
            try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
            catch (FormatException) { return T(key); }
        }

        /// <summary>
        /// A counted phrase: keys key_One, key_Few, key_Many with {0} for the number. Russian and Ukrainian use three
        /// forms (1 / 2-4 / 5+, with 11-14 as "many"); English uses One for 1 and Many otherwise. The rules follow the
        /// language the resources actually came from ("PluralRules" in Metro.resw).
        /// </summary>
        public static string Plural(string key, long n)
        {
            _slavic ??= T("PluralRules") == "slavic";
            string form;
            if (_slavic.Value)
            {
                var d = Math.Abs(n) % 10;
                var dd = Math.Abs(n) % 100;
                form = d == 1 && dd != 11 ? "One" : d >= 2 && d <= 4 && (dd < 12 || dd > 14) ? "Few" : "Many";
            }
            else
            {
                form = n == 1 ? "One" : "Many";
            }
            return F(key + "_" + form, n);
        }
    }
}
