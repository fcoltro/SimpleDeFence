using System.Text.Json.Serialization;

namespace SimpleDeFence
{
    // Local-only types (client settings) that never cross the IPC wire, so they don't need to
    // live in SimpleDeFence.Core alongside the protocol/config types shared with the WinUI 3 GUI.
    // Kept in their own JsonSerializerContext for that reason. The known-apps database used to be
    // listed here too; it moved into Core's SourceGenerationContext with the DatabaseClasses split.
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Default,
        IgnoreReadOnlyFields = false,
        IgnoreReadOnlyProperties = false,
        IncludeFields = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        WriteIndented = true
        )]
    [JsonSerializable(typeof(ConfigContainer))]
    internal partial class AppSourceGenerationContext : JsonSerializerContext
    {
    }
}
