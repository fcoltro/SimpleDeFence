using System;
using System.Linq;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// The firewall's load-bearing invariant: every mode that is not deliberately open denies by
    /// default, and the rule that does the denying is marked as the mode's own so a failure to
    /// install it can be told apart from a failure to install anything else.
    ///
    /// None of this was reachable from a test before - it lived inside a 233-line method on a class
    /// that opens a WFP session in its field initializers. These walk all five modes and an
    /// unrecognised one.
    /// </summary>
    public class ModeRulesTests
    {
        private static readonly Guid ModeId = Guid.NewGuid();

        public static TheoryData<FirewallMode> OperatingModes => new()
        {
            FirewallMode.Normal,
            FirewallMode.BlockAll,
            FirewallMode.AllowOutgoing,
            FirewallMode.Learning,
            FirewallMode.Disabled,
        };

        /// <summary>Everything that is not Learning or Disabled has to stop something by itself.</summary>
        public static TheoryData<FirewallMode> DenyingModes => new()
        {
            FirewallMode.Normal,
            FirewallMode.BlockAll,
            FirewallMode.AllowOutgoing,
        };

        [Theory]
        [MemberData(nameof(OperatingModes))]
        public void Every_mode_contributes_at_least_one_rule_of_its_own(FirewallMode mode)
        {
            var rules = ModeRules.For(mode, ModeId, out _);

            Assert.NotEmpty(rules);
            Assert.All(rules, r => Assert.True(r.IsModeDefault, $"{r.Name} is not marked as the mode's own"));
            Assert.All(rules, r => Assert.Equal(ModeId, r.ExceptionId));
        }

        [Theory]
        [MemberData(nameof(DenyingModes))]
        public void A_denying_mode_produces_a_default_deny_over_everything(FirewallMode mode)
        {
            var rules = ModeRules.For(mode, ModeId, out _);

            var deny = rules.SingleOrDefault(r => r.Action == RuleAction.Block);

            Assert.True(deny is not null, $"{mode} produced no block rule at all");
            Assert.Equal(RuleDirection.InOut, deny!.Direction);
            Assert.Equal(Protocol.Any, deny.Protocol);
            Assert.Equal((ulong)FilterWeights.DefaultBlock, deny.Weight);
            Assert.True(deny.IsModeDefault);
        }

        [Fact]
        public void An_unrecognised_mode_still_denies()
        {
            // The reason the default arm exists. A mode outside the five matched no case once, so
            // no rule was added, and the firewall came up with user permits and nothing to deny
            // anything else. FirewallMode.Unknown is a defined value and is what ServerState.Mode
            // starts out as, so this is one field copy away from happening.
            var unknown = (FirewallMode)9999;

            Assert.True(ModeRules.IsUnrecognised(unknown));

            var rules = ModeRules.For(unknown, ModeId, out bool needUserRules);

            Assert.Contains(rules, r => r.Action == RuleAction.Block && r.Direction == RuleDirection.InOut);
            Assert.True(needUserRules);
        }

        [Fact]
        public void Unknown_is_treated_as_unrecognised_as_well()
        {
            Assert.True(ModeRules.IsUnrecognised(FirewallMode.Unknown));
            Assert.Contains(ModeRules.For(FirewallMode.Unknown, ModeId, out _), r => r.Action == RuleAction.Block);
        }

        [Theory]
        [MemberData(nameof(OperatingModes))]
        public void Only_the_two_all_or_nothing_modes_skip_application_exceptions(FirewallMode mode)
        {
            ModeRules.For(mode, ModeId, out bool needUserRules);

            bool answersEveryPacketItself = mode is FirewallMode.BlockAll or FirewallMode.Disabled;
            Assert.Equal(!answersEveryPacketItself, needUserRules);
        }

        [Fact]
        public void Allow_outgoing_denies_first_and_permits_outbound()
        {
            var rules = ModeRules.For(FirewallMode.AllowOutgoing, ModeId, out _);

            var deny = rules.Single(r => r.Action == RuleAction.Block);
            var permit = rules.Single(r => r.Action == RuleAction.Allow);

            Assert.Equal(RuleDirection.InOut, deny.Direction);
            Assert.Equal(RuleDirection.Out, permit.Direction);

            // The permit has to outweigh the deny or outbound traffic would be stopped by it.
            Assert.True(permit.Weight > deny.Weight);
        }

        [Theory]
        [InlineData(FirewallMode.Disabled)]
        [InlineData(FirewallMode.Learning)]
        public void The_open_modes_permit_everything_and_deny_nothing(FirewallMode mode)
        {
            var rules = ModeRules.For(mode, ModeId, out _);

            Assert.DoesNotContain(rules, r => r.Action == RuleAction.Block);
            var permit = rules.Single();
            Assert.Equal(RuleDirection.InOut, permit.Direction);
            Assert.Equal(Protocol.Any, permit.Protocol);
        }

        [Fact]
        public void Block_all_denies_and_nothing_else()
        {
            var rules = ModeRules.For(FirewallMode.BlockAll, ModeId, out bool needUserRules);

            var only = Assert.Single(rules);
            Assert.Equal(RuleAction.Block, only.Action);
            Assert.False(needUserRules);
        }

        [Fact]
        public void The_mode_mark_survives_a_shallow_copy()
        {
            // GetRulesForException copies rule templates around; a mark that did not survive the
            // copy would quietly turn the default deny back into an ordinary rule at install time.
            var deny = ModeRules.For(FirewallMode.Normal, ModeId, out _).Single();

            Assert.True(deny.ShallowCopy().IsModeDefault);
        }

        [Fact]
        public void A_user_rule_is_not_marked_as_the_modes_own()
        {
            // The distinction InstallRules leans on: this one failing costs one application its
            // network, not the whole firewall its floor.
            var userRule = new RuleDef { Action = RuleAction.Block, Name = "Block" };

            Assert.False(userRule.IsModeDefault);
        }
    }
}
