using System.ComponentModel;

namespace pylorak.SimpleDeFence.Installer
{
    [RunInstaller(true)]
    public class TinyWallInstaller : System.Configuration.Install.Installer
    {
        public TinyWallInstaller()
        {
            this.Installers.Add(new TinyWallServiceInstaller());
        }
    }
}