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
            public const string Settings = "nav.settings";
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
            public const string AnotherDialogOpenDetail = "common.anotherDialogOpenDetail";
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
            public const string EmptyFiltered = "connections.empty.filtered";
            public const string FilterPlaceholder = "connections.filterPlaceholder";
            public const string AutoRefresh = "connections.autoRefresh";
            public const string Allow = "connections.allow";
            public const string AllowSuccessTitle = "connections.allowSuccess.title";
            public const string AllowSuccessBody = "connections.allowSuccess.body";
            public const string AllowFailedTitle = "connections.allowFailed.title";
            public const string AllowFailedLockedDetail = "connections.allowFailed.lockedDetail";
            public const string AllowFailedStaleDetail = "connections.allowFailed.staleDetail";
            public const string AllowFailedGenericDetail = "connections.allowFailed.genericDetail";
            public const string AllowFailedUnidentified = "connections.allowFailed.unidentified";
            public const string GatherFailedTitle = "connections.gatherFailed.title";
        }

        public static class Subject
        {
            public const string Executable = "subject.executable";
            public const string Service = "subject.service";
            public const string UwpPackage = "subject.uwpPackage";
            public const string Global = "subject.global";
            public const string AllApplications = "subject.allApplications";
        }

        public static class Rules
        {
            public const string SpecialKind = "rules.specialKind";
            public const string SpecialPolicy = "rules.specialPolicy";

            public const string Title = "rules.title";
            public const string SectionApplications = "rules.section.applications";
            public const string SectionSpecialRecommended = "rules.section.specialRecommended";
            public const string SectionSpecialOptional = "rules.section.specialOptional";
            public const string FilterPlaceholder = "rules.filterPlaceholder";
            public const string EmptyApplications = "rules.empty.applications";
            public const string EmptySpecial = "rules.empty.special";
            public const string EmptyFiltered = "rules.empty.filtered";
            public const string Remove = "rules.remove";
            public const string RemoveConfirmTitle = "rules.removeConfirm.title";
            public const string RemoveConfirmBody = "rules.removeConfirm.body";
            public const string RemoveSuccessTitle = "rules.removeSuccess.title";
            public const string RemoveSuccessBody = "rules.removeSuccess.body";
            public const string RemoveFailedTitle = "rules.removeFailed.title";
            public const string RemoveFailedLockedDetail = "rules.removeFailed.lockedDetail";
            public const string RemoveFailedStaleDetail = "rules.removeFailed.staleDetail";
            public const string RemoveFailedGenericDetail = "rules.removeFailed.genericDetail";
            public const string SpecialToggleFailedTitle = "rules.specialToggleFailed.title";
            public const string SpecialToggleFailedLockedDetail = "rules.specialToggleFailed.lockedDetail";
            public const string SpecialToggleFailedStaleDetail = "rules.specialToggleFailed.staleDetail";
            public const string SpecialToggleFailedGenericDetail = "rules.specialToggleFailed.genericDetail";
            public const string Add = "rules.add";
            public const string AddPickExecutable = "rules.addPickExecutable";
            public const string AddPickProcess = "rules.addPickProcess";
            public const string AddPickWindow = "rules.addPickWindow";
            public const string AddPickUwp = "rules.addPickUwp";
            public const string PickUwpTitle = "rules.pickUwpTitle";
            public const string PickProcessTitle = "rules.pickProcessTitle";
            public const string PickWindowTitle = "rules.pickWindowTitle";
            public const string PickEmpty = "rules.pickEmpty";

            public const string DetailTitle = "rules.detail.title";
            public const string DetailPolicyBlocked = "rules.detail.policyBlocked";
            public const string DetailPolicyUnrestricted = "rules.detail.policyUnrestricted";
            public const string DetailPolicyUnrestrictedLan = "rules.detail.policyUnrestrictedLan";
            public const string DetailPolicyTcpUdp = "rules.detail.policyTcpUdp";
            public const string DetailTcpOut = "rules.detail.tcpOut";
            public const string DetailUdpOut = "rules.detail.udpOut";
            public const string DetailTcpIn = "rules.detail.tcpIn";
            public const string DetailUdpIn = "rules.detail.udpIn";
            public const string DetailLanOnly = "rules.detail.lanOnly";
            public const string DetailApply = "rules.detail.apply";
            public const string DetailCustomRulesReadOnly = "rules.detail.customRulesReadOnly";
            public const string DetailApplySuccess = "rules.detail.applySuccess";
            public const string DetailApplyFailedTitle = "rules.detail.applyFailed.title";
            public const string DetailApplyFailedLockedDetail = "rules.detail.applyFailed.lockedDetail";
            public const string DetailApplyFailedStaleDetail = "rules.detail.applyFailed.staleDetail";
            public const string DetailApplyFailedGenericDetail = "rules.detail.applyFailed.genericDetail";
        }

        public static class Settings
        {
            public const string Title = "settings.title";
            public const string SectionGeneral = "settings.section.general";
            public const string SectionProtection = "settings.section.protection";
            public const string SectionBlocklists = "settings.section.blocklists";
            public const string SectionSecurity = "settings.section.security";
            public const string SectionUpdates = "settings.section.updates";
            public const string SectionMaintenance = "settings.section.maintenance";
            public const string SectionAbout = "settings.section.about";

            public const string CommitFailedTitle = "settings.commitFailed.title";
            public const string CommitFailedLockedDetail = "settings.commitFailed.lockedDetail";
            public const string CommitFailedStaleDetail = "settings.commitFailed.staleDetail";
            public const string CommitFailedGenericDetail = "settings.commitFailed.genericDetail";

            public const string GeneralTheme = "settings.general.theme";
            public const string GeneralThemeDescription = "settings.general.themeDescription";
            public const string GeneralThemeAuto = "settings.general.themeAuto";
            public const string GeneralThemeLight = "settings.general.themeLight";
            public const string GeneralThemeDark = "settings.general.themeDark";

            public const string ProtectionAllowLocalSubnet = "settings.protection.allowLocalSubnet";
            public const string ProtectionAllowLocalSubnetDescription = "settings.protection.allowLocalSubnetDescription";
            public const string ProtectionDisplayOffBlock = "settings.protection.displayOffBlock";
            public const string ProtectionDisplayOffBlockDescription = "settings.protection.displayOffBlockDescription";

            public const string BlocklistsEnable = "settings.blocklists.enable";
            public const string BlocklistsEnableDescription = "settings.blocklists.enableDescription";
            public const string BlocklistsHosts = "settings.blocklists.hosts";
            public const string BlocklistsPorts = "settings.blocklists.ports";

            public const string SecurityLockHostsFile = "settings.security.lockHostsFile";
            public const string SecurityLockHostsFileDescription = "settings.security.lockHostsFileDescription";

            public const string SecurityPasswordSet = "settings.security.passwordSet";
            public const string SecurityPasswordNotSet = "settings.security.passwordNotSet";
            public const string SecurityNewPassword = "settings.security.newPassword";
            public const string SecurityConfirmPassword = "settings.security.confirmPassword";
            public const string SecuritySetPassword = "settings.security.setPassword";
            public const string SecurityRemovePassword = "settings.security.removePassword";
            public const string SecurityPasswordMismatchTitle = "settings.security.passwordMismatch.title";
            public const string SecurityPasswordMismatchDetail = "settings.security.passwordMismatch.detail";
            public const string SecurityPasswordUpdatedTitle = "settings.security.passwordUpdated.title";
            public const string SecurityPasswordUpdatedBody = "settings.security.passwordUpdated.body";
            public const string SecurityPasswordRemovedBody = "settings.security.passwordRemoved.body";
            public const string SecurityPasswordUpdateFailedTitle = "settings.security.passwordUpdateFailed.title";

            public const string SecurityLockedStatus = "settings.security.lockedStatus";
            public const string SecurityUnlockedStatus = "settings.security.unlockedStatus";
            public const string SecurityLockNow = "settings.security.lockNow";
            public const string SecurityLockFailedTitle = "settings.security.lockFailed.title";
            public const string SecurityUnlockPasswordPlaceholder = "settings.security.unlockPasswordPlaceholder";
            public const string SecurityUnlock = "settings.security.unlock";
            public const string SecurityUnlockFailedTitle = "settings.security.unlockFailed.title";
            public const string SecurityUnlockFailedDetail = "settings.security.unlockFailed.detail";

            public const string UpdatesAutoCheck = "settings.updates.autoCheck";
            public const string UpdatesAutoCheckDescription = "settings.updates.autoCheckDescription";

            public const string AboutVersion = "settings.about.version";
            public const string AboutHomepage = "settings.about.homepage";
            public const string AboutLicense = "settings.about.license";
            public const string AboutAttributions = "settings.about.attributions";
            public const string AboutLinkFailedTitle = "settings.about.linkFailed.title";
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
