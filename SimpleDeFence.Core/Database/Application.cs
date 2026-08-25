using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence.DatabaseClasses
{
    [DataContract(Namespace = "SimpleDeFence")]
    public partial class Application : ISerializable<Application>
    {
        // Application name
        [DataMember(EmitDefaultValue = false)]
        public string Name { get; set; } = string.Empty;

        // Executables that belong to this application
        [DataMember(EmitDefaultValue = false)]
        public List<SubjectIdentity> Components { get; set; } = new List<SubjectIdentity>();

        public override string ToString()
        {
            return this.Name;
        }

        [DataMember(Name = "Flags", EmitDefaultValue = false)]
        public Dictionary<string, string?>? Flags { get; set; } = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        public bool HasFlag(string flag)
        {
            if (Flags == null)
                return false;

            return Flags.ContainsKey(flag.ToUpperInvariant());
        }

        [OnDeserialized()]
        internal void OnDeserializedMethod(StreamingContext context)
        {
            Components ??= new List<SubjectIdentity>();
            Flags ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        public JsonTypeInfo<Application> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.Application;
        }
    }
}
