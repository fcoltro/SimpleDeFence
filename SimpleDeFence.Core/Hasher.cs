using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SimpleDeFence
{
    public static class Hasher
    {
        public static string HashStream(Stream stream)
        {
            using SHA256 hasher = SHA256.Create();
            return CoreUtils.HexEncode(hasher.ComputeHash(stream));
        }

        public static string HashFile(string filePath)
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read);
            return HashStream(fs);
        }

        public static string HashString(string text)
        {
            using SHA256 hasher = SHA256.Create();
            return CoreUtils.HexEncode(hasher.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        /// <summary>True only when the file matches a checksum that was actually published for it.
        /// Both an absent expected hash and an unreadable file are false: a payload nobody vouched
        /// for, and one we cannot check, are equally unverified. Used before running anything
        /// downloaded from the network.</summary>
        public static bool FileMatchesHash(string filePath, string? expectedHash)
        {
            if (string.IsNullOrWhiteSpace(expectedHash))
                return false;

            try
            {
                return HashFile(filePath).Equals(expectedHash, System.StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string HashFileSha1(string filePath)
        {
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read);
            using SHA1 hasher = SHA1.Create();
            return CoreUtils.HexEncode(hasher.ComputeHash(fs));
        }
    }
}
