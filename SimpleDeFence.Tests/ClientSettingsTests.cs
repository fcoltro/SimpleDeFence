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
