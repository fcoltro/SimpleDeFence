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

        internal Application? TryGetApp(ExecutableSubject fromSubject, out FirewallExceptionV3? fwex, bool matchSpecial)
        {
            foreach (var app in KnownApplications)
            {
                if (!matchSpecial && app.HasFlag("TWUI:Special"))
                    continue;

                foreach (var id in app.Components)
                {
                    if (id.DoesExecutableSatisfy(fromSubject))
                    {
                        fwex = id.InstantiateException(fromSubject);
                        return app;
                    }
                }
            }

            fwex = null;
            return null;
        }

        /// <summary>The prompt-free half of what was SimpleDeFence/DatabaseClasses/AppDatabase.cs's
        /// GetExceptionsForApp(subject, guiPrompt, out app) - moved here because this half has no
        /// WinForms dependency (unlike the guiPrompt=true path, which shows a
        /// Microsoft.Samples.TaskDialog prompt and stays in that WinForms-only partial as a thin
        /// wrapper around this method).</summary>
        public List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, out Application? app)
        {
            app = null;
            var exceptions = new List<FirewallExceptionV3>();

            if (fromSubject is AppContainerSubject)
            {
                exceptions.Add(new FirewallExceptionV3(fromSubject, new TcpUdpPolicy(true)));
                return exceptions;
            }
            else if (fromSubject is ExecutableSubject exeSubject)
            {
                app = TryGetApp(exeSubject, out _, false);
                if (app == null)
                {
                    exceptions.Add(new FirewallExceptionV3(exeSubject, new TcpUdpPolicy(true)));
                    return exceptions;
                }

                string? pathHint = System.IO.Path.GetDirectoryName(exeSubject.ExecutablePath);
                foreach (SubjectIdentity id in app.Components)
                {
                    List<ExceptionSubject> foundSubjects = id.SearchForFile(pathHint);
                    foreach (ExceptionSubject subject in foundSubjects)
                    {
                        var tmp = id.InstantiateException(subject);
                        if (fromSubject.Equals(subject))
                            exceptions.Insert(0, tmp);
                        else
                            exceptions.Add(tmp);
                    }
                }
            }
            else
            {
                throw new NotImplementedException();
            }

            return exceptions;
        }

        public JsonTypeInfo<AppDatabase> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.AppDatabase;
        }
    }
}
