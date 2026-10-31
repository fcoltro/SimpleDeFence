using System;
using System.Collections.Generic;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class CustomRuleTextTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("*")]
        [InlineData("Any")]
        [InlineData(" , ")]
        public void Blank_star_and_any_mean_no_restriction(string? text)
        {
            Assert.True(CustomRuleText.TryNormalizePorts(text, out var ports));
            Assert.Null(ports);
            Assert.True(CustomRuleText.TryNormalizeAddresses(text, out var addresses));
            Assert.Null(addresses);
        }

        [Theory]
        [InlineData("80", "80")]
        [InlineData(" 80 , 443 ", "80,443")]
        [InlineData("8000-8100", "8000-8100")]
        [InlineData("8000 - 8100,53", "8000-8100,53")]
        [InlineData("0080", "80")]
        [InlineData("443-443", "443")]
        [InlineData("65535", "65535")]
        public void Ports_are_normalised(string text, string expected)
        {
            Assert.True(CustomRuleText.TryNormalizePorts(text, out var ports));
            Assert.Equal(expected, ports);
        }

        [Theory]
        [InlineData("65536")]
        [InlineData("-1")]
        [InlineData("http")]
        [InlineData("100-50")]
        [InlineData("80;443")]
        [InlineData("1-2-3")]
        public void Bad_ports_are_refused(string text)
        {
            Assert.False(CustomRuleText.TryNormalizePorts(text, out _));
        }

        [Theory]
        [InlineData("192.168.1.10", "192.168.1.10")]
        [InlineData("10.0.0.0/8, 172.16.0.0/12", "10.0.0.0/8,172.16.0.0/12")]
        [InlineData("localsubnet,dns", "LocalSubnet,DNS")]
        [InlineData("DefaultGateway", "DefaultGateway")]
        [InlineData("2001:db8::/32", "2001:db8::/32")]
        public void Addresses_are_normalised(string text, string expected)
        {
            Assert.True(CustomRuleText.TryNormalizeAddresses(text, out var addresses));
            Assert.Equal(expected, addresses);
        }

        [Theory]
        [InlineData("10")]
        [InlineData("10.1")]
        [InlineData("10.0.0.0/33")]
        [InlineData("300.1.1.1")]
        [InlineData("example.com")]
        [InlineData("10.0.0.0/")]
        public void Bad_addresses_are_refused(string text)
        {
            Assert.False(CustomRuleText.TryNormalizeAddresses(text, out _));
        }

        [Fact]
        public void Custom_rules_replace_the_policy_and_keep_the_identity()
        {
            var original = new FirewallExceptionV3(new ExecutableSubject(@"C:\Apps\a.exe"), new UnrestrictedPolicy())
            {
                Timer = AppExceptionTimer.For_1_Hour,
            };
            var rule = new RuleDef(Guid.NewGuid(), "Web", new ExecutableSubject(@"C:\Other\b.exe"), RuleAction.Allow, RuleDirection.Out, Protocol.TCP, 0)
            {
                RemotePorts = "80,443",
            };

            var edited = RuleEdit.ApplyCustomRules(original, new List<RuleDef> { rule });

            Assert.Equal(original.Id, edited.Id);
            Assert.Equal(AppExceptionTimer.For_1_Hour, edited.Timer);
            var list = Assert.IsType<RuleListPolicy>(edited.Policy);
            var only = Assert.Single(list.Rules);
            Assert.Equal("80,443", only.RemotePorts);
            Assert.Null(only.Application);
            Assert.Equal(Guid.Empty, only.ExceptionId);
            Assert.Equal(@"C:\Other\b.exe", rule.Application);   // the caller's rule is not changed
        }

        [Fact]
        public void An_empty_custom_rule_list_is_refused()
        {
            var original = new FirewallExceptionV3(new ExecutableSubject(@"C:\Apps\a.exe"), new UnrestrictedPolicy());
            Assert.Throws<ArgumentException>(() => RuleEdit.ApplyCustomRules(original, new List<RuleDef>()));
        }
    }
}
