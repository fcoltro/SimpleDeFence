using System.Text.Json.Serialization;

namespace SimpleDeFence.DatabaseClasses
{
    public partial class Application
    {
        // WinForms-only: resolves the display name through the app's resx resources. The WinUI
        // GUI uses Loc instead, so this member stays on this side of the split.
        [JsonIgnore]
        public string LocalizedName
        {
            get
            {
                try
                {
                    string ret = Resources.Exceptions.ResourceManager.GetString(Name);
                    return string.IsNullOrEmpty(ret) ? Name : ret;
                }
                catch
                {
                    return Name;
                }
            }
        }
    }
}

