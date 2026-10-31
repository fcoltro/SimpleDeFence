using System;
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ClientSettingsTests
    {
        [Fact]
        public void Default_theme_is_auto()
        {
            Assert.Equal("auto", new ClientSettings().UiTheme);
        }

        [Fact]
        public void UiTheme_round_trips_through_serialization()
        {
            var original = new ClientSettings { UiTheme = "dark" };
            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ClientSettings());

            Assert.Equal("dark", restored.UiTheme);
        }

        [Fact]
        public void Default_language_is_auto()
        {
            Assert.Equal("auto", new ClientSettings().Language);
        }

        [Fact]
        public void Default_ask_for_exception_details_is_false()
        {
            Assert.False(new ClientSettings().AskForExceptionDetails);
        }

        [Fact]
        public void Default_enable_global_hotkeys_is_true()
        {
            Assert.True(new ClientSettings().EnableGlobalHotkeys);
        }

        [Fact]
        public void Blocked_history_window_defaults_to_an_hour()
        {
            // Was a hard-coded five minutes, which expired faster than a user notices an app is
            // broken and goes looking for why - leaving nothing to press Allow on.
            Assert.Equal(TimeSpan.FromMinutes(60), new ClientSettings().BlockedHistoryWindow);
        }

        [Fact]
        public void Blocked_history_window_clamps_nonsense_values()
        {
            // A persisted 0 is what an older settings file deserializes to, and must not mean
            // "a window of no length" - i.e. a Blocked list that can never show anything.
            Assert.Equal(TimeSpan.FromMinutes(60), new ClientSettings { BlockedHistoryMinutes = 0 }.BlockedHistoryWindow);
            Assert.Equal(TimeSpan.FromMinutes(1), new ClientSettings { BlockedHistoryMinutes = -5 }.BlockedHistoryWindow);
            Assert.Equal(TimeSpan.FromMinutes(1440), new ClientSettings { BlockedHistoryMinutes = 99999 }.BlockedHistoryWindow);
        }

        [Fact]
        public void Blocked_history_minutes_round_trips_through_serialization()
        {
            var original = new ClientSettings { BlockedHistoryMinutes = 120 };
            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ClientSettings());

            Assert.Equal(TimeSpan.FromMinutes(120), restored.BlockedHistoryWindow);
        }

        [Fact]
        public void New_fields_round_trip_through_serialization()
        {
            var original = new ClientSettings { Language = "pt-BR", AskForExceptionDetails = true, EnableGlobalHotkeys = false };
            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ClientSettings());

            Assert.Equal("pt-BR", restored.Language);
            Assert.True(restored.AskForExceptionDetails);
            Assert.False(restored.EnableGlobalHotkeys);
        }

        [Fact]
        public void A_muted_app_is_remembered_by_path_regardless_of_case()
        {
            var settings = new ClientSettings();
            Assert.True(settings.SetMuted(@"C:\Program Files\Adobe\Illustrator.exe", null, true));
            Assert.True(settings.IsMuted(@"c:\program files\adobe\illustrator.exe", null));
            Assert.False(settings.IsMuted(@"C:\Program Files\Adobe\Photoshop.exe", null));
        }

        [Fact]
        public void Unmuting_removes_it_and_muting_twice_keeps_one_entry()
        {
            var settings = new ClientSettings();
            settings.SetMuted(@"C:\Tools\a.exe", null, true);
            settings.SetMuted(@"C:\TOOLS\A.EXE", null, true);
            Assert.Single(settings.MutedApps!);
            settings.SetMuted(@"C:\Tools\a.exe", null, false);
            Assert.False(settings.IsMuted(@"C:\Tools\a.exe", null));
            Assert.Null(settings.MutedApps);
        }

        [Fact]
        public void A_row_with_nothing_to_name_it_by_cannot_be_muted()
        {
            var settings = new ClientSettings();
            Assert.False(settings.SetMuted(null, null, true));
            Assert.False(settings.SetMuted("svchost.exe", null, true));
            Assert.Null(settings.MutedApps);
        }

        [Fact]
        public void A_package_is_muted_by_its_id()
        {
            var settings = new ClientSettings();
            settings.SetMuted(null, "Contoso.App_123", true);
            Assert.True(settings.IsMuted(@"C:\whatever.exe", "Contoso.App_123"));
        }
    }
}
