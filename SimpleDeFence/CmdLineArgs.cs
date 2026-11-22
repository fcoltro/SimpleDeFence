
namespace SimpleDeFence
{
    internal enum StartUpMode
    {
        Invalid,
        DevelTool,
        Service,
        Controller,
        SelfHosted,
        Install,
        Uninstall
    }

    internal class CmdLineArgs
    {
        internal bool autowhitelist = false;
        internal bool updatenow = false;
        internal bool startup = false;

        /// <summary>/uninstall /upgrade: the installer is replacing this version with a newer one,
        /// not removing SimpleDeFence. See SimpleDeFenceDoctor.Uninstall.</summary>
        internal bool upgrade = false;

        internal StartUpMode ProgramMode = StartUpMode.Invalid;
    }
}
