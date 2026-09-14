using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClawTweaksCenter.Core
{
    /// <summary>The thirteen languages Center ships, plus "follow the OS".
    ///
    /// APPEND ONLY, and for the usual reason: the chosen language is stored as this enum's NUMBER,
    /// so inserting a value in the middle silently moves every setting that was saved before it.
    /// New languages go at the end, however untidy that leaves the order. <see cref="Loc.Order"/>
    /// decides what the settings row shows, and it is free to sort them properly.</summary>
    public enum UiLanguage
    {
        /// <summary>Whatever Windows is set to, if we have it. The default, and what a fresh
        /// installation runs on - see <see cref="Loc.Detect"/>.</summary>
        System,
        English,
        German,
        French,
        Korean,
        Spanish,
        Russian,
        Greek,
        /// <summary>Mainland characters.</summary>
        ChineseSimplified,
        /// <summary>Taiwan and Hong Kong. Not a nicety on this hardware - MSI is a Taiwanese
        /// company, and its handhelds sell into a market that reads these characters.</summary>
        ChineseTraditional,
        Italian,
        /// <summary>Brazilian. Ten times the players of European Portuguese, and Portugal reads
        /// Brazilian far more comfortably than Brazil reads European.</summary>
        Portuguese,
        Japanese,
        Polish,
    }

    /// <summary>
    /// Center's translations.
    ///
    /// KEYED BY THE ENGLISH STRING, not by a symbolic id, and that is the whole design. A missing
    /// entry returns its own key, so an untranslated string renders in English instead of showing
    /// "Home_Tile_3_Title" or throwing. That makes partial coverage the NORMAL state rather than a
    /// defect: the tables below hold the strings somebody has actually checked, and everything else
    /// is correct English by construction. It is also why adding a new label to the interface needs
    /// no work here at all.
    ///
    /// The cost is the usual one for this shape: two identical English strings that need different
    /// translations cannot be told apart. Nothing in Center hits that today; the day it does, that
    /// one string gets a symbolic key and everything else stays as it is.
    ///
    /// WHERE IT IS APPLIED: at the few builders every screen goes through - the footer chips, the
    /// Home tiles, the settings rows, the status rows - not at the hundreds of call sites. One
    /// lookup at render time covers the interface, and a string that is not in a table simply
    /// passes through.
    ///
    /// DELIBERATELY CONSERVATIVE. Menu headings stay English, and a translation is only kept when it
    /// is close to the English in RENDERED WIDTH (see the check in Verify below) - Center's chips,
    /// tabs and tiles are laid out for the English word, and a label half again as long does not get
    /// a wider tile, it gets clipped. Where the honest translation is too long, the English stays.
    /// </summary>
    public static partial class Loc
    {
        /// <summary>The language in effect. Never <see cref="UiLanguage.System"/> - that is a stored
        /// preference, not a state, and it is resolved once on startup.</summary>
        public static UiLanguage Current { get; private set; } = UiLanguage.English;

        /// <summary>What the user picked, including "System". This is what the settings row shows.</summary>
        public static UiLanguage Preference { get; private set; } = UiLanguage.System;

        private static Dictionary<string, string> table;

        /// <summary>
        /// Resolves the stored preference into a live language. Call once, before the first window.
        /// </summary>
        public static void Initialise()
        {
            Preference = CenterSettings.Language;
            Current = Preference == UiLanguage.System ? Detect() : Preference;
            table = TableFor(Current);
        }

        /// <summary>Stores the preference and switches immediately.</summary>
        public static void Set(UiLanguage preference)
        {
            if (preference == Preference) return;

            Preference = preference;
            CenterSettings.Language = preference;
            Current = preference == UiLanguage.System ? Detect() : preference;
            table = TableFor(Current);
        }

        // NO "language changed" EVENT, deliberately. Center builds every screen in code and rebuilds
        // it on navigation, so the one caller of Set() redraws the window itself and everything else
        // is already redrawn by the time it is seen. An event here would be a second mechanism for
        // something that has exactly one subscriber.

        /// <summary>
        /// What Windows is set to, mapped onto what we ship. Anything else is English.
        ///
        /// CurrentUICulture, not CurrentCulture: the second one is the FORMATTING culture (dates,
        /// decimal separators) and follows the region, not the display language. A German keyboard
        /// layout with an English Windows is a common setup on this hardware, and it must stay
        /// English.
        ///
        /// Matched on the two-letter code, so de-AT and de-CH arrive at German rather than falling
        /// through to English on a technicality. The same reasoning sends pt-PT to the Brazilian
        /// table and es-MX to the Spanish one: a table in the reader's language, written for a
        /// different country, beats a screen in a language they may not have at all.
        ///
        /// CHINESE IS THE EXCEPTION, because the two-letter code does not carry the answer. "zh"
        /// alone says nothing about which characters to draw, and the two sets are not mutually
        /// readable at a glance. The script subtag decides it when Windows supplies one, and the
        /// region decides it otherwise - Taiwan, Hong Kong and Macau read traditional, everywhere
        /// else reads simplified.
        /// </summary>
        public static UiLanguage Detect()
        {
            try
            {
                CultureInfo ui = CultureInfo.CurrentUICulture;
                switch (ui.TwoLetterISOLanguageName)
                {
                    case "de": return UiLanguage.German;
                    case "fr": return UiLanguage.French;
                    case "ko": return UiLanguage.Korean;
                    case "es": return UiLanguage.Spanish;
                    case "ru": return UiLanguage.Russian;
                    case "el": return UiLanguage.Greek;
                    case "it": return UiLanguage.Italian;
                    case "pt": return UiLanguage.Portuguese;
                    case "ja": return UiLanguage.Japanese;
                    case "pl": return UiLanguage.Polish;
                    case "zh": return DetectChinese(ui);
                }
            }
            catch { }
            return UiLanguage.English;
        }

        /// <summary>Which characters this Chinese reader expects. Simplified unless told otherwise.</summary>
        private static UiLanguage DetectChinese(CultureInfo ui)
        {
            string name = ui.Name ?? string.Empty;

            // "zh-Hant", "zh-Hant-TW", "zh-Hant-HK" - the explicit answer, when there is one.
            if (name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0)
                return UiLanguage.ChineseTraditional;
            if (name.IndexOf("Hans", StringComparison.OrdinalIgnoreCase) >= 0)
                return UiLanguage.ChineseSimplified;

            // "zh-TW", "zh-HK", "zh-MO" - the older spelling, still what a lot of installations
            // report. Everything else, mainland and Singapore included, is simplified.
            foreach (string region in new[] { "-TW", "-HK", "-MO" })
                if (name.EndsWith(region, StringComparison.OrdinalIgnoreCase))
                    return UiLanguage.ChineseTraditional;

            return UiLanguage.ChineseSimplified;
        }

        /// <summary>
        /// The translation, or the English back unchanged.
        ///
        /// Null and empty pass straight through: this sits inside builders that are handed optional
        /// text, and a null check at every one of them would be the same check written many times.
        /// </summary>
        public static string T(string english)
        {
            if (string.IsNullOrEmpty(english)) return english;
            if (table == null) return english;
            return table.TryGetValue(english, out string s) ? s : english;
        }

        /// <summary>
        /// A translated string with values filled in.
        ///
        /// THE FORMAT IS THE KEY, not the finished sentence, and that is the point. "Half-Life is
        /// running" glues a name onto one end of an English sentence, and a language that puts it
        /// somewhere else cannot be translated at all from that shape; "{0} is running" can.
        ///
        /// It never throws at render time. A translation whose placeholders were mistyped falls back
        /// to the English format, and a broken English format returns the unformatted string - a
        /// screen that reads slightly wrong beats a screen that does not come up.
        /// </summary>
        public static string F(string english, params object[] args)
        {
            string s = T(english);
            if (args == null || args.Length == 0) return s;

            try { return string.Format(CultureInfo.CurrentCulture, s, args); }
            catch (FormatException) { }
            try { return string.Format(CultureInfo.CurrentCulture, english, args); }
            catch (FormatException) { return s; }
        }

        /// <summary>The language's name IN THAT LANGUAGE. Somebody who has landed in a language they
        /// cannot read has to be able to find their way out, and "German" does not help them -
        /// "Deutsch" does.</summary>
        public static string NameOf(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return "Deutsch";
                case UiLanguage.French: return "Français";
                case UiLanguage.Korean: return "한국어";
                case UiLanguage.Spanish: return "Español";
                case UiLanguage.Russian: return "Русский";
                case UiLanguage.Greek: return "Ελληνικά";
                // Named as each side names itself, not as "Chinese (Simplified)". The parenthesis
                // is a librarian's distinction; these are what the two look like to their readers.
                case UiLanguage.ChineseSimplified: return "简体中文";
                case UiLanguage.ChineseTraditional: return "繁體中文";
                case UiLanguage.Italian: return "Italiano";
                case UiLanguage.Portuguese: return "Português (BR)";
                case UiLanguage.Japanese: return "日本語";
                case UiLanguage.Polish: return "Polski";
                case UiLanguage.English: return "English";
                default: return T("System language");
            }
        }

        /// <summary>A reading order for the languages. System first: it is the default, and it is
        /// the entry somebody looking for "put it back" wants. English second, because it is the one
        /// name on this list that a reader of any of the others can recognise. After those two, the
        /// alphabetical order of their OWN names - the order they are written in on screen.
        ///
        /// ⚠️ NOTHING USES THIS, and nothing has since the settings screen grew a list that opens
        /// instead of a value that steps: it sorts with its own LanguageOrder(), by English name,
        /// because that is the column the list is read down. Order and <see cref="Next"/> are kept
        /// rather than deleted only because they are public and harmless - if a second screen ever
        /// needs an order, it should call LanguageOrder() and these two should go.</summary>
        public static readonly UiLanguage[] Order =
        {
            UiLanguage.System, UiLanguage.English,
            UiLanguage.German,               // Deutsch
            UiLanguage.Spanish,              // Español
            UiLanguage.French,               // Français
            UiLanguage.Italian,              // Italiano
            UiLanguage.Polish,               // Polski
            UiLanguage.Portuguese,           // Português
            UiLanguage.Greek,                // Ελληνικά
            UiLanguage.Russian,              // Русский
            UiLanguage.ChineseSimplified,    // 简体中文
            UiLanguage.ChineseTraditional,   // 繁體中文
            UiLanguage.Japanese,             // 日本語
            UiLanguage.Korean,               // 한국어
        };

        public static UiLanguage Next(UiLanguage current)
        {
            int i = Array.IndexOf(Order, current);
            return Order[(i < 0 ? 0 : (i + 1) % Order.Length)];
        }

        private static Dictionary<string, string> TableFor(UiLanguage language)
        {
            switch (language)
            {
                case UiLanguage.German: return German;
                case UiLanguage.French: return French;
                case UiLanguage.Korean: return Korean;
                case UiLanguage.Spanish: return Spanish;
                case UiLanguage.Russian: return Russian;
                case UiLanguage.Greek: return Greek;
                case UiLanguage.ChineseSimplified: return ChineseSimplified;
                case UiLanguage.ChineseTraditional: return ChineseTraditional;
                case UiLanguage.Italian: return Italian;
                case UiLanguage.Portuguese: return Portuguese;
                case UiLanguage.Japanese: return Japanese;
                case UiLanguage.Polish: return Polish;
                default: return null;
            }
        }
    }
}
