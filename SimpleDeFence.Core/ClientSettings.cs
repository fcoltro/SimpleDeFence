using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>
    /// WinUI's own local, per-user preferences - kept deliberately separate from
    /// SimpleDeFence.Settings.ControllerSettings (the WinForms GUI's equivalent). See the Settings
    /// plan's "why this plan does NOT touch SimpleDeFence/Settings.cs" note for why: sharing that
    /// class would either drag a System.Windows.Forms/System.Drawing dependency into Core for
    /// fields WinUI has no use for, or - if WinUI wrote only a subset of fields back to the same
    /// file WinForms uses - silently discard whatever WinForms-only preferences were already
    /// there. Reconciling the two is deferred to the eventual net10 exe-merge migration.
    /// </summary>
    [DataContract(Namespace = "SimpleDeFence")]
    public sealed class ClientSettings : ISerializable<ClientSettings>
    {
        [DataMember(EmitDefaultValue = false)]
        public string UiTheme { get; set; } = "auto";

        [DataMember(EmitDefaultValue = false)]
        public string Language { get; set; } = "auto";

        [DataMember(EmitDefaultValue = false)]
        public bool AskForExceptionDetails { get; set; } = false;

        [DataMember(EmitDefaultValue = false)]
        public bool EnableGlobalHotkeys { get; set; } = true;

        private static string FilePath
        {
            get
            {
#if DEBUG
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!;
#else
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleDeFence");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
#endif
                return Path.Combine(dir, "UIConfig");
            }
        }

        public static ClientSettings Load()
        {
            try
            {
                return SerializationHelper.DeserializeFromFile(FilePath, new ClientSettings());
            }
            catch
            {
                // First run (no file yet) or a corrupt/unreadable file are both a normal state,
                // not an error - the same defensive convention ControllerSettings.Load() already
                // uses. A fresh default instance (theme "auto") is a safe, honest fallback.
                return new ClientSettings();
            }
        }

        public void Save()
        {
            try
            {
                SerializationHelper.SerializeToFile(this, FilePath);
            }
            catch
            {
                // Best-effort persistence, matching ControllerSettings.Save()'s existing
                // convention - a failed save (e.g. AppData briefly locked) must not crash the
                // Settings page or block the in-memory theme change from applying.
            }
        }

        public JsonTypeInfo<ClientSettings> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ClientSettings;
        }
    }
}
