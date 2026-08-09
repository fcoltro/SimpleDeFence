using System.ComponentModel;

namespace SimpleDeFence.Installer
{
    [RunInstaller(true)]
    public class SimpleDeFenceInstaller : System.Configuration.Install.Installer
    {
        public SimpleDeFenceInstaller()
        {
            this.Installers.Add(new SimpleDeFenceServiceInstaller());
        }
    }
}