using System.Globalization;
using System.Text;

namespace SimpleDeFence
{
    internal static class CoreUtils
    {
        public static string ExecutablePath { get; } = System.Reflection.Assembly.GetEntryAssembly()!.Location;

        public static string HexEncode(byte[] binstr)
        {
            var sb = new StringBuilder();
            foreach (byte oct in binstr)
                sb.Append(oct.ToString(@"X2", CultureInfo.InvariantCulture));

            return sb.ToString();
        }
    }
}
