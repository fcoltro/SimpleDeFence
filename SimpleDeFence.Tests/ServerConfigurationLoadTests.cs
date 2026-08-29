using System;
using System.IO;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>Pins the behaviour that made a fresh install unusable: ServerConfiguration.Load
    /// reports "no config on disk" by returning an unusable object, not by throwing. The service's
    /// LoadServerConfig relies on these exact facts to decide when to build a default config
    /// instead, so if any of them change, that decision has to change with them.</summary>
    public class ServerConfigurationLoadTests : IDisposable
    {
        private readonly string _dir;

        public ServerConfigurationLoadTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sdf-cfgload-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Load_of_a_missing_file_does_not_throw()
        {
            var missing = Path.Combine(_dir, "config");
            Assert.False(File.Exists(missing));

            var cfg = ServerConfiguration.Load(missing);

            Assert.NotNull(cfg);
        }

        [Fact]
        public void Load_of_a_missing_file_yields_a_config_naming_no_profile()
        {
            // This is the whole trap. A caller that treats "did not throw" as "got a config"
            // ends up holding one of these, and every ActiveProfile access on it throws.
            var cfg = ServerConfiguration.Load(Path.Combine(_dir, "config"));

            Assert.True(string.IsNullOrEmpty(cfg.ActiveProfileName),
                "Load of a missing file is expected to leave ActiveProfileName empty; " +
                "SimpleDeFenceService.LoadServerConfig checks exactly that to detect it.");
        }

        [Fact]
        public void ActiveProfile_throws_when_no_profile_is_named()
        {
            var cfg = ServerConfiguration.Load(Path.Combine(_dir, "config"));

            // What the service hit on every command before LoadServerConfig learned to reject a
            // profile-less config: the service stayed up, so the GUI reported "Not connected"
            // rather than anything pointing here.
            Assert.Throws<InvalidOperationException>(() => cfg.ActiveProfile);
        }

        [Fact]
        public void Load_of_a_missing_file_reports_Missing()
        {
            ServerConfiguration.Load(Path.Combine(_dir, "config"), out var outcome);

            Assert.Equal(ConfigLoadOutcome.Missing, outcome);
        }

        [Fact]
        public void Load_of_a_tampered_config_reports_it_rather_than_looking_like_a_first_run()
        {
            // Both endings produce a config naming no profile, and the service builds defaults
            // from either. Only the outcome separates "there was nothing here" from "there was
            // something here and it did not authenticate" - which is what reaches the user as a
            // degraded-state warning instead of a silent reset.
            var path = Path.Combine(_dir, "config");
            new ServerConfiguration { ActiveProfileName = "Default" }.Save(path);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            var cfg = ServerConfiguration.Load(path, out var outcome);

            Assert.Equal(ConfigLoadOutcome.Unauthenticated, outcome);
            Assert.True(string.IsNullOrEmpty(cfg.ActiveProfileName));
        }

        [Fact]
        public void A_config_that_names_a_profile_is_usable_and_round_trips()
        {
            var path = Path.Combine(_dir, "config");
            new ServerConfiguration { ActiveProfileName = "Default" }.Save(path);

            var cfg = ServerConfiguration.Load(path);

            Assert.Equal("Default", cfg.ActiveProfileName);
            Assert.NotNull(cfg.ActiveProfile);
            Assert.Equal("Default", cfg.ActiveProfile.ProfileName);
        }
    }
}
