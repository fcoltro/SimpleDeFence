using System.Globalization;
using System.Text;

namespace SimpleDeFence
{
    internal static class CoreUtils
    {
        /// <summary>
        /// Full path of the running executable. Backs the "%twpath%" folder variable, which rule
        /// paths in the application database resolve against.
        ///
        /// Environment.ProcessPath, not Assembly.GetEntryAssembly().Location: Location is
        /// documented to return an empty string for an assembly embedded in a single-file app, and
        /// it fails quietly - Path.GetDirectoryName("") is "", so every %twpath% rule would resolve
        /// to a relative path and match nothing, with no error anywhere. ProcessPath is right in
        /// both layouts. The two differ in one harmless way even today: Location named
        /// SimpleDeFence.dll where ProcessPath names SimpleDeFence.exe, and the only caller takes
        /// the directory of it, which is the same either way.
        /// </summary>
        public static string ExecutablePath { get; } =
            System.Environment.ProcessPath ?? System.AppContext.BaseDirectory;

        public static string HexEncode(byte[] binstr)
        {
            var sb = new StringBuilder();
            foreach (byte oct in binstr)
                sb.Append(oct.ToString(@"X2", CultureInfo.InvariantCulture));

            return sb.ToString();
        }
    }
}
