using System;
using System.IO;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class UpdateDescriptorTests
    {
        /// <summary>The descriptor the service and the GUI both fetch, as it is actually published
        /// from this repository. Read from disk rather than reconstructed, so this fails if the
        /// published file and the model ever drift apart - which is how it can 404 or throw with
        /// nobody noticing: both callers swallow the failure.</summary>
        private static string? RepoDescriptorPath()
        {
            var p = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "updates", "update.json"));
            return File.Exists(p) ? p : null;
        }

        [Fact]
        public void The_published_descriptor_deserializes()
        {
            var path = RepoDescriptorPath();
            if (path is null)
                return; // running outside the repo layout

            var descriptor = SerializationHelper.DeserializeFromFile(path, new UpdateDescriptor());

            Assert.Equal("SimpleDeFence Update Descriptor", descriptor.MagicWord);
            Assert.NotEmpty(descriptor.Modules);
        }

        [Fact]
        public void The_published_descriptor_carries_a_module_for_this_architecture()
        {
            // GetModule is what both callers use; a descriptor without the running architecture's
            // module makes the update check silently do nothing.
            var path = RepoDescriptorPath();
            if (path is null)
                return;

            var descriptor = SerializationHelper.DeserializeFromFile(path, new UpdateDescriptor());

            Assert.NotNull(descriptor.GetModule(UpdateDescriptor.MODULE_NAME_MAINBIN));
        }

        [Fact]
        public void The_published_version_is_not_newer_than_this_build()
        {
            // The updater offers an update when ComponentVersion is greater than the running
            // assembly's. Publishing a version ahead of the build would prompt every user to
            // "update" to something that does not exist yet.
            var path = RepoDescriptorPath();
            if (path is null)
                return;

            var descriptor = SerializationHelper.DeserializeFromFile(path, new UpdateDescriptor());
            var module = descriptor.GetModule(UpdateDescriptor.MODULE_NAME_MAINBIN);
            Assert.NotNull(module);

            // Compared against the version SimpleDeFence.csproj declares, not against a loaded
            // assembly: the updater reads Assembly.GetEntryAssembly(), which under the test host is
            // the test host, and SimpleDeFence.Core carries its own unrelated 1.0.0.0.
            var csproj = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "SimpleDeFence", "SimpleDeFence.csproj"));
            if (!File.Exists(csproj))
                return;

            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(csproj), @"<Version>([^<]+)</Version>");
            Assert.True(match.Success, "SimpleDeFence.csproj declares no <Version>");

            var published = new Version(module!.ComponentVersion!);
            var declared = new Version(match.Groups[1].Value);

            Assert.True(published <= declared,
                $"descriptor advertises {published} but the project builds {declared}");
        }

        [Fact]
        public void No_hosts_or_database_module_is_advertised()
        {
            // The service downloads and installs these two without asking. They belong in the
            // descriptor only once there is a real payload and a matching hash to verify it.
            var path = RepoDescriptorPath();
            if (path is null)
                return;

            var descriptor = SerializationHelper.DeserializeFromFile(path, new UpdateDescriptor());

            Assert.Null(descriptor.GetModule(UpdateDescriptor.MODULE_NAME_HOSTS));
            Assert.Null(descriptor.GetModule(UpdateDescriptor.MODULE_NAME_DATABASE));
        }
    }
}
