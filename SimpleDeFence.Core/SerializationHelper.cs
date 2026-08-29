using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    public interface ISerializable<T>
    {
        public JsonTypeInfo<T> GetJsonTypeInfo();
    }

    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Default,
        IgnoreReadOnlyFields = false,
        IgnoreReadOnlyProperties = false,
        IncludeFields = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        WriteIndented = true
        )]
    [JsonSerializable(typeof(TwMessage))]
    [JsonSerializable(typeof(TwMessageGetSettings))]
    [JsonSerializable(typeof(TwMessagePutSettings))]
    [JsonSerializable(typeof(TwMessageComError))]
    [JsonSerializable(typeof(TwMessageError))]
    [JsonSerializable(typeof(TwMessageLocked))]
    [JsonSerializable(typeof(TwMessageGetProcessPath))]
    [JsonSerializable(typeof(TwMessageReadFwLog))]
    [JsonSerializable(typeof(TwMessageIsLocked))]
    [JsonSerializable(typeof(TwMessageUnlock))]
    [JsonSerializable(typeof(TwMessageModeSwitch))]
    [JsonSerializable(typeof(TwMessageSetPassword))]
    [JsonSerializable(typeof(TwMessageSimple))]
    [JsonSerializable(typeof(TwMessageAddTempException))]
    [JsonSerializable(typeof(GlobalSubject))]
    [JsonSerializable(typeof(AppContainerSubject))]
    [JsonSerializable(typeof(ExecutableSubject))]
    [JsonSerializable(typeof(ServiceSubject))]
    [JsonSerializable(typeof(HardBlockPolicy))]
    [JsonSerializable(typeof(UnrestrictedPolicy))]
    [JsonSerializable(typeof(TcpUdpPolicy))]
    [JsonSerializable(typeof(RuleListPolicy))]
    [JsonSerializable(typeof(FirewallExceptionV3))]
    [JsonSerializable(typeof(ServerConfiguration))]
    [JsonSerializable(typeof(ClientSettings))]
    [JsonSerializable(typeof(ConfigExport))]
    [JsonSerializable(typeof(UpdateDescriptor))]
    [JsonSerializable(typeof(ServerState))]
    [JsonSerializable(typeof(DatabaseClasses.AppDatabase))]
    [JsonSerializable(typeof(DatabaseClasses.Application))]
    [JsonSerializable(typeof(DatabaseClasses.SubjectIdentity))]
    internal partial class SourceGenerationContext : JsonSerializerContext
    {
    }

    /// <summary>
    /// Which of the five endings <see cref="SerializationHelper.DeserializeFromEncryptedFile{T}"/>
    /// reached. Three of them hand back the default instance for reasons that are nothing like
    /// "there was no file": the configuration was there and the reader refused it. A caller that
    /// cannot tell those apart - and until this existed, none could - builds the firewall from a
    /// configuration nobody chose while reporting itself healthy.
    /// </summary>
    public enum ConfigLoadOutcome
    {
        /// <summary>Read under the current authenticated format.</summary>
        Loaded,

        /// <summary>Nothing on disk to read. A first run.</summary>
        Missing,

        /// <summary>A file is there but this process could not read its bytes - a sharing
        /// violation, or an ACL that no longer lets the service at its own config.</summary>
        Unreadable,

        /// <summary>Carries the current marker but failed its authentication tag: altered,
        /// truncated, or written under a different key.</summary>
        Unauthenticated,

        /// <summary>Marker-less, on an installation that has already migrated - i.e. a file that
        /// went backwards to the format whose key ships in every copy of the binary.</summary>
        DowngradeRefused,

        /// <summary>Read under one of the older formats and rewritten under the current one.</summary>
        Migrated,
    }

    public static class SerializationHelper
    {
        public static byte[] Serialize<T>(T obj) where T : ISerializable<T>
        {
            return JsonSerializer.SerializeToUtf8Bytes(obj, obj.GetJsonTypeInfo());
        }

        public static void Serialize<T>(Stream stream, T obj) where T : ISerializable<T>
        {
            JsonSerializer.Serialize(stream, obj, obj.GetJsonTypeInfo());
        }

        public static T Deserialize<T>(byte[] utf8bytes, T defInstance) where T : ISerializable<T>
        {
            return JsonSerializer.Deserialize(utf8bytes, defInstance.GetJsonTypeInfo()) ?? throw new NullResultExceptions(nameof(JsonSerializer.Deserialize));
        }

        public static T Deserialize<T>(Stream stream, T defInstance) where T : ISerializable<T>
        {
            return JsonSerializer.Deserialize(stream, defInstance.GetJsonTypeInfo()) ?? throw new NullResultExceptions(nameof(JsonSerializer.Deserialize));
        }

        public static void SerializeToPipe<T>(PipeStream pipe, T obj) where T : ISerializable<T>
        {
            // Pipe might be message-based, so we want to make sure the whole serialized object
            // gets written to the pipe in a single write. To ensure this, we serialize to a
            // byte-array first.

            var utf8Bytes = Serialize(obj);
            //string dbg = System.Text.Encoding.UTF8.GetString(utf8Bytes);
            pipe.Write(utf8Bytes, 0, utf8Bytes.Length);
            pipe.Flush();
        }

        public static T DeserializeFromPipe<T>(PipeStream pipe, int timeout_ms, T defInstance) where T : ISerializable<T>
        {
            bool pipeClosed = false;
            var buf = new byte[4 * 1024];

            using var memoryStream = new MemoryStream();
            using var readDone = new System.Threading.AutoResetEvent(false);

            do
            {
                int len = 0;
                var res = pipe.BeginRead(buf, 0, buf.Length, delegate (IAsyncResult r)
                {
                    try
                    {
                        len = pipe.EndRead(r);
                        if (len == 0)
                            pipeClosed = true;
                        readDone.Set();
                    }
                    catch { }
                }, null);

                if (!readDone.WaitOne(timeout_ms))
                    throw new TimeoutException("Timeout while waiting for answer from service.");

                if (pipeClosed)
                    throw new IOException("Pipe closed.");

                memoryStream.Write(buf, 0, len);
                timeout_ms = 1000;
            } while (!pipe.IsMessageComplete);

            memoryStream.Flush();
            memoryStream.Position = 0;

            //string dbg = System.Text.Encoding.UTF8.GetString(memoryStream.ToArray());
            return Deserialize(memoryStream, defInstance);
        }

        public static T DeserializeFromFile<T>(string filepath, T defInstance, bool readOnlySource = false) where  T : ISerializable<T>
        {
            try
            {
                using var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read);
                return Deserialize(stream, defInstance);
            }
            catch
            {
                // Try loading from old serialization format, and save in new format if allowed
                var xmlPath = filepath.EndsWith(".json") ? Path.ChangeExtension(filepath, ".xml") : filepath;
                var ret = LoadFromXMLFile<T>(xmlPath);
                if (!readOnlySource) SerializeToFile(ret, filepath);
                return ret;
            }
        }

        public static void SerializeToFile<T>(T obj, string filepath) where T : ISerializable<T>
        {
            using var fileUpdater = new AtomicFileUpdater(filepath);
            using (var stream = new FileStream(fileUpdater.TemporaryFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Serialize(stream, obj);
            }
            fileUpdater.Commit();
        }

        /// <summary>Reads a config written under the current AES-GCM scheme, falling back through
        /// the two older formats and rewriting whatever it finds under the current one. The
        /// fallbacks exist so that upgrading cannot cost a user their firewall rules; each is tried
        /// only when the one before it says "this is not my format".</summary>
        public static T DeserializeFromEncryptedFile<T>(string filepath, string key, string iv, T defInst) where T : ISerializable<T>
        {
            return DeserializeFromEncryptedFile(filepath, key, iv, defInst, out _);
        }

        /// <summary>The same read, reporting which ending it reached. See
        /// <see cref="ConfigLoadOutcome"/> for why the distinction is worth carrying: every branch
        /// below that returns <paramref name="defInst"/> looks identical to the caller otherwise,
        /// including the three that mean "a configuration is present and was refused".</summary>
        public static T DeserializeFromEncryptedFile<T>(string filepath, string key, string iv, T defInst, out ConfigLoadOutcome outcome) where T : ISerializable<T>
        {
            byte[] fileBytes;
            try
            {
                fileBytes = File.ReadAllBytes(filepath);
            }
            catch
            {
                outcome = File.Exists(filepath) ? ConfigLoadOutcome.Unreadable : ConfigLoadOutcome.Missing;
                return defInst;
            }

            // Current format.
            byte[] plaintext;
            if (ConfigProtection.TryUnprotect(fileBytes, filepath, out plaintext))
            {
                using var plainStream = new MemoryStream(plaintext, false);
                outcome = ConfigLoadOutcome.Loaded;
                return Deserialize<T>(plainStream, defInst);
            }

            // A file that carries the current marker but failed to authenticate is not a format we
            // should keep guessing about: it was altered, truncated, or written under a different
            // key. Falling through to the legacy readers would be pointless, and rewriting it would
            // destroy whatever is actually there, so leave it alone and start from defaults.
            if (ConfigProtection.HasMagic(fileBytes))
            {
                outcome = ConfigLoadOutcome.Unauthenticated;
                return defInst;
            }

            // No marker at all means a file from before the migration, and the only reader for it is
            // the legacy one below - AES-CBC under a key derived from a compile-time constant that
            // ships in every copy of this binary. A key everyone has authenticates nothing: anyone
            // who can write this file can author one the service loads as genuine, which is the
            // exact threat ConfigProtection exists to close. The attacker picks the format, so
            // leaving the weaker reader reachable leaves the whole scheme at the strength of the
            // scheme it replaced.
            //
            // The key file is what tells the two cases apart. Its presence means this installation
            // has already migrated, so a marker-less file is not an upgrade to read - it is a
            // downgrade to refuse, and the answer is the same as for a failed tag: start from
            // defaults and leave the file alone.
            if (File.Exists(ConfigProtection.KeyFilePath(filepath)))
            {
                outcome = ConfigLoadOutcome.DowngradeRefused;
                return defInst;
            }

            T ret;
            try
            {
                // Legacy AES-CBC under the old build's compile-time key.
                using var symmetricKey = Aes.Create();
                symmetricKey.Mode = CipherMode.CBC;
                symmetricKey.Key = Encoding.ASCII.GetBytes(key);
                symmetricKey.IV = Encoding.ASCII.GetBytes(iv);

                using var fs = new MemoryStream(fileBytes, false);
                using var cryptoStream = new CryptoStream(fs, symmetricKey.CreateDecryptor(), CryptoStreamMode.Read);
                ret = Deserialize<T>(cryptoStream, defInst);
            }
            catch
            {
                // Older still: the pre-3.0 XML shape.
                var xmlPath = filepath.EndsWith(".json") ? Path.ChangeExtension(filepath, ".xml") : filepath;
                ret = LoadFromEncryptedXMLFile<T>(xmlPath, key, iv);
            }

            // Re-save so the next read takes the authenticated path. Best-effort: a read must still
            // succeed on a read-only volume.
            try { SerializeToEncryptedFile(ret, filepath, key, iv); }
            catch { }

            outcome = ConfigLoadOutcome.Migrated;
            return ret;
        }

        /// <summary>Always writes the current AES-GCM format. The key/iv parameters are retained
        /// for the reader's legacy path and deliberately unused here - nothing should be written
        /// under the old compile-time key again.</summary>
        public static void SerializeToEncryptedFile<T>(T obj, string filePath, string key, string iv) where T : ISerializable<T>
        {
            byte[] plaintext;
            using (var buffer = new MemoryStream())
            {
                Serialize(buffer, obj);
                plaintext = buffer.ToArray();
            }

            var protectedBytes = ConfigProtection.Protect(plaintext, filePath);

            using var fileUpdater = new AtomicFileUpdater(filePath);
            File.WriteAllBytes(fileUpdater.TemporaryFilePath, protectedBytes);
            fileUpdater.Commit();
        }

        [Obsolete("XML serializer kept around for importing pre-3.0 configs.")]
        private static readonly Type[] KnownDataContractTypes =
        {
            typeof(BlockListSettings),
            typeof(ServerProfileConfiguration),
            typeof(ServerConfiguration),

            typeof(ExceptionSubject),
            typeof(GlobalSubject),
            typeof(ExecutableSubject),
            typeof(ServiceSubject),
            typeof(AppContainerSubject),

            typeof(ExceptionPolicy),
            typeof(HardBlockPolicy),
            typeof(UnrestrictedPolicy),
            typeof(TcpUdpPolicy),
            typeof(RuleListPolicy),

            typeof(RuleDef),
            typeof(List<RuleDef>),
            typeof(FirewallExceptionV3),

            typeof(UpdateModule),
            typeof(UpdateDescriptor),
        };
        
        [Obsolete("XML serializer kept around for importing pre-3.0 configs.")]
        public static T DeserializeDC<T>(Stream stream)
        {
            var serializer = new DataContractSerializer(typeof(T), KnownDataContractTypes);
            return ((T?)serializer.ReadObject(stream)) ?? throw new NullResultExceptions("DataContractSerializer.ReadObject()");
        }

        [Obsolete("XML serializer kept around for importing pre-3.0 configs.")]
        public static T LoadFromEncryptedXMLFile<T>(string filepath, string key, string iv)
        {
            // Construct encryptor
            using var symmetricKey = Aes.Create();
            symmetricKey.Mode = CipherMode.CBC;
            symmetricKey.Key = Encoding.ASCII.GetBytes(key);
            symmetricKey.IV = Encoding.ASCII.GetBytes(iv);

            // Decrypt
            using var fs = new FileStream(filepath, FileMode.Open, FileAccess.Read);
            using var cryptoStream = new CryptoStream(fs, symmetricKey.CreateDecryptor(), CryptoStreamMode.Read);
            return DeserializeDC<T>(cryptoStream);
        }

        [Obsolete("XML serializer kept around for importing pre-3.0 configs.")]
        public static T LoadFromXMLFile<T>(string filepath)
        {
            using var stream = new FileStream(filepath, FileMode.Open, FileAccess.Read);
            return DeserializeDC<T>(stream);
        }
    }
}
