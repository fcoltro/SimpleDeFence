namespace SimpleDeFence
{
    // Constants from the Windows Firewall COM API (netfw.h), previously supplied by the
    // NetFwTypeLib <COMReference>. That reference had to go: ResolveComReference only runs under
    // .NET Framework MSBuild, which in turn cannot build a .NET 10 target, so the project could
    // not be migrated while it remained. The objects are IDispatch-scriptable, so the call sites
    // bind late instead - which also avoids hand-transcribing interface GUIDs and vtable layouts,
    // where a single mistake corrupts memory rather than failing cleanly.

    internal static class NetFwAction
    {
        public const int Block = 0;
        public const int Allow = 1;
    }

    internal static class NetFwRuleDirection
    {
        public const int In = 1;
        public const int Out = 2;
    }

    internal static class NetFwProfileType2
    {
        public const int Domain = 0x1;
        public const int Private = 0x2;
        public const int Public = 0x4;
    }
}
