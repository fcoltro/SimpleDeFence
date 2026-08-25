using System;
using System.Security.Cryptography;
using System.Text;

namespace SimpleDeFence.Utilities
{
    /// <summary>
    /// Password hashing for the configuration lock.
    ///
    /// Stored hashes are self-describing: "algorithm;salt;iterations;length;hash". The algorithm
    /// field is what allows the parameters to be raised later without invalidating passwords set
    /// by an older build - <see cref="CompareHash"/> verifies against whatever the stored record
    /// says it used, while <see cref="GetHashForStorage"/> always writes the current scheme.
    ///
    /// </summary>
    public static class Pbkdf2
    {
        /// <summary>Current scheme. The legacy marker is the bare "Rfc2898", which meant
        /// PBKDF2-HMAC-SHA1 with the salt stored as raw text.</summary>
        private const string AlgorithmSha256 = "Rfc2898-SHA256";
        private const string AlgorithmLegacySha1 = "Rfc2898";

        /// <summary>OWASP's current PBKDF2-HMAC-SHA256 floor. The previous scheme used 150,000
        /// iterations of HMAC-SHA1, which was reasonable when it was written and is now well below
        /// what a password guarding a firewall's configuration should cost to attack.</summary>
        public const int DefaultIterations = 600_000;

        /// <summary>32 bytes, matching the SHA-256 output rather than truncating it to the 16 the
        /// old scheme stored.</summary>
        public const int DefaultHashBytes = 32;

        /// <summary>16 bytes of CSPRNG output. The old salt was 8 characters from
        /// Utils.RandomString, which draws on System.Random - a clock-seeded, non-cryptographic
        /// PRNG whose entire output is reproducible from a seed an attacker can narrow down to the
        /// moment the password was set. A salt that can be guessed is barely a salt.</summary>
        public const int DefaultSaltBytes = 16;

        /// <summary>Derives raw key bytes. Retained because callers other than the password lock
        /// use PBKDF2 as a key-derivation function rather than for storage.</summary>
        public static string GetHash(string text, string salt, int iterations, int numBytes)
        {
            var saltBytes = Encoding.UTF8.GetBytes(salt);
            return Convert.ToBase64String(Derive(text, saltBytes, iterations, numBytes, HashAlgorithmName.SHA256));
        }

        /// <summary>Hashes a password for storage under the current scheme, generating a fresh
        /// random salt. The salt is stored Base64-encoded because it is now raw bytes rather than
        /// printable characters.</summary>
        public static string GetHashForStorage(string text)
        {
            var saltBytes = RandomNumberGenerator.GetBytes(DefaultSaltBytes);
            var hash = Derive(text, saltBytes, DefaultIterations, DefaultHashBytes, HashAlgorithmName.SHA256);
            return string.Format("{0};{1};{2};{3};{4}",
                AlgorithmSha256,
                Convert.ToBase64String(saltBytes),
                DefaultIterations,
                DefaultHashBytes,
                Convert.ToBase64String(hash));
        }

        /// <summary>Verifies a password against a stored record of either scheme.
        ///
        /// The comparison is over raw bytes in constant time. The previous implementation re-derived
        /// the whole record and compared it with string.Equals(OrdinalIgnoreCase), which both leaked
        /// timing on a secret-dependent value and - because Base64 is case-sensitive - was comparing
        /// two encodings more loosely than the bytes they stand for.</summary>
        public static bool CompareHash(string storedHash, string text)
        {
            if (string.IsNullOrEmpty(storedHash))
                return false;

            var elems = storedHash.Split(';');
            if (elems.Length < 5)
                return false;

            var algorithm = elems[0];
            byte[] saltBytes;
            HashAlgorithmName hashName;

            if (algorithm == AlgorithmSha256)
            {
                hashName = HashAlgorithmName.SHA256;
                try { saltBytes = Convert.FromBase64String(elems[1]); }
                catch (FormatException) { return false; }
            }
            else if (algorithm == AlgorithmLegacySha1)
            {
                // Pre-existing passwords: HMAC-SHA1 over a salt that was stored as raw text.
                hashName = HashAlgorithmName.SHA1;
                saltBytes = Encoding.UTF8.GetBytes(elems[1]);
            }
            else
            {
                return false;
            }

            int iterations, numBytes;
            if (!int.TryParse(elems[2], out iterations) || !int.TryParse(elems[3], out numBytes))
                return false;
            if (iterations <= 0 || numBytes <= 0 || numBytes > 1024)
                return false;

            byte[] expected;
            try { expected = Convert.FromBase64String(elems[4]); }
            catch (FormatException) { return false; }

            var actual = Derive(text, saltBytes, iterations, numBytes, hashName);

            // Constant-time over the raw bytes. The previous implementation re-derived the entire
            // record and compared it with string.Equals(OrdinalIgnoreCase), which leaked timing on
            // a secret-dependent value and, Base64 being case-sensitive, compared two encodings
            // more loosely than the bytes they stand for.
            return FixedTimeEquals(actual, expected);
        }

        /// <summary>True when a stored record was written by an older scheme and should be
        /// rewritten. Callers verify first and rehash only on success, which is the one moment the
        /// plaintext is available to do it with.</summary>
        public static bool NeedsUpgrade(string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash))
                return false;

            var elems = storedHash.Split(';');
            if (elems.Length < 5)
                return false;

            if (elems[0] != AlgorithmSha256)
                return true;

            int iterations;
            return !int.TryParse(elems[2], out iterations) || iterations < DefaultIterations;
        }

        private static byte[] Derive(string text, byte[] saltBytes, int iterations, int numBytes, HashAlgorithmName hashName)
            => Rfc2898DeriveBytes.Pbkdf2(text, saltBytes, iterations, hashName, numBytes);

        private static bool FixedTimeEquals(byte[] left, byte[] right)
            => CryptographicOperations.FixedTimeEquals(left, right);
    }
}
