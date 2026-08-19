using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    /// <summary>
    /// At-rest protection for the service configuration.
    ///
    /// What this replaces: AES-CBC under a key derived from a compile-time constant, with a
    /// constant IV, both sitting in the shipped binary. A key everyone has is not a key - it gave
    /// no confidentiality (anyone could decrypt any installation's config) and, worse for a
    /// firewall, no integrity: anyone could *author* a config file that the service would load as
    /// authentic, which is a supported way to turn the firewall off or whitelist something.
    ///
    /// What it is now: AES-GCM under a 256-bit key generated per installation, so the ciphertext
    /// is both secret and tamper-evident - GCM's tag makes a modified or forged file fail to
    /// decrypt rather than quietly load. The key is kept next to the config, wrapped with DPAPI at
    /// machine scope.
    ///
    /// The honest limit of that: DPAPI machine scope binds the key to this machine, which defeats
    /// copying the config off it, but it does not defeat an attacker who already has
    /// administrator/SYSTEM on the running machine or a full offline image of it - they can unwrap
    /// it the same way the service does. For a service that must read its own config unattended at
    /// boot, with no user secret available, that is the ceiling; the alternative would be
    /// prompting for a passphrase before the firewall can start.
    /// </summary>
    public static class ConfigProtection
    {
        /// <summary>File marker. Its absence is how <see cref="TryUnprotect"/> recognises a config
        /// written by the old scheme and hands it to the legacy reader instead.</summary>
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SDFC1");

        private const int KeySize = 32;   // AES-256
        private const int NonceSize = 12; // 96 bits, the size AES-GCM is specified around
        private const int TagSize = 16;

        public static string KeyFilePath(string configFilePath) => configFilePath + ".key";

        /// <summary>Encrypts with a fresh random nonce. A nonce must never repeat under one key,
        /// which is exactly the mistake the old scheme's constant IV made.</summary>
        public static byte[] Protect(byte[] plaintext, string configFilePath)
        {
            var key = LoadOrCreateKey(configFilePath);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using (var gcm = new AesGcm(key, TagSize))
            {
                gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            }

            var output = new byte[Magic.Length + NonceSize + TagSize + ciphertext.Length];
            Buffer.BlockCopy(Magic, 0, output, 0, Magic.Length);
            Buffer.BlockCopy(nonce, 0, output, Magic.Length, NonceSize);
            Buffer.BlockCopy(tag, 0, output, Magic.Length + NonceSize, TagSize);
            Buffer.BlockCopy(ciphertext, 0, output, Magic.Length + NonceSize + TagSize, ciphertext.Length);
            return output;
        }

        /// <summary>Returns false - rather than throwing - both for a file written by the old
        /// scheme and for one that fails its authentication tag, so the caller can fall back to the
        /// legacy reader in the first case and refuse the file in the second.</summary>
        public static bool TryUnprotect(byte[] fileBytes, string configFilePath, out byte[] plaintext)
        {
            plaintext = Array.Empty<byte>();

            if (!HasMagic(fileBytes))
                return false;
            if (fileBytes.Length < Magic.Length + NonceSize + TagSize)
                return false;
            if (!File.Exists(KeyFilePath(configFilePath)))
                return false;

            try
            {
                var key = LoadOrCreateKey(configFilePath);

                var nonce = new byte[NonceSize];
                var tag = new byte[TagSize];
                var ciphertext = new byte[fileBytes.Length - Magic.Length - NonceSize - TagSize];
                Buffer.BlockCopy(fileBytes, Magic.Length, nonce, 0, NonceSize);
                Buffer.BlockCopy(fileBytes, Magic.Length + NonceSize, tag, 0, TagSize);
                Buffer.BlockCopy(fileBytes, Magic.Length + NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

                var buffer = new byte[ciphertext.Length];
                using (var gcm = new AesGcm(key, TagSize))
                {
                    // Throws CryptographicException if the tag does not verify - i.e. the file was
                    // altered, truncated, or produced under a different key.
                    gcm.Decrypt(nonce, ciphertext, tag, buffer);
                }

                plaintext = buffer;
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public static bool HasMagic(byte[] fileBytes)
        {
            if (fileBytes == null || fileBytes.Length < Magic.Length)
                return false;

            for (int i = 0; i < Magic.Length; i++)
            {
                if (fileBytes[i] != Magic[i])
                    return false;
            }
            return true;
        }

        /// <summary>Reads the installation's key, creating one on first use. Written through
        /// AtomicFileUpdater so an interrupted first run cannot leave a truncated key behind, which
        /// would render an otherwise good config permanently unreadable.</summary>
        private static byte[] LoadOrCreateKey(string configFilePath)
        {
            var keyPath = KeyFilePath(configFilePath);

            if (File.Exists(keyPath))
            {
                var wrapped = File.ReadAllBytes(keyPath);
                if (wrapped.Length > 0)
                    return ProtectedData.Unprotect(wrapped, null, DataProtectionScope.LocalMachine);
            }

            var key = RandomNumberGenerator.GetBytes(KeySize);
            var toStore = ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine);

            var dir = Path.GetDirectoryName(keyPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using (var updater = new AtomicFileUpdater(keyPath))
            {
                File.WriteAllBytes(updater.TemporaryFilePath, toStore);
                updater.Commit();
            }

            return key;
        }
    }
}
