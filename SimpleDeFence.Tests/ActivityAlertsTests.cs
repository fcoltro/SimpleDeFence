using System;
using Xunit;
using Sighting = SimpleDeFence.ActivityAlerts.Sighting;

namespace SimpleDeFence.Tests
{
    public class ActivityAlertsTests
    {
        private static readonly Sighting Chrome = new(@"exe:C:\Program Files\Google\Chrome\Application\chrome.exe", "chrome.exe");
        private static readonly Sighting Game = new(@"exe:C:\Games\game.exe", "game.exe");

        [Fact]
        public void Nothing_already_there_when_watching_starts_is_announced()
        {
            var alerts = new ActivityAlerts();

            var (blocked, listening) = alerts.Observe(new[] { Chrome }, new[] { Game });

            Assert.Empty(blocked);
            Assert.Empty(listening);
        }

        [Fact]
        public void A_new_app_is_announced_once()
        {
            var alerts = new ActivityAlerts();
            alerts.Observe(new[] { Chrome }, Array.Empty<Sighting>());

            var first = alerts.Observe(new[] { Chrome, Game }, Array.Empty<Sighting>());
            var second = alerts.Observe(new[] { Chrome, Game }, Array.Empty<Sighting>());

            Assert.Equal(new[] { Game }, first.NewlyBlocked);
            Assert.Empty(second.NewlyBlocked);
        }

        [Fact]
        public void Blocked_and_listening_are_tracked_separately()
        {
            var alerts = new ActivityAlerts();
            alerts.Observe(new[] { Game }, Array.Empty<Sighting>());

            var (blocked, listening) = alerts.Observe(new[] { Game }, new[] { Game });

            Assert.Empty(blocked);
            Assert.Equal(new[] { Game }, listening);
        }

        [Fact]
        public void After_a_reset_the_next_snapshot_is_a_new_baseline()
        {
            var alerts = new ActivityAlerts();
            alerts.Observe(Array.Empty<Sighting>(), Array.Empty<Sighting>());
            alerts.Reset();

            var (blocked, _) = alerts.Observe(new[] { Chrome }, Array.Empty<Sighting>());

            Assert.Empty(blocked);
        }

        [Theory]
        [InlineData(@"C:\Windows\System32\svchost.exe", null, null)]
        [InlineData(@"c:\windows\explorer.exe", null, null)]
        [InlineData(null, null, null)]
        [InlineData("", null, null)]
        [InlineData("chrome.exe", null, null)]
        [InlineData(@"C:\WindowsApps2\tool.exe", null, @"exe:C:\WindowsApps2\tool.exe")]
        [InlineData(@"C:\Games\game.exe", null, @"exe:C:\Games\game.exe")]
        [InlineData(null, "S-1-15-2-1", "pkg:S-1-15-2-1")]
        [InlineData(@"C:\Windows\SystemApps\app.exe", "S-1-15-2-1", "pkg:S-1-15-2-1")]
        public void Windows_components_and_unnamed_apps_are_never_announced(string? path, string? package, string? expected)
        {
            Assert.Equal(expected, ActivityAlerts.KeyFor(path, package, @"C:\Windows"));
        }
    }
}
