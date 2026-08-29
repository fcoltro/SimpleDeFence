using System;
using System.ComponentModel;
using SimpleDeFence.Windows.Services;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// These run against the machine's real service control manager, because that is the thing
    /// whose behaviour the code under test exists to survive. They are cheap - one SCM enumeration
    /// - and they assert only properties that hold on any Windows install, elevated or not.
    /// </summary>
    public class ServicePidMapTests
    {
        [Fact]
        public void GetServicePid_throws_for_a_service_that_is_not_installed()
        {
            // The ending ServicePidMap's loop has to tolerate. A service present in
            // ServiceController.GetServices()'s snapshot can be gone by the time the loop reaches
            // it, and OpenService then fails exactly like this. Pinned because the whole point of
            // the guard around that call is that this throws rather than returning null.
            using var scm = new ServiceControlManager();

            Assert.Throws<Win32Exception>(
                () => scm.GetServicePid("SimpleDeFenceNoSuchService_" + Guid.NewGuid().ToString("N")));
        }

        [Fact]
        public void Building_the_map_survives_whatever_the_machine_has_running()
        {
            // Before the per-service guard this could throw straight out of the constructor, and
            // FirewallClient.GetConnectionsAsync builds this map before it gathers anything - so
            // the exception emptied the Connections screen's Blocked, Connected AND Open lists at
            // once, including the two that never needed the service control manager.
            var map = new ServicePidMap();

            Assert.NotNull(map);
        }

        [Fact]
        public void CreateOrEmpty_always_returns_a_usable_map()
        {
            var map = ServicePidMap.CreateOrEmpty();

            Assert.NotNull(map);
            // A pid nothing could be hosting: answers empty rather than throwing or returning null,
            // which is what lets callers treat "no service names" as a naming detail rather than a
            // reason to drop the row.
            Assert.Empty(map.GetServicesInPid(uint.MaxValue));
        }
    }
}
