using System.Text;
using System.Text.Json;
using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ConfigExportTests
    {
        [Fact]
        public void Service_and_controller_round_trip_through_serialization()
        {
            var original = new ConfigExport
            {
                Service = new ServerConfiguration
                {
                    LockHostsFile = false,
                    AutoUpdateCheck = false,
                    ActiveProfileName = "Default"
                },
                Controller = new ClientSettings { UiTheme = "light" },
            };

            var bytes = SerializationHelper.Serialize(original);
            var restored = SerializationHelper.Deserialize(bytes, new ConfigExport());

            Assert.False(restored.Service.LockHostsFile);
            Assert.False(restored.Service.AutoUpdateCheck);
            Assert.Equal("light", restored.Controller.UiTheme);
        }

        [Fact]
        public void Deserializes_a_config_container_shaped_payload_ignoring_unknown_controller_fields()
        {
            // Mirrors the real shape SimpleDeFence.ConfigContainer (WinForms) serializes to:
            // {"Service": {...}, "Controller": {...}}, where Controller carries many WinForms-only
            // fields (window geometry, Language, EnableGlobalHotkeys, SettingsTabIndex) ConfigExport
            // does not know about. This is the cross-compatibility claim the Settings design doc's
            // decision 5 depends on: a .tws file WinForms exports must not throw when WinUI imports
            // it, even though WinUI's Controller type only recognizes a subset of its fields.
            var json = """
            {
              "Service": { "ConfigVersion": 1, "LockHostsFile": true, "AutoUpdateCheck": true, "Profiles": [] },
              "Controller": { "Language": "en", "UiTheme": "dark", "EnableGlobalHotkeys": true, "ConnFormWindowState": 0, "SettingsTabIndex": 2 }
            }
            """;

            var bytes = Encoding.UTF8.GetBytes(json);
            var restored = JsonSerializer.Deserialize(bytes, SourceGenerationContext.Default.ConfigExport);

            Assert.NotNull(restored);
            Assert.True(restored!.Service.LockHostsFile);
            Assert.Equal("dark", restored.Controller.UiTheme);
        }
    }
}
