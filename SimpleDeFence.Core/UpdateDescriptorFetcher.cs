using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleDeFence
{
    /// <summary>Framework-agnostic half of SimpleDeFence/UpdateChecker.cs's update-descriptor fetch
    /// (the interactive check/confirm/download flow that file also holds is genuinely WinForms-
    /// coupled and is not ported here - see SimpleDeFence.UI.Services.Updater for its WinUI
    /// replacement).
    ///
    /// Named UpdateDescriptorFetcher rather than UpdateChecker: SimpleDeFence.csproj (the WinForms
    /// exe) compiles every top-level .cs file in this directory directly into its own assembly via
    /// a &lt;Compile Include&gt; glob (see SimpleDeFence.csproj), and SimpleDeFence/UpdateChecker.cs
    /// already declares a class named SimpleDeFence.UpdateChecker in that same compilation - two
    /// non-partial classes with that name would be a duplicate-definition (CS0101) error. This stays
    /// in the flat "SimpleDeFence" namespace (not "SimpleDeFence.Core") to match every sibling file
    /// in this directory, which is what makes the glob-sharing trick work without extra usings at
    /// WinForms call sites.
    /// </summary>
    public static class UpdateDescriptorFetcher
    {
        private const string URL_UPDATE_DESCRIPTOR = @"https://raw.githubusercontent.com/fcoltro/SimpleDeFence/refs/heads/main/updates/update.json";

        public static async Task<UpdateDescriptor> GetDescriptorAsync(CancellationToken cancellationToken)
        {
            var productVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("TW-Version", productVersion);

            using var response = await httpClient.GetAsync(URL_UPDATE_DESCRIPTOR, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync().ConfigureAwait(false);

            var descriptor = SerializationHelper.Deserialize(System.Text.Encoding.UTF8.GetBytes(json), new UpdateDescriptor());
            if (descriptor.MagicWord != "SimpleDeFence Update Descriptor")
                throw new ApplicationException("Bad update descriptor file.");

            return descriptor;
        }
    }
}
