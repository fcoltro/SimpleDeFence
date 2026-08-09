using SimpleDeFence.Localization;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// A JSON-per-locale scheme trades away the compile-time key safety a resx gives you for free.
    /// These tests are what buys it back: every LocKeys constant must exist in the English file
    /// (the source of truth), every English key must have a LocKeys constant (nothing orphaned),
    /// and every other locale file must cover the same key set (nothing silently falls back to
    /// English, and nothing in a translation is dead weight after a rename).
    /// </summary>
    public class LocTests
    {
        private static IReadOnlyList<string> AllDeclaredKeys()
        {
            var keys = new List<string>();
            CollectConstants(typeof(LocKeys), keys);
            return keys;
        }

        private static void CollectConstants(System.Type type, List<string> into)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.FieldType == typeof(string) && field.IsLiteral)
                    into.Add((string)field.GetRawConstantValue()!);
            }

            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
                CollectConstants(nested, into);
        }

        [Fact]
        public void English_file_is_present_and_non_empty()
        {
            var english = Loc.Load("en");

            Assert.NotNull(english);
            Assert.NotEmpty(english!);
        }

        [Fact]
        public void Every_LocKeys_constant_resolves_in_English()
        {
            var english = Loc.Load("en")!;
            var declared = AllDeclaredKeys();

            var missing = declared.Where(k => !english.ContainsKey(k)).ToList();

            Assert.True(missing.Count == 0,
                "LocKeys declares keys with no entry in Strings.en.json: " + string.Join(", ", missing));
        }

        [Fact]
        public void Every_English_key_has_a_LocKeys_constant()
        {
            var english = Loc.Load("en")!;
            var declared = AllDeclaredKeys().ToHashSet();

            var orphaned = english.Keys.Where(k => !declared.Contains(k)).ToList();

            Assert.True(orphaned.Count == 0,
                "Strings.en.json has keys with no LocKeys constant (unreachable from typed code): "
                    + string.Join(", ", orphaned));
        }

        [Theory]
        [InlineData("pt-BR")]
        public void Translation_covers_every_English_key(string culture)
        {
            var english = Loc.Load("en")!;
            var translation = Loc.Load(culture);

            Assert.True(translation is not null, $"No Strings.{culture}.json embedded resource found.");

            var missing = english.Keys.Where(k => !translation!.ContainsKey(k)).ToList();
            var extra = translation!.Keys.Where(k => !english.ContainsKey(k)).ToList();

            Assert.True(missing.Count == 0,
                $"Strings.{culture}.json is missing keys present in English: " + string.Join(", ", missing));
            Assert.True(extra.Count == 0,
                $"Strings.{culture}.json has keys not present in English (stale after a rename?): "
                    + string.Join(", ", extra));
        }

        [Fact]
        public void Missing_key_renders_as_a_visible_marker_not_a_blank()
        {
            // A key that cannot possibly exist. In a firewall UI a blank label hides a real problem;
            // the bracketed marker is deliberately impossible to miss on screen or in a screenshot.
            Assert.Equal("[this.key.does.not.exist]", Loc.T("this.key.does.not.exist"));
        }

        [Fact]
        public void Formatting_substitutes_arguments()
        {
            Loc.SetCulture("en");

            Assert.Equal("6 exceptions in profile \"Default\"",
                Loc.T(LocKeys.Applications.Summary, 6, "Default"));
        }

        [Fact]
        public void Malformed_translation_format_string_degrades_instead_of_throwing()
        {
            // Loc.T must not let a bad translation crash the GUI - formatting fails soft.
            var beforeCulture = Loc.Culture;
            try
            {
                Loc.SetCulture("en");
                // The real key takes one argument; passing none must not throw even though the
                // format string contains "{0}".
                var result = Loc.T(LocKeys.Mode.SwitchFailedGenericDetail);
                Assert.False(string.IsNullOrEmpty(result));
            }
            finally
            {
                Loc.SetCulture(beforeCulture);
            }
        }

        [Fact]
        public void SetCulture_falls_back_to_neutral_language_then_English()
        {
            var beforeCulture = Loc.Culture;
            try
            {
                // "pt-PT" has no file of its own; must fall back to the "pt" neutral file if present,
                // and to English if not - never to an empty string set.
                Loc.SetCulture("pt-PT");
                Assert.False(string.IsNullOrEmpty(Loc.T(LocKeys.App.Name)));

                Loc.SetCulture("xx-XX");
                Assert.Equal("SimpleDeFence", Loc.T(LocKeys.App.Name));
            }
            finally
            {
                Loc.SetCulture(beforeCulture);
            }
        }

        [Fact]
        public void CultureChanged_fires_when_the_culture_actually_changes()
        {
            var beforeCulture = Loc.Culture;
            try
            {
                Loc.SetCulture("en");

                int raised = 0;
                System.EventHandler handler = (_, _) => raised++;
                Loc.CultureChanged += handler;
                try
                {
                    Loc.SetCulture("pt-BR");
                    Assert.Equal(1, raised);
                    Assert.Equal("Regras", Loc.T(LocKeys.Nav.Rules));
                }
                finally
                {
                    Loc.CultureChanged -= handler;
                }
            }
            finally
            {
                Loc.SetCulture(beforeCulture);
            }
        }
    }
}
