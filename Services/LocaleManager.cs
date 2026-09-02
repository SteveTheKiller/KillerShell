using System;
using System.Globalization;
using System.Windows;

namespace KillerShell.Services
{
    // 15 UI languages, matching KillerScan and KillerPDF. en-US is always the base layer so any
    // locale that omits a key falls back to English; the chosen locale's file layers on top.
    // Ported from KillerScan.
    //
    // Append new members at the END: the value is persisted by NAME, not by ordinal, but keeping
    // the order stable also keeps the language menu's order stable.
    public enum Locale { EnUS, Es, ZhTW, ZhCN, Bn, TrTR, De, Fr, Ja, Cs, PlPL, HuHU, It, RuRU, KkKZ }

    public static class LocaleManager
    {
        // Persistence hooks (wired in App.xaml.cs). Default: in-memory only.
        public static Func<string, string?> GetSetting { get; set; } = _ => null;
        public static Action<string, string> SetSetting { get; set; } = (_, _) => { };

        // App.xaml merged-dictionary layout for KillerShell:
        //   [0] theme palette   [1] Controls.xaml   [2] AppStyles.xaml
        //   [3] Strings/en-US.xaml  (string BASE - always present)
        //   [4] chosen locale override (added at runtime; absent for English)
        private const int BaseIndex = 3;
        private const int OverrideIndex = 4;

        private static Locale _current = Locale.EnUS;
        public static Locale Current => _current;

        /// <summary>Call once at startup (after ThemeManager.Initialize) to restore the saved locale.</summary>
        public static void Initialize()
        {
            var saved = GetSetting("Locale");
            if (Enum.TryParse<Locale>(saved, out var locale))
            {
                _current = locale;
            }
            else
            {
                _current = LocaleForCulture(CultureInfo.CurrentUICulture);
                SetSetting("Locale", _current.ToString());
            }
            ApplyInternal(_current);
        }

        internal static Locale LocaleForCulture(CultureInfo culture)
        {
            string name = culture.Name;
            string language = culture.TwoLetterISOLanguageName;
            if (language == "zh")
                return name.IndexOf("Hant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       name.EndsWith("-TW", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-HK", StringComparison.OrdinalIgnoreCase) ||
                       name.EndsWith("-MO", StringComparison.OrdinalIgnoreCase)
                    ? Locale.ZhTW
                    : Locale.ZhCN;

            return language switch
            {
                "bn" => Locale.Bn, "cs" => Locale.Cs, "de" => Locale.De,
                "es" => Locale.Es, "fr" => Locale.Fr, "hu" => Locale.HuHU,
                "it" => Locale.It, "ja" => Locale.Ja, "kk" => Locale.KkKZ,
                "pl" => Locale.PlPL, "ru" => Locale.RuRU, "tr" => Locale.TrTR,
                _ => Locale.EnUS,
            };
        }

        /// <summary>Switch locale, persist the choice, and hot-swap the string ResourceDictionary.</summary>
        public static void Apply(Locale locale)
        {
            _current = locale;
            SetSetting("Locale", locale.ToString());
            ApplyInternal(locale);
        }

        private static void ApplyInternal(Locale locale)
        {
            var merged = Application.Current.Resources.MergedDictionaries;

            // Re-assert the English base so a partial locale falls back to English for missing keys.
            if (merged.Count > BaseIndex)
                merged[BaseIndex] = new ResourceDictionary { Source = new Uri("pack://application:,,,/Strings/en-US.xaml") };

            Uri? overrideUri = locale switch
            {
                Locale.Es   => new Uri("pack://application:,,,/Strings/es.xaml"),
                Locale.Fr   => new Uri("pack://application:,,,/Strings/fr-FR.xaml"),
                Locale.ZhTW => new Uri("pack://application:,,,/Strings/zh-TW.xaml"),
                Locale.ZhCN => new Uri("pack://application:,,,/Strings/zh-CN.xaml"),
                Locale.Bn   => new Uri("pack://application:,,,/Strings/bn.xaml"),
                Locale.TrTR => new Uri("pack://application:,,,/Strings/tr-TR.xaml"),
                Locale.De   => new Uri("pack://application:,,,/Strings/de-DE.xaml"),
                Locale.Ja   => new Uri("pack://application:,,,/Strings/ja-JP.xaml"),
                Locale.Cs   => new Uri("pack://application:,,,/Strings/cs-CZ.xaml"),
                Locale.PlPL => new Uri("pack://application:,,,/Strings/pl-PL.xaml"),
                Locale.HuHU => new Uri("pack://application:,,,/Strings/hu-HU.xaml"),
                Locale.It   => new Uri("pack://application:,,,/Strings/it-IT.xaml"),
                Locale.RuRU => new Uri("pack://application:,,,/Strings/ru-RU.xaml"),
                Locale.KkKZ => new Uri("pack://application:,,,/Strings/kk-KZ.xaml"),
                _           => null,   // English: base only
            };

            if (overrideUri is not null)
            {
                try
                {
                    var ov = new ResourceDictionary { Source = overrideUri };
                    if (merged.Count > OverrideIndex) merged[OverrideIndex] = ov; else merged.Add(ov);
                }
                catch
                {
                    // Locale file not present yet - stay on the English base instead of crashing.
                    if (merged.Count > OverrideIndex) merged.RemoveAt(OverrideIndex);
                }
            }
            else if (merged.Count > OverrideIndex)
            {
                merged.RemoveAt(OverrideIndex);
            }
        }
    }
}
