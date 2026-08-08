using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class ExceptionDescriptorTests
    {
        // --- Subjects ---------------------------------------------------------------------

        [Fact]
        public void Executable_is_named_by_file_name_and_detailed_by_full_path()
        {
            var subject = new ExecutableSubject(@"C:\Program Files\Thing\thing.exe");

            Assert.Equal("thing.exe", ExceptionDescriptor.SubjectName(subject));
            Assert.Equal("Executable", ExceptionDescriptor.SubjectKind(subject));
            Assert.Equal(@"C:\Program Files\Thing\thing.exe", ExceptionDescriptor.SubjectDetail(subject));
        }

        /// <summary>
        /// ServiceSubject derives from ExecutableSubject, so anything switching on the runtime type
        /// silently reports a service as an executable - showing the exe path where the service name
        /// belongs. This is the regression guard for that.
        /// </summary>
        [Fact]
        public void Service_is_not_mistaken_for_an_executable()
        {
            var service = new ServiceSubject(@"C:\Windows\System32\svchost.exe", "UsoSvc");

            Assert.IsAssignableFrom<ExecutableSubject>(service);   // the trap this guards against

            Assert.Equal("UsoSvc", ExceptionDescriptor.SubjectName(service));
            Assert.Equal("Service", ExceptionDescriptor.SubjectKind(service));
            Assert.Equal(@"C:\Windows\System32\svchost.exe", ExceptionDescriptor.SubjectDetail(service));
        }

        [Fact]
        public void Global_subject_is_described_as_all_applications()
        {
            var subject = GlobalSubject.Instance;

            Assert.Equal("All applications", ExceptionDescriptor.SubjectName(subject));
            Assert.Equal("Global", ExceptionDescriptor.SubjectKind(subject));
            Assert.Equal(string.Empty, ExceptionDescriptor.SubjectDetail(subject));
        }

        [Fact]
        public void Uwp_package_is_named_by_display_name_and_detailed_by_publisher()
        {
            var subject = new AppContainerSubject("S-1-15-2-1", "Contoso Mail", "CN=Contoso", "abc123");

            Assert.Equal("Contoso Mail", ExceptionDescriptor.SubjectName(subject));
            Assert.Equal("UWP Package", ExceptionDescriptor.SubjectKind(subject));
            Assert.Equal("abc123, CN=Contoso", ExceptionDescriptor.SubjectDetail(subject));
        }

        // --- Policies ---------------------------------------------------------------------

        [Fact]
        public void Hard_block_is_described_as_blocked()
        {
            Assert.Equal("Blocked", ExceptionDescriptor.DescribePolicy(new HardBlockPolicy()));
        }

        [Fact]
        public void Unrestricted_reports_whether_it_is_lan_only()
        {
            Assert.Equal("Unrestricted",
                ExceptionDescriptor.DescribePolicy(new UnrestrictedPolicy { LocalNetworkOnly = false }));

            Assert.Equal("Unrestricted (LAN only)",
                ExceptionDescriptor.DescribePolicy(new UnrestrictedPolicy { LocalNetworkOnly = true }));
        }

        [Fact]
        public void Tcp_udp_policy_lists_each_configured_direction()
        {
            var policy = new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = "443",
                AllowedLocalUdpListenerPorts = "5353",
            };

            Assert.Equal("TCP out 443, UDP in 5353", ExceptionDescriptor.DescribePolicy(policy));
        }

        [Fact]
        public void Tcp_udp_policy_renders_wildcard_ports_as_all()
        {
            // This is what TcpUdpPolicy(unrestricted: true) produces.
            var policy = new TcpUdpPolicy(unrestricted: true);

            Assert.Equal("TCP out all, UDP out all, TCP in all, UDP in all",
                ExceptionDescriptor.DescribePolicy(policy));
        }

        [Fact]
        public void Tcp_udp_policy_with_no_ports_says_so_rather_than_looking_permissive()
        {
            Assert.Equal("No ports allowed", ExceptionDescriptor.DescribePolicy(new TcpUdpPolicy()));
        }

        [Fact]
        public void Tcp_udp_policy_appends_lan_only()
        {
            var policy = new TcpUdpPolicy
            {
                AllowedRemoteTcpConnectPorts = "80",
                LocalNetworkOnly = true,
            };

            Assert.Equal("TCP out 80 (LAN only)", ExceptionDescriptor.DescribePolicy(policy));
        }

        [Theory]
        [InlineData(0, "0 custom rules")]
        [InlineData(1, "1 custom rule")]
        [InlineData(3, "3 custom rules")]
        public void Rule_list_policy_is_pluralised(int ruleCount, string expected)
        {
            var policy = new RuleListPolicy();
            for (int i = 0; i < ruleCount; ++i)
                policy.Rules.Add(new RuleDef());

            Assert.Equal(expected, ExceptionDescriptor.DescribePolicy(policy));
        }

        // --- Whole entry ------------------------------------------------------------------

        [Fact]
        public void Describe_flags_hard_blocked_entries()
        {
            var blocked = new FirewallExceptionV3(
                new ExecutableSubject(@"C:\bad\thing.exe"), new HardBlockPolicy());
            var allowed = new FirewallExceptionV3(
                new ExecutableSubject(@"C:\good\thing.exe"), new UnrestrictedPolicy());

            Assert.True(ExceptionDescriptor.Describe(blocked).IsBlocked);
            Assert.False(ExceptionDescriptor.Describe(allowed).IsBlocked);
        }

        [Fact]
        public void Describe_fills_every_field_for_a_service_entry()
        {
            var ex = new FirewallExceptionV3(
                new ServiceSubject(@"C:\Windows\System32\svchost.exe", "DoSvc"),
                new TcpUdpPolicy { AllowedRemoteTcpConnectPorts = "80,443" });

            var d = ExceptionDescriptor.Describe(ex);

            Assert.Equal("DoSvc", d.Name);
            Assert.Equal("Service", d.Kind);
            Assert.Equal(@"C:\Windows\System32\svchost.exe", d.Detail);
            Assert.Equal("TCP out 80,443", d.Policy);
            Assert.False(d.IsBlocked);
        }
    }
}
