using System;
using System.IO;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Runtime.Serialization;
using SimpleDeFence.Utilities;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    public static class PasswordLock
    {
        internal static string PasswordFilePath { get; } = Path.Combine(Utils.AppDataPath, "pwd");

        private static bool _Locked;

        internal static bool Locked
        {
            get { return _Locked && HasPassword; }
            set
            {
                if (value && HasPassword)
                    _Locked = true;
            }
        }

        internal static void SetPass(string password)
        {
            // Construct file path
            string SettingsFile = PasswordFilePath;

            if (password == string.Empty)
                // If we have no password, delete password explicitly
                File.Delete(SettingsFile);
            else
            {
                // Salt and parameters come from Pbkdf2 now. The salt used to be
                // Utils.RandomString(8), which draws on System.Random - a clock-seeded,
                // non-cryptographic PRNG - so the salt guarding this password was reproducible by
                // anyone who could guess within a window when it was set.
                WriteHash(Pbkdf2.GetHashForStorage(password));
            }
        }

        internal static bool Unlock(string password)
        {
            if (!HasPassword)
                return true;

            try
            {
                string storedHash = System.IO.File.ReadAllText(PasswordFilePath, System.Text.Encoding.UTF8);
                _Locked = !Pbkdf2.CompareHash(storedHash, password);
                StoredHashNeedsUpgrade = !_Locked && Pbkdf2.NeedsUpgrade(storedHash);
            }
            catch { }

            return !_Locked;
        }

        /// <summary>Set by <see cref="Unlock"/> when the password just verified against a record
        /// written by an older hashing scheme. Rewriting it is not done here: the service holds the
        /// password file open through its FileLocker, so the write has to be sequenced by the
        /// caller that owns that lock - exactly as SET_PASSPHRASE already does.</summary>
        internal static bool StoredHashNeedsUpgrade { get; private set; }

        /// <summary>Rewrites the stored record under the current scheme. Only meaningful straight
        /// after a successful <see cref="Unlock"/>, which is the one moment the plaintext exists to
        /// rehash with.</summary>
        internal static void UpgradeStoredHash(string password)
        {
            WriteHash(Pbkdf2.GetHashForStorage(password));
            StoredHashNeedsUpgrade = false;
        }

        private static void WriteHash(string hash)
        {
            using var fileUpdater = new AtomicFileUpdater(PasswordFilePath);
            File.WriteAllText(fileUpdater.TemporaryFilePath, hash, Encoding.UTF8);
            fileUpdater.Commit();
        }

        internal static bool HasPassword
        {
            get
            {
                if (!File.Exists(PasswordFilePath))
                    return false;

                var fi = new FileInfo(PasswordFilePath);
                return (fi.Length != 0);
            }
        }
    }

    [DataContract(Namespace = "http://schemas.datacontract.org/2004/07/PKSoft")]
     public sealed class ConfigContainer : ISerializable<ConfigContainer>
    {
        [DataMember(EmitDefaultValue = false)]
        public ServerConfiguration Service;

        public ConfigContainer()
        {
            Service = new ServerConfiguration();
        }

        public JsonTypeInfo<ConfigContainer> GetJsonTypeInfo()
        {
            return AppSourceGenerationContext.Default.ConfigContainer;
        }
    }

    internal static class ActiveConfig
    {
        [AllowNull]
        internal static ServerConfiguration Service = null;

        /*
        internal static ConfigContainer ToContainer()
        {
            ConfigContainer c = new ConfigContainer();
            c.Controller = ActiveConfig.Controller;
            c.Service = ActiveConfig.Service;
            return c;
        }*/
    }
}



