namespace SimpleDeFence.Localization
{
    /// <summary>
    /// Typed names for every key in Strings.en.json.
    ///
    /// This is what buys back the compile-time safety a JSON-driven scheme normally gives up: call
    /// sites reference <c>LocKeys.Status.NotConnected</c> rather than a loose "status.notConnected"
    /// literal, so a typo fails the build instead of rendering a blank label. LocKeysTests asserts
    /// these constants and the JSON files never drift apart, which is what makes that guarantee
    /// hold as strings are added.
    /// </summary>
    public static class LocKeys
    {
        public static class App
        {
            public const string Name = "app.name";
        }

        public static class Nav
        {
            public const string Rules = "nav.rules";
            public const string ModeChip = "nav.modeChip";
        }

        public static class Common
        {
            public const string Ok = "common.ok";
            public const string Cancel = "common.cancel";
            public const string Refresh = "common.refresh";
            public const string Loading = "common.loading";
            public const string Connecting = "common.connecting";
            public const string Unknown = "common.unknown";
        }

        public static class Status
        {
            public const string NotConnected = "status.notConnected";
            public const string NotConnectedDetail = "status.notConnectedDetail";
            public const string Locked = "status.locked";
            public const string LockedDetail = "status.lockedDetail";
            public const string Connected = "status.connected";
        }

        public static class Mode
        {
            public const string NormalLabel = "mode.normal.label";
            public const string NormalDescription = "mode.normal.description";
            public const string BlockAllLabel = "mode.blockAll.label";
            public const string BlockAllDescription = "mode.blockAll.description";
            public const string AllowOutgoingLabel = "mode.allowOutgoing.label";
            public const string AllowOutgoingDescription = "mode.allowOutgoing.description";
            public const string DisabledLabel = "mode.disabled.label";
            public const string DisabledDescription = "mode.disabled.description";
            public const string LearningLabel = "mode.learning.label";
            public const string LearningDescription = "mode.learning.description";

            public const string SwitchFailedLockedTitle = "mode.switchFailed.lockedTitle";
            public const string SwitchFailedComErrorTitle = "mode.switchFailed.comErrorTitle";
            public const string SwitchFailedComErrorDetail = "mode.switchFailed.comErrorDetail";
            public const string SwitchFailedGenericTitle = "mode.switchFailed.genericTitle";
            public const string SwitchFailedGenericDetail = "mode.switchFailed.genericDetail";
            public const string SwitchFailedUnreachableTitle = "mode.switchFailed.unreachableTitle";

            public const string LearningConfirmTitle = "mode.learningConfirm.title";
            public const string LearningConfirmBody = "mode.learningConfirm.body";
            public const string LearningConfirmConfirm = "mode.learningConfirm.confirm";
        }

        public static class Applications
        {
            public const string Title = "applications.title";
            public const string FilterPlaceholder = "applications.filterPlaceholder";
            public const string Summary = "applications.summary";
            public const string SummaryOne = "applications.summaryOne";
            public const string Empty = "applications.empty";
        }

        public static class Connections
        {
            public const string Title = "connections.title";
            public const string SectionBlocked = "connections.section.blocked";
            public const string SectionConnected = "connections.section.connected";
            public const string SectionOpen = "connections.section.open";
            public const string SectionCount = "connections.section.count";
            public const string EmptyBlocked = "connections.empty.blocked";
            public const string EmptyConnected = "connections.empty.connected";
            public const string EmptyOpen = "connections.empty.open";
            public const string FilterPlaceholder = "connections.filterPlaceholder";
            public const string AutoRefresh = "connections.autoRefresh";
            public const string Allow = "connections.allow";
            public const string AllowSuccessTitle = "connections.allowSuccess.title";
            public const string AllowSuccessBody = "connections.allowSuccess.body";
            public const string AllowFailedTitle = "connections.allowFailed.title";
            public const string AllowFailedLockedDetail = "connections.allowFailed.lockedDetail";
        }

        public static class Subject
        {
            public const string Executable = "subject.executable";
            public const string Service = "subject.service";
            public const string UwpPackage = "subject.uwpPackage";
            public const string Global = "subject.global";
            public const string AllApplications = "subject.allApplications";
        }

        public static class Policy
        {
            public const string Blocked = "policy.blocked";
            public const string Unrestricted = "policy.unrestricted";
            public const string UnrestrictedLan = "policy.unrestrictedLan";
            public const string NoPorts = "policy.noPorts";
            public const string LanOnlySuffix = "policy.lanOnlySuffix";
            public const string CustomRuleOne = "policy.customRuleOne";
            public const string CustomRuleMany = "policy.customRuleMany";
            public const string TcpOut = "policy.tcpOut";
            public const string UdpOut = "policy.udpOut";
            public const string TcpIn = "policy.tcpIn";
            public const string UdpIn = "policy.udpIn";
            public const string AllPorts = "policy.allPorts";
        }
    }
}
