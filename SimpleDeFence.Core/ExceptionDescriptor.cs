using SimpleDeFence.Localization;
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>
    /// The fields a GUI shows for a single firewall exception.
    /// </summary>
    public sealed class ExceptionDescription
    {
        public string Name { get; }
        public string Kind { get; }
        public string Detail { get; }
        public string Policy { get; }
        public bool IsBlocked { get; }

        public ExceptionDescription(string name, string kind, string detail, string policy, bool isBlocked)
        {
            Name = name;
            Kind = kind;
            Detail = detail;
            Policy = policy;
            IsBlocked = isBlocked;
        }
    }

    /// <summary>
    /// Turns a <see cref="FirewallExceptionV3"/> into display fields. Lives in Core rather than in a
    /// GUI so the WinForms and WinUI front-ends can describe an entry identically.
    ///
    /// Text comes from <see cref="Loc"/>. The WinForms GUI keeps using its own localized resx
    /// (unaffected - it does not call this class); this is for the WinUI GUI.
    /// </summary>
    public static class ExceptionDescriptor
    {
        public static ExceptionDescription Describe(FirewallExceptionV3 ex)
        {
            return new ExceptionDescription(
                SubjectName(ex.Subject),
                SubjectKind(ex.Subject),
                SubjectDetail(ex.Subject),
                DescribePolicy(ex.Policy),
                ex.Policy.PolicyType == PolicyType.HardBlock);
        }

        // The subject accessors below all switch on SubjectType rather than the runtime type.
        // ServiceSubject derives from ExecutableSubject, so a type-pattern switch silently matches
        // services as executables and reports the exe path where the service name belongs.

        public static string SubjectName(ExceptionSubject subject) => subject.SubjectType switch
        {
            SubjectType.Executable => ((ExecutableSubject)subject).ExecutableName,
            SubjectType.Service => ((ServiceSubject)subject).ServiceName,
            SubjectType.AppContainer => ((AppContainerSubject)subject).DisplayName,
            SubjectType.Global => Loc.T(LocKeys.Subject.AllApplications),
            _ => Loc.T(LocKeys.Common.Unknown),
        };

        public static string SubjectKind(ExceptionSubject subject) => subject.SubjectType switch
        {
            SubjectType.Executable => Loc.T(LocKeys.Subject.Executable),
            SubjectType.Service => Loc.T(LocKeys.Subject.Service),
            SubjectType.AppContainer => Loc.T(LocKeys.Subject.UwpPackage),
            SubjectType.Global => Loc.T(LocKeys.Subject.Global),
            _ => Loc.T(LocKeys.Common.Unknown),
        };

        public static string SubjectDetail(ExceptionSubject subject) => subject.SubjectType switch
        {
            SubjectType.Executable => ((ExecutableSubject)subject).ExecutablePath,
            SubjectType.Service => ((ServiceSubject)subject).ExecutablePath,
            SubjectType.AppContainer => Join((AppContainerSubject)subject),
            _ => string.Empty,
        };

        private static string Join(AppContainerSubject uwp) => uwp.PublisherId + ", " + uwp.Publisher;

        public static string DescribePolicy(ExceptionPolicy policy)
        {
            switch (policy.PolicyType)
            {
                case PolicyType.HardBlock:
                    return Loc.T(LocKeys.Policy.Blocked);

                case PolicyType.Unrestricted:
                    return ((UnrestrictedPolicy)policy).LocalNetworkOnly
                        ? Loc.T(LocKeys.Policy.UnrestrictedLan)
                        : Loc.T(LocKeys.Policy.Unrestricted);

                case PolicyType.TcpUdpOnly:
                    return DescribeTcpUdp((TcpUdpPolicy)policy);

                case PolicyType.RuleList:
                    int count = ((RuleListPolicy)policy).Rules.Count;
                    return count == 1
                        ? Loc.T(LocKeys.Policy.CustomRuleOne)
                        : Loc.T(LocKeys.Policy.CustomRuleMany, count);

                default:
                    return Loc.T(LocKeys.Common.Unknown);
            }
        }

        private static string DescribeTcpUdp(TcpUdpPolicy p)
        {
            var parts = new List<string>();
            Add(parts, Loc.T(LocKeys.Policy.TcpOut), p.AllowedRemoteTcpConnectPorts);
            Add(parts, Loc.T(LocKeys.Policy.UdpOut), p.AllowedRemoteUdpConnectPorts);
            Add(parts, Loc.T(LocKeys.Policy.TcpIn), p.AllowedLocalTcpListenerPorts);
            Add(parts, Loc.T(LocKeys.Policy.UdpIn), p.AllowedLocalUdpListenerPorts);

            if (parts.Count == 0)
                return Loc.T(LocKeys.Policy.NoPorts);

            var text = string.Join(", ", parts);
            return p.LocalNetworkOnly ? Loc.T(LocKeys.Policy.LanOnlySuffix, text) : text;
        }

        private static void Add(List<string> into, string label, string? ports)
        {
            if (string.IsNullOrEmpty(ports))
                return;

            into.Add(label + " " + (ports == "*" ? Loc.T(LocKeys.Policy.AllPorts) : ports));
        }
    }
}
