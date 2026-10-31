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

            /// <summary>The state label beside a ToggleSwitch. WinUI's own default for these is
            /// English here (its resources follow the framework's language, not ours, and this app
            /// ships unpackaged and self-contained), so every switch is given the text explicitly
            /// rather than left reading "Off" in the middle of a translated page.</summary>
            public const string On = "common.on";
            public const string Off = "common.off";
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

            /// <summary>Replaces NotConnectedDetail when the reason the service is unreachable is
            /// that this window is not elevated. "Is it installed and running?" is a misleading
            /// question to ask someone whose service is running perfectly well.</summary>
            public const string NotConnectedNeedsAdmin = "status.notConnectedNeedsAdmin";
            public const string Locked = "status.locked";
            public const string LockedDetail = "status.lockedDetail";
            public const string Connected = "status.connected";

            /// <summary>Shown when the service is running but not enforcing what the configuration
            /// says. One detail line per <see cref="ServiceDegradation"/> flag.</summary>
            public static class Degraded
            {
                public const string Title = "status.degraded.title";
                public const string InitFailed = "status.degraded.initFailed";
                public const string DatabaseUnavailable = "status.degraded.databaseUnavailable";
                public const string RulesIncomplete = "status.degraded.rulesIncomplete";
                public const string HostsUnavailable = "status.degraded.hostsUnavailable";
                public const string ConfigUnreadable = "status.degraded.configUnreadable";
            }
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

            /// <summary>The choice in the learning confirmation of when the service switches back to
            /// Normal by itself. Learning lets everything through, so leaving it on by mistake is the
            /// failure this guards against.</summary>
            public const string LearningDurationLabel = "mode.learningDuration.label";
            public const string LearningDurationNever = "mode.learningDuration.never";
            public const string LearningDurationMinutes10 = "mode.learningDuration.minutes10";
            public const string LearningDurationMinutes30 = "mode.learningDuration.minutes30";
            public const string LearningDurationHour1 = "mode.learningDuration.hour1";

            /// <summary>Follows the learning description in the status line while a time limit is
            /// set: {0} is the description, {1} the time it ends.</summary>
            public const string LearningEndsAt = "mode.learningEndsAt";

            /// <summary>Shown once when the window sees Learning end, by the time limit or by hand,
            /// so the rules it wrote get looked at.</summary>
            public const string LearningEndedTitle = "mode.learningEnded.title";
            public const string LearningEndedBody = "mode.learningEnded.body";
            public const string LearningEndedBodyOne = "mode.learningEnded.bodyOne";
            public const string LearningEndedBodyNone = "mode.learningEnded.bodyNone";
            public const string LearningEndedReview = "mode.learningEnded.review";
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
            /// <summary>Shown instead of EmptyBlocked when the firewall log could not be read, so
            /// that "nothing was blocked" is never said about a list we could not fetch.</summary>
            public const string BlockedUnavailable = "connections.empty.blockedUnavailable";
            public const string EmptyConnected = "connections.empty.connected";
            public const string EmptyOpen = "connections.empty.open";
            public const string EmptyFiltered = "connections.empty.filtered";
            public const string FilterPlaceholder = "connections.filterPlaceholder";
            public const string AutoRefresh = "connections.autoRefresh";
            public const string Allow = "connections.allow";
            public const string AllowDialogTitle = "connections.allowDialog.title";
            public const string AllowDialogBody = "connections.allowDialog.body";
            public const string AllowSuccessTitle = "connections.allowSuccess.title";
            public const string AllowSuccessBody = "connections.allowSuccess.body";
            public const string AllowPublisherSuccessBody = "connections.allowPublisherSuccess.body";
            public const string AllowByPublisher = "connections.allowByPublisher";
            public const string AllowFailedTitle = "connections.allowFailed.title";
            public const string AllowFailedLockedDetail = "connections.allowFailed.lockedDetail";
            public const string AllowFailedStaleDetail = "connections.allowFailed.staleDetail";
            public const string AllowFailedGenericDetail = "connections.allowFailed.genericDetail";
            public const string AllowFailedUnidentified = "connections.allowFailed.unidentified";
            public const string GatherFailedTitle = "connections.gatherFailed.title";
            public const string BlockedAttempts = "connections.blocked.attempts";
            public const string BlockedAttemptsOneAddress = "connections.blocked.attemptsOneAddress";
            public const string BlockedAttemptsHeader = "connections.blocked.attemptsHeader";
            public const string ShowAll = "connections.showAll";
            public const string LegendLocal = "connections.legend.local";
            public const string ListTruncated = "connections.truncated";
            public const string ConnectedSummary = "connections.connected.summary";
            public const string ConnectedHeader = "connections.connected.header";
            public const string OpenSummary = "connections.open.summary";
            public const string OpenHeader = "connections.open.header";

            /// <summary>The right-click menu on every row of the three lists.</summary>
            public const string MenuDetails = "connections.menu.details";
            public const string MenuLookUp = "connections.menu.lookUp";
            public const string MenuFilterApp = "connections.menu.filterApp";
            public const string MenuCopyAddresses = "connections.menu.copyAddresses";
            public const string MenuCopyPath = "connections.menu.copyPath";
            public const string MenuOpenLocation = "connections.menu.openLocation";
            public const string OpenLocationMissing = "connections.menu.openLocationMissing";
            public const string MenuMute = "connections.menu.mute";
            public const string MenuUnmute = "connections.menu.unmute";
            public const string MutedShow = "connections.muted.show";
            public const string MutedHide = "connections.muted.hide";

            // "Stop it trying": switch off a blocked app's background parts
            public const string StopMenu = "connections.stopTrying.menu";
            public const string StopTitle = "connections.stopTrying.title";
            public const string StopIntro = "connections.stopTrying.intro";
            public const string StopFromFolder = "connections.stopTrying.fromFolder";
            public const string StopIntroFolder = "connections.stopTrying.introFolder";
            public const string StopUnsignedFolder = "connections.stopTrying.unsignedFolder";
            public const string StopNeeded = "connections.stopTrying.needed";
            public const string StopKindService = "connections.stopTrying.kindService";
            public const string StopKindTask = "connections.stopTrying.kindTask";
            public const string StopKindStartup = "connections.stopTrying.kindStartup";
            public const string StopApply = "connections.stopTrying.apply";
            public const string StopAllOn = "connections.stopTrying.allOn";
            public const string StopNone = "connections.stopTrying.none";
            public const string StopUnsigned = "connections.stopTrying.unsigned";
            public const string StopUnreachable = "connections.stopTrying.unreachable";
            public const string StopDone = "connections.stopTrying.done";
            public const string StopFailed = "connections.stopTrying.failed";
            public const string StopLocked = "connections.stopTrying.locked";

            /// <summary>The details dialog behind "Show all", and its on-demand address lookup.</summary>
            public const string DetailsAddresses = "connections.details.addresses";
            public const string DetailsLookUpNote = "connections.details.lookUpNote";
            public const string DetailsLookUpLimit = "connections.details.lookUpLimit";
            public const string DetailsLocal = "connections.details.local";
            public const string DetailsNotFound = "connections.details.notFound";
            public const string DetailsHost = "connections.details.host";
            public const string DetailsOwner = "connections.details.owner";
            public const string DetailsNetwork = "connections.details.network";
            public const string DetailsCountry = "connections.details.country";
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
            public const string EmptyPublishers = "rules.empty.publishers";
            public const string PublishersKind = "rules.publishersKind";
            public const string PublishersDescription = "rules.publishersDescription";
            public const string EmptyFiltered = "rules.empty.filtered";
            public const string AppMissing = "rules.appMissing";
            public const string Remove = "rules.remove";
            public const string CleanUp = "rules.cleanUp";
            public const string CleanUpTooltip = "rules.cleanUpTooltip";
            public const string CleanUpTitle = "rules.cleanUpDialog.title";
            public const string CleanUpConfirmBody = "rules.cleanUpDialog.confirmBody";
            public const string CleanUpNothing = "rules.cleanUpDialog.nothing";
            public const string MultiSelectHint = "rules.multiSelectHint";
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
            public const string AddPickFolder = "rules.addPickFolder";
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
            public const string DetailPolicyCustom = "rules.detail.policyCustom";
            public const string CustomAddRule = "rules.custom.addRule";
            public const string CustomRule = "rules.custom.rule";
            public const string CustomRemoveRule = "rules.custom.removeRule";
            public const string CustomAction = "rules.custom.action";
            public const string CustomAllow = "rules.custom.allow";
            public const string CustomBlock = "rules.custom.block";
            public const string CustomDirection = "rules.custom.direction";
            public const string CustomOutgoing = "rules.custom.outgoing";
            public const string CustomIncoming = "rules.custom.incoming";
            public const string CustomBoth = "rules.custom.both";
            public const string CustomProtocol = "rules.custom.protocol";
            public const string CustomAnyProtocol = "rules.custom.anyProtocol";
            public const string CustomTcpUdp = "rules.custom.tcpUdp";
            public const string CustomRemoteAddresses = "rules.custom.remoteAddresses";
            public const string CustomRemoteAddressesHint = "rules.custom.remoteAddressesHint";
            public const string CustomRemotePorts = "rules.custom.remotePorts";
            public const string CustomLocalPorts = "rules.custom.localPorts";
            public const string CustomPortsHint = "rules.custom.portsHint";
            public const string CustomInvalidTitle = "rules.custom.invalidTitle";
            public const string CustomInvalidPorts = "rules.custom.invalidPorts";
            public const string CustomInvalidAddresses = "rules.custom.invalidAddresses";
            public const string CustomNoRules = "rules.custom.noRules";
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
            public const string SectionLogging = "settings.section.logging";
            public const string SectionPrivacy = "settings.section.privacy";

            public const string CommitFailedTitle = "settings.commitFailed.title";
            public const string CommitFailedLockedDetail = "settings.commitFailed.lockedDetail";
            public const string CommitFailedStaleDetail = "settings.commitFailed.staleDetail";
            public const string CommitFailedGenericDetail = "settings.commitFailed.genericDetail";
            public const string LocalSaveFailedTitle = "settings.localSaveFailed.title";
            public const string LocalSaveFailedDetail = "settings.localSaveFailed.detail";

            public const string GeneralTheme = "settings.general.theme";
            public const string GeneralThemeDescription = "settings.general.themeDescription";
            public const string GeneralThemeAuto = "settings.general.themeAuto";
            public const string GeneralThemeLight = "settings.general.themeLight";
            public const string GeneralThemeDark = "settings.general.themeDark";
            public const string GeneralLanguage = "settings.general.language";
            public const string GeneralLanguageDescription = "settings.general.languageDescription";
            public const string GeneralLanguageAuto = "settings.general.languageAuto";
            public const string GeneralLanguageRestartTitle = "settings.general.languageRestartTitle";
            public const string GeneralLanguageRestartDetail = "settings.general.languageRestartDetail";
            public const string GeneralAskForExceptionDetails = "settings.general.askForExceptionDetails";
            public const string GeneralAskForExceptionDetailsDescription = "settings.general.askForExceptionDetailsDescription";
            public const string GeneralEnableHotkeys = "settings.general.enableHotkeys";
            public const string GeneralEnableHotkeysDescription = "settings.general.enableHotkeysDescription";
            public const string GeneralNotifyBlocked = "settings.general.notifyBlocked";
            public const string GeneralNotifyBlockedDescription = "settings.general.notifyBlockedDescription";
            public const string GeneralNotifyListening = "settings.general.notifyListening";
            public const string GeneralNotifyListeningDescription = "settings.general.notifyListeningDescription";
            public const string GeneralAutoRefreshInterval = "settings.general.autoRefreshInterval";
            public const string GeneralAutoRefreshIntervalDescription = "settings.general.autoRefreshIntervalDescription";

            public const string LoggingEnabled = "settings.logging.enabled";
            public const string LoggingEnabledDescription = "settings.logging.enabledDescription";
            public const string LoggingPath = "settings.logging.path";
            public const string LoggingPathDescription = "settings.logging.pathDescription";
            public const string LoggingPathPlaceholder = "settings.logging.pathPlaceholder";
            public const string LoggingInterval = "settings.logging.interval";
            public const string LoggingIntervalDescription = "settings.logging.intervalDescription";
            public const string LoggingMaxSize = "settings.logging.maxSize";
            public const string LoggingMaxSizeDescription = "settings.logging.maxSizeDescription";
            public const string LoggingOpenFolder = "settings.logging.openFolder";
            public const string LoggingWriteFailedTitle = "settings.logging.writeFailedTitle";

            public const string ProtectionAllowLocalSubnet = "settings.protection.allowLocalSubnet";
            public const string ProtectionAllowLocalSubnetDescription = "settings.protection.allowLocalSubnetDescription";
            public const string ProtectionDisplayOffBlock = "settings.protection.displayOffBlock";
            public const string ProtectionDisplayOffBlockDescription = "settings.protection.displayOffBlockDescription";
            public const string ProtectionPublicNetworkLockdown = "settings.protection.publicNetworkLockdown";
            public const string ProtectionPublicNetworkLockdownDescription = "settings.protection.publicNetworkLockdownDescription";
            public const string ProtectionPublicNetworkNow = "settings.protection.publicNetworkNow";

            public const string PrivacyTelemetry = "settings.privacy.telemetry";
            public const string PrivacyTelemetryDescription = "settings.privacy.telemetryDescription";
            public const string PrivacyStatusOff = "settings.privacy.statusOff";
            public const string PrivacyStatusOn = "settings.privacy.statusOn";
            public const string PrivacyStatusPartial = "settings.privacy.statusPartial";
            public const string PrivacyApply = "settings.privacy.apply";
            public const string PrivacyApplyAgain = "settings.privacy.applyAgain";
            public const string PrivacyUndo = "settings.privacy.undo";
            public const string PrivacyConfirmTitle = "settings.privacy.confirm.title";
            public const string PrivacyConfirmBody = "settings.privacy.confirm.body";
            public const string PrivacyConfirmConfirm = "settings.privacy.confirm.confirm";
            public const string PrivacyFailedTitle = "settings.privacy.failedTitle";
            public const string PrivacyFailedDetail = "settings.privacy.failedDetail";
            public const string PrivacyUndoFailedTitle = "settings.privacy.undoFailedTitle";

            public const string BlocklistsEnable = "settings.blocklists.enable";
            public const string BlocklistsEnableDescription = "settings.blocklists.enableDescription";
            public const string BlocklistsHosts = "settings.blocklists.hosts";
            public const string BlocklistsPorts = "settings.blocklists.ports";
            public const string BlocklistsAddresses = "settings.blocklists.addresses";
            public const string BlocklistsAddressesDescription = "settings.blocklists.addressesDescription";
            public const string BlocklistsAddressesPlaceholder = "settings.blocklists.addressesPlaceholder";
            public const string BlocklistsAddressesSave = "settings.blocklists.addressesSave";
            public const string BlocklistsAddressesInvalid = "settings.blocklists.addressesInvalid";

            public const string SecurityLockHostsFile = "settings.security.lockHostsFile";
            public const string SecurityLockHostsFileDescription = "settings.security.lockHostsFileDescription";

            public const string SecurityPasswordSet = "settings.security.passwordSet";
            public const string SecurityPasswordNotSet = "settings.security.passwordNotSet";
            public const string SecurityPasswordNotSetDetail = "settings.security.passwordNotSetDetail";
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
            public const string UpdatesCheckNow = "settings.updates.checkNow";
            public const string UpdatesCheckingTitle = "settings.updates.checkingTitle";
            public const string UpdatesCheckFailedTitle = "settings.updates.checkFailedTitle";
            public const string UpdatesNoneAvailable = "settings.updates.noneAvailable";
            public const string UpdatesAvailable = "settings.updates.available";
            public const string UpdatesDownloading = "settings.updates.downloading";
            public const string UpdatesVerificationFailed = "settings.updates.verificationFailed";

            public const string MaintenanceImport = "settings.maintenance.import";
            public const string MaintenanceExport = "settings.maintenance.export";
            public const string MaintenanceFilePickerName = "settings.maintenance.filePickerName";
            public const string MaintenanceImportConfirmTitle = "settings.maintenance.importConfirm.title";
            public const string MaintenanceImportConfirmBody = "settings.maintenance.importConfirm.body";
            public const string MaintenanceImportConfirmConfirm = "settings.maintenance.importConfirm.confirm";
            public const string MaintenanceImportSuccessTitle = "settings.maintenance.importSuccess.title";
            public const string MaintenanceImportSuccessBody = "settings.maintenance.importSuccess.body";
            public const string MaintenanceImportFailedTitle = "settings.maintenance.importFailed.title";
            public const string MaintenanceExportSuccessTitle = "settings.maintenance.exportSuccess.title";
            public const string MaintenanceExportSuccessBody = "settings.maintenance.exportSuccess.body";
            public const string MaintenanceExportFailedTitle = "settings.maintenance.exportFailed.title";

            public const string AboutVersion = "settings.about.version";
            public const string AboutHomepage = "settings.about.homepage";
            public const string AboutLicense = "settings.about.license";
            public const string AboutAttributions = "settings.about.attributions";
            public const string AboutTagline = "settings.about.tagline";
            public const string AboutBasedOn = "settings.about.basedOn";
            public const string AboutBasedOnDetail = "settings.about.basedOnDetail";
            public const string AboutLicenseDetail = "settings.about.licenseDetail";
            public const string AboutAttributionsDetail = "settings.about.attributionsDetail";
            public const string AboutHomepageDetail = "settings.about.homepageDetail";
            public const string AboutReadFailed = "settings.about.readFailed";
            public const string AboutLinkFailedTitle = "settings.about.linkFailed.title";
        }

        public static class Tray
        {
            public const string ModeNormal = "tray.mode.normal";
            public const string ModeBlockAll = "tray.mode.blockAll";
            public const string ModeAllowOutgoing = "tray.mode.allowOutgoing";
            public const string ModeDisabled = "tray.mode.disabled";
            public const string ModeLearning = "tray.mode.learning";
            public const string Manage = "tray.manage";
            public const string Connections = "tray.connections";
            public const string Lock = "tray.lock";
            public const string Elevate = "tray.elevate";
            public const string AllowLocalSubnet = "tray.allowLocalSubnet";
            public const string EnableHostsBlocklist = "tray.enableHostsBlocklist";
            public const string WhitelistByExecutable = "tray.whitelistByExecutable";
            public const string WhitelistByProcess = "tray.whitelistByProcess";
            public const string WhitelistByWindow = "tray.whitelistByWindow";
            public const string Quit = "tray.quit";
            public const string StillRunningTitle = "tray.stillRunning.title";
            public const string StillRunningBody = "tray.stillRunning.body";
            public const string UnlockTitle = "tray.unlock.title";
            public const string UnlockPasswordPlaceholder = "tray.unlock.passwordPlaceholder";
            public const string UnlockButton = "tray.unlock.button";
            public const string UnlockFailedTitle = "tray.unlock.failedTitle";

            public const string QuickAddConfirmTitle = "tray.quickAdd.confirmTitle";
            public const string QuickAddConfirmBody = "tray.quickAdd.confirmBody";
            public const string QuickAddConfirmAdd = "tray.quickAdd.confirmAdd";
            public const string ElevateFailedTitle = "tray.elevateFailed.title";
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

        /// <summary>The tray notifications ActivityNotifier shows. {0} is an app's name, {1} how many
        /// more apps the same notification covers.</summary>
        public static class Notifications
        {
            public const string BlockedOneTitle = "notifications.blockedOneTitle";
            public const string BlockedManyTitle = "notifications.blockedManyTitle";
            public const string BlockedBody = "notifications.blockedBody";
            public const string ListeningOneTitle = "notifications.listeningOneTitle";
            public const string ListeningManyTitle = "notifications.listeningManyTitle";
            public const string ListeningBody = "notifications.listeningBody";
        }

        /// <summary>How long a rule stays in force, one per offered AppExceptionTimer value, plus
        /// the two suffixes the Rules list adds to a rule that is not permanent.</summary>
        public static class Duration
        {
            public const string Label = "duration.label";
            public const string Permanent = "duration.permanent";
            public const string Minutes5 = "duration.minutes5";
            public const string Minutes30 = "duration.minutes30";
            public const string Hour1 = "duration.hour1";
            public const string Hours4 = "duration.hours4";
            public const string Hours9 = "duration.hours9";
            public const string Hours24 = "duration.hours24";
            public const string UntilRestart = "duration.untilRestart";

            /// <summary>{0} is the policy text, {1} the time the rule expires.</summary>
            public const string ExpiresAtSuffix = "duration.expiresAtSuffix";

            /// <summary>{0} is the policy text.</summary>
            public const string UntilRestartSuffix = "duration.untilRestartSuffix";
        }
    }
}
