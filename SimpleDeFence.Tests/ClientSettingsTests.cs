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
    }
}
