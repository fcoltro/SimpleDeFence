using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>
    /// The Import/Export (.tws) wire shape for the WinUI GUI: {Service, Controller}, matching
    /// SimpleDeFence.ConfigContainer's (WinForms) shape field-for-field so files exported by
    /// either GUI import cleanly into the other. A distinct type, not a reuse of ConfigContainer:
    /// SimpleDeFence.Core's sources are glob-compiled into the WinForms project, and a Core type
    /// literally named ConfigContainer would collide with the existing WinForms-only class of that
    /// name in the same namespace.
    /// </summary>
    [DataContract(Namespace = "SimpleDeFence")]
    public sealed class ConfigExport : ISerializable<ConfigExport>
    {
        [DataMember(EmitDefaultValue = false)]
        public ServerConfiguration Service { get; set; } = new ServerConfiguration();

        [DataMember(EmitDefaultValue = false)]
        public ClientSettings Controller { get; set; } = new ClientSettings();

        public JsonTypeInfo<ConfigExport> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ConfigExport;
        }
    }
}
