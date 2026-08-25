using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        private const string ResourcePrefix = "SimpleDeFence.Localization.Strings.";
        private const string ResourceSuffix = ".json";

        private static readonly object _sync = new object();
        private static Dictionary<string, string> _strings = Load(DefaultCulture) ?? new Dictionary<string, string>();
        private static Dictionary<string, string> _fallback = _strings;
        private static string _culture = DefaultCulture;
        private static bool _isRightToLeft;

        /// <summary>The culture currently in use, e.g. "en" or "pt-BR".</summary>
        public static string Culture
        {
            get { lock (_sync) { return _culture; } }
        }

        /// <summary>
        /// True when the language in use is written right-to-left (Arabic, Persian), so the shell
        /// can flip its FlowDirection. Reports the *effective* language: a locale that failed to
        /// load and fell back to English is left-to-right, whatever was asked for.
        /// </summary>
        public static bool IsRightToLeft
        {
            get { lock (_sync) { return _isRightToLeft; } }
        }

        /// <summary>Raised after the culture changes so open screens can re-read their text.</summary>
        public static event EventHandler? CultureChanged;

        /// <summary>
        /// Selects the language for the current session, resolving it against the languages that
        /// actually ship. See <see cref="Resolve"/> for the order; a locale that resolves to
        /// nothing leaves the app in English rather than blank.
        /// </summary>
        public static void SetCulture(string culture)
        {
            if (string.IsNullOrWhiteSpace(culture))
                culture = DefaultCulture;

            var (loaded, resolvedName) = Resolve(culture);

            lock (_sync)
            {
                _strings = loaded ?? _fallback;
                _culture = loaded is null ? DefaultCulture : culture;
                _isRightToLeft = IsRightToLeftCulture(loaded is null ? DefaultCulture : resolvedName);
            }

            CultureChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>Selects the language from the OS setting.</summary>
        public static void UseSystemCulture()
            => SetCulture(CultureInfo.CurrentUICulture.Name);

        /// <summary>
        /// Finds the best shipped language file for a requested culture, and reports which one it
        /// landed on. Four steps, in order:
        ///
        ///   1. An exact match - "pt-BR" finds Strings.pt-BR.json.
        ///   2. The CultureInfo parent chain. This is the step that makes Chinese work at all:
        ///      Windows reports zh-CN / zh-TW / zh-HK, and .NET's parent of zh-CN is zh-Hans (not
        ///      "zh"), which is what the file is actually named. Naive truncation at the first
        ///      dash - what this used to do - turns every Chinese locale into "zh", matches no
        ///      file, and silently drops the user into English.
        ///   3. Any shipped sibling in the same language. Portuguese ships only as pt-BR, so a
        ///      pt-PT user gets Brazilian Portuguese, which is far closer than English.
        ///   4. Nothing - the caller falls back to English.
        /// </summary>
        private static (Dictionary<string, string>? Strings, string ResolvedName) Resolve(string culture)
        {
            var exact = Load(culture);
            if (exact is not null)
                return (exact, culture);

            foreach (var parent in ParentChain(culture))
            {
                var loaded = Load(parent);
                if (loaded is not null)
                    return (loaded, parent);
            }

            var language = TwoLetterLanguage(culture);
            if (language is not null)
            {
                foreach (var available in AvailableCultures())
                {
                    if (string.Equals(TwoLetterLanguage(available), language, StringComparison.OrdinalIgnoreCase))
                    {
                        var loaded = Load(available);
                        if (loaded is not null)
                            return (loaded, available);
                    }
                }
            }

            return (null, DefaultCulture);
        }

        /// <summary>Walks "zh-CN" -> "zh-Hans" -> "zh", stopping before the invariant culture.</summary>
        private static IEnumerable<string> ParentChain(string culture)
        {
            // The lookup is deliberately outside the iterator's yielding section: C# forbids a
            // yield inside a catch, so the unknown-tag case is decided first and yielded after.
            var info = TryGetCulture(culture);

            if (info is null)
            {
                // An unknown tag still has a usable language part: "xx-XX" -> "xx".
                int dash = culture.IndexOf('-');
                if (dash > 0)
                    yield return culture.Substring(0, dash);
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { info.Name };

            while (true)
            {
                info = info.Parent;

                // The invariant culture is the end of every chain, and its name is empty.
                if (string.IsNullOrEmpty(info.Name) || !seen.Add(info.Name))
                    yield break;

                yield return info.Name;
            }
        }

        /// <summary>CultureInfo.GetCultureInfo, but null instead of an exception for a tag Windows
        /// does not know. Command-line and settings values reach here unvalidated.</summary>
        private static CultureInfo? TryGetCulture(string culture)
        {
            try
            {
                return CultureInfo.GetCultureInfo(culture);
            }
            catch (CultureNotFoundException)
            {
                return null;
            }
        }

        /// <summary>"pt-BR" -> "pt". Null when there is nothing language-like to compare.</summary>
        private static string? TwoLetterLanguage(string culture)
        {
            if (string.IsNullOrEmpty(culture))
                return null;

            int dash = culture.IndexOf('-');
            var language = dash > 0 ? culture.Substring(0, dash) : culture;
            return language.Length == 0 ? null : language;
        }

        /// <summary>Every language that ships, read off the embedded resource names.</summary>
        public static IReadOnlyList<string> AvailableCultures()
        {
            var asm = typeof(Loc).GetTypeInfo().Assembly;

            return asm.GetManifestResourceNames()
                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                         && n.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                .Select(n => n.Substring(ResourcePrefix.Length, n.Length - ResourcePrefix.Length - ResourceSuffix.Length))
                .Where(n => n.Length > 0)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }

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
                return string.Format(FormatCulture(), format, args);
            }
            catch (FormatException)
            {
                // A translation with a malformed placeholder must not crash the GUI; showing the
                // unformatted text is a visible defect rather than a fatal one.
                return format;
            }
        }

        /// <summary>
        /// Numbers inside a translated sentence follow the language being displayed, not the
        /// machine's regional settings. Otherwise German text on a US-configured machine renders
        /// a thousands separator as "," where the sentence around it expects "." - which in German
        /// reads as a different number rather than as a formatting quirk. Falls back to
        /// CurrentCulture when the UI language is not a culture Windows knows.
        /// </summary>
        private static CultureInfo FormatCulture()
        {
            string culture;
            lock (_sync) { culture = _culture; }

            return TryGetCulture(culture) ?? CultureInfo.CurrentCulture;
        }

        /// <summary>Whether a culture's script runs right-to-left, ignoring unknown tags.</summary>
        private static bool IsRightToLeftCulture(string culture)
            => TryGetCulture(culture)?.TextInfo.IsRightToLeft ?? false;

        /// <summary>All keys currently loaded. Used by the tests that police key parity.</summary>
        public static IReadOnlyCollection<string> Keys
        {
            get { lock (_sync) { return new List<string>(_strings.Keys); } }
        }

        /// <summary>Reads and flattens one embedded language file, or null when absent.</summary>
        internal static Dictionary<string, string>? Load(string culture)
        {
            var asm = typeof(Loc).GetTypeInfo().Assembly;
            var resource = ResourcePrefix + culture + ResourceSuffix;

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
