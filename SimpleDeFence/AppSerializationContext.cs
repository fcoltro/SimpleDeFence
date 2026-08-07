using System.Text.Json.Serialization;

namespace SimpleDeFence
{
    // Local-only types (client settings, the known-apps database) that never cross the IPC
    // wire, so they don't need to live in SimpleDeFence.Core alongside the protocol/config
    // types shared with the WinUI 3 GUI. Kept in their own JsonSerializerContext for that reason.
    [JsonSourceGenerationOptions(
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Default,
        IgnoreReadOnlyFields = false,
        IgnoreReadOnlyProperties = false,
        IncludeFields = true,
        PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
        WriteIndented = true
        )]
    [JsonSerializable(typeof(ControllerSettings))]
    [JsonSerializable(typeof(ConfigContainer))]
    [JsonSerializable(typeof(DatabaseClasses.SubjectIdentity))]
    [JsonSerializable(typeof(DatabaseClasses.Application))]
    [JsonSerializable(typeof(DatabaseClasses.AppDatabase))]
    internal partial class AppSourceGenerationContext : JsonSerializerContext
    {
    }
}
