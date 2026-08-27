using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    [DataContract(Namespace = "SimpleDeFence")]
    public class UpdateModule
    {
        [DataMember, AllowNull]
        public string Component;
        [DataMember]
        public string? ComponentVersion;
        [DataMember]
        public string? DownloadHash;
        [DataMember]
        public string? UpdateURL;
    }

    [DataContract(Namespace = "SimpleDeFence")]
    public class UpdateDescriptor : ISerializable<UpdateDescriptor>
    {
        public static readonly string ISTALLER_ARCH_SUFFIX = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            // Was "x86", carried over from TinyWall with the note "selects the 32-bit installer
            // even on x64, intentional as long as Win32 is supported". Nothing about that is true
            // here: SimpleDeFence.csproj hard-codes RuntimeIdentifier=win-x64, the only installer
            // the build produces is SimpleDeFence_x64.msi, and there is no 32-bit build to select.
            // The effect was that the one architecture this product actually ships for asked the
            // update server for a module named SimpleDeFence_x86, which nothing ever publishes -
            // so the updater could never find an update, on every machine, silently.
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException()
        };
        /// <summary>The module this running build asks the update server for.</summary>
        public static readonly string MODULE_NAME_MAINBIN = "SimpleDeFence_" + ISTALLER_ARCH_SUFFIX;

        /// <summary>The module names an update descriptor may publish. They live here, beside the
        /// suffix that consumes them, because they used to be typed as bare literals in
        /// DevelToolWindow while the consumer built its name from ISTALLER_ARCH_SUFFIX - and the
        /// two halves drifted apart without anything failing to compile.</summary>
        public const string MODULE_NAME_MAINBIN_X64 = "SimpleDeFence_x64";
        public const string MODULE_NAME_MAINBIN_ARM64 = "SimpleDeFence_arm64";

        public const string MODULE_NAME_HOSTS = "HostsFile";
        public const string MODULE_NAME_DATABASE = "Database";

        [DataMember]
        public string MagicWord = "SimpleDeFence Update Descriptor";
        [DataMember]
        public UpdateModule[] Modules = Array.Empty<UpdateModule>();

        public JsonTypeInfo<UpdateDescriptor> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.UpdateDescriptor;
        }

        public UpdateModule? GetModule(string moduleName)
        {
            for (int i = 0; i < Modules.Length; ++i)
            {
                if (Modules[i].Component.Equals(moduleName, StringComparison.InvariantCultureIgnoreCase))
                    return Modules[i];
            }

            return null;
        }
    }

    /// <summary>
    /// Ways the service can be running, and reporting a mode, while not actually enforcing what the
    /// configuration says. Every one of these was a failure the service used to swallow: a firewall
    /// that looks healthy in the UI and is not doing the job is the worst of the available outcomes,
    /// so each case is named here and travels to the client with the rest of the state.
    /// </summary>
    [Flags]
    public enum ServiceDegradation
    {
        None = 0,

        /// <summary>Startup did not finish. Whatever WFP was enforcing before is still in force,
        /// which may be an older configuration - or, on a first run, nothing.</summary>
        InitializationFailed = 1,

        /// <summary>The application database did not load, so blocklists and the named application
        /// profiles resolve to no rules at all.</summary>
        AppDatabaseUnavailable = 2,

        /// <summary>One or more filters were refused by WFP, so the installed rule set is not the
        /// one that was assembled.</summary>
        RulesIncomplete = 4,

        /// <summary>The hosts blocklist is switched on in the configuration but is not installed.</summary>
        HostsBlocklistUnavailable = 8,
    }

    public class ServerState : ISerializable<ServerState>
    {
        public bool HasPassword = false;
        public bool Locked = false;
        public UpdateDescriptor? Update = null;
        public FirewallMode Mode = FirewallMode.Unknown;
        public ServiceDegradation Degraded = ServiceDegradation.None;
        public List<MessageType> ClientNotifs = new();

        public JsonTypeInfo<ServerState> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ServerState;
        }
    }
}
