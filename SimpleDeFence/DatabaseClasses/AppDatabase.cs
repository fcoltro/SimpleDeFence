using System.Collections.Generic;

namespace SimpleDeFence.DatabaseClasses
{
    public partial class AppDatabase
    {
        public static string DBPath
        {
            get { return System.IO.Path.Combine(Utils.AppDataPath, "profiles.json"); }
        }

        public static AppDatabase Load()
        {
            return Load(DBPath);
        }

        /// <summary>Thin wrapper kept for the service's call site, which passes guiPrompt: false.
        ///
        /// The guiPrompt: true branch used to raise a native TaskDialog offering "unblock all
        /// related / only this one / cancel" when a database entry expanded to several exceptions.
        /// Nothing had called it with true since the WinForms GUI was deleted - the WinUI Rules
        /// page goes through SimpleDeFence.Core's two-argument overload - so it went with the
        /// TaskDialog wrapper it was the last consumer of. If that choice is wanted back, it
        /// belongs on RulesPage as a ContentDialog, not here.</summary>
        internal List<FirewallExceptionV3> GetExceptionsForApp(ExceptionSubject fromSubject, bool guiPrompt, out Application? app)
        {
            return GetExceptionsForApp(fromSubject, out app);
        }
    }
}
