using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence.DatabaseClasses
{
    [DataContract(Namespace = "SimpleDeFence")]
    public partial class AppDatabase : ISerializable<AppDatabase>
    {
        [DataMember(Name = "KnownApplications")]
        private readonly List<Application> _KnownApplications;

        public static AppDatabase Load(string filePath)
        {
            return SerializationHelper.DeserializeFromFile(filePath, new AppDatabase());
        }

        public void Save(string filePath)
        {
            SerializationHelper.SerializeToFile(this, filePath);
        }

        [JsonConstructor]
        public AppDatabase(List<Application> knownApplications)
        {
            _KnownApplications = knownApplications;
        }

        public AppDatabase() :
            this(new List<Application>())
        { }

        public List<Application> KnownApplications
        {
            get { return _KnownApplications; }
        }

        public Application? GetApplicationByName(string name)
        {
            foreach (Application app in _KnownApplications)
            {
                if (app.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase))
                    return app;
            }

            return null;
        }

        public List<FirewallExceptionV3> FastSearchMachineForKnownApps()
        {
            var ret = new List<FirewallExceptionV3>();

            foreach (DatabaseClasses.Application app in KnownApplications)
            {
                if (app.HasFlag("TWUI:Special"))
                    continue;

                foreach (SubjectIdentity id in app.Components)
                {
                    List<ExceptionSubject> subjects = id.SearchForFile();
                    foreach (var subject in subjects)
                    {
                        ret.Add(id.InstantiateException(subject));
                    }
                }
            }

            return ret;
        }

        public JsonTypeInfo<AppDatabase> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.AppDatabase;
        }
    }
}
