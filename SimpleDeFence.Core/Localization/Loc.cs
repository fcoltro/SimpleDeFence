using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SimpleDeFence.Localization
{
    /// <summary>
    /// App-wide string lookup, driven by one JSON file per language.
    ///
    /// The .NET default would be a resx per form per language - which is how the WinForms GUI ended
    /// up with 194 of them. Here every string for the whole app lives in a single file per locale
    /// (Strings.&lt;culture&gt;.json), keyed by dotted paths like "mode.normal.label", which is the
    /// shape web i18n libraries use and is far easier to hand to a translator.
    ///
    /// The trade-off that model normally carries is losing compile-time safety: a mistyped key
    /// becomes a blank label at runtime. Two things push back on that here - <see cref="LocKeys"/>
    /// gives call sites typed constants instead of loose strings, and a unit test asserts that the
    /// constants, the English file, and every translation stay in agreement.
    /// </summary>
    public static class Loc
    {
        private const string DefaultCulture = "en";

        private static readonly object _sync = new object();
        private static Dictionary<string, string> _strings = Load(DefaultCulture) ?? new Dictionary<string, string>();
        private static Dictionary<string, string> _fallback = _strings;
        private static string _culture = DefaultCulture;

        /// <summary>The culture currently in use, e.g. "en" or "pt-BR".</summary>
        public static string Culture
        {
            get { lock (_sync) { return _culture; } }
        }

        /// <summary>Raised after the culture changes so open screens can re-read their text.</summary>
        public static event EventHandler? CultureChanged;

        /// <summary>
        /// Selects the language for the current session. Falls back to the neutral language
        /// ("pt-BR" -> "pt") and then to English, so a partially translated locale still works.
        /// </summary>
        public static void SetCulture(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
                culture = DefaultCulture;

            var loaded = Load(culture);

            if (loaded is null)
            {
                int dash = culture.IndexOf('-');
                if (dash > 0)
                    loaded = Load(culture.Substring(0, dash));
            }

            lock (_sync)
            {
                _strings = loaded ?? _fallback;
                _culture = loaded is null ? DefaultCulture : culture;
            }

            CultureChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Selects the language from the OS setting.</summary>
        public static void UseSystemCulture()
            => SetCulture(CultureInfo.CurrentUICulture.Name);

        /// <summary>
        /// Resolves a key. A missing key returns the key itself wrapped in brackets rather than an
        /// empty string - a blank label in a firewall UI hides the problem, whereas "[mode.foo]" is
        /// obvious on sight and in a screenshot.
        /// </summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            lock (_sync)
            {
                if (_strings.TryGetValue(key, out var value))
                    return value;

                // A translation may be incomplete; English is the backstop.
                if (_fallback.TryGetValue(key, out var englishValue))
                    return englishValue;
            }

            return "[" + key + "]";
        }

        /// <summary>Resolves a key and formats it with the given arguments.</summary>
        public static string T(string key, params object[] args)
        {
            var format = T(key);

            if (args is null || args.Length == 0)
                return format;

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, args);
            }
            catch (FormatException)
            {
                // A translation with a malformed placeholder must not crash the GUI; showing the
                // unformatted text is a visible defect rather than a fatal one.
                return format;
            }
        }

        /// <summary>All keys currently loaded. Used by the tests that police key parity.</summary>
        public static IReadOnlyCollection<string> Keys
        {
            get { lock (_sync) { return new List<string>(_strings.Keys); } }
        }

        /// <summary>Reads and flattens one embedded language file, or null when absent.</summary>
        internal static Dictionary<string, string>? Load(string culture)
        {
            var asm = typeof(Loc).GetTypeInfo().Assembly;
            var resource = "SimpleDeFence.Localization.Strings." + culture + ".json";

            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());

            var flat = new Dictionary<string, string>(StringComparer.Ordinal);
            Flatten(doc.RootElement, string.Empty, flat);
            return flat;
        }

        /// <summary>Turns nested objects into dotted keys: { "mode": { "normal": ... } } -> "mode.normal".</summary>
        private static void Flatten(JsonElement element, string prefix, Dictionary<string, string> into)
        {
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Object:
                        Flatten(property.Value, key, into);
                        break;
                    case JsonValueKind.String:
                        into[key] = property.Value.GetString() ?? string.Empty;
                        break;
                    default:
                        into[key] = property.Value.ToString();
                        break;
                }
            }
        }
    }
}
