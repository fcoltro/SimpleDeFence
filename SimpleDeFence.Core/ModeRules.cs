using System;
using System.Collections.Generic;

namespace SimpleDeFence
{
    /// <summary>
    /// How much a rule outweighs another when WFP has to choose between them. Lifted out of
    /// SimpleDeFenceServer so the rules a mode contributes can be assembled - and tested - without
    /// constructing the service.
    /// </summary>
    public enum FilterWeights : ulong
    {
        Blocklist = 9000000,
        RawSocketPermit = 8000000,
        RawSocketBlock = 7000000,
        UserBlock = 6000000,
        UserPermit = 5000000,
        DefaultPermit = 4000000,
        DefaultBlock = 3000000,
    }

    /// <summary>
    /// The rules a firewall mode contributes on its own, before any user exception is considered.
    ///
    /// This is the decision that settles whether anything is denied at all, and it used to sit in
    /// the middle of a 233-line method on a 2,200-line class that no test could construct - so the
    /// one invariant that matters most, "every mode that is not deliberately open still denies by
    /// default", was enforced only by reading. Here it is a pure function of the mode, and
    /// ModeRulesTests walks all five modes plus an unrecognised one.
    /// </summary>
    public static class ModeRules
    {
        /// <summary>
        /// Builds the mode's own rules.
        /// </summary>
        /// <param name="mode">The mode being entered.</param>
        /// <param name="modeId">Exception id stamped on each rule, so they can be told apart from
        /// user rules once installed.</param>
        /// <param name="needUserRules">False for the two modes that answer every packet by
        /// themselves - Block All denies everything, Disabled permits everything - where collecting
        /// application exceptions would be work with no effect.</param>
        public static List<RuleDef> For(FirewallMode mode, Guid modeId, out bool needUserRules)
        {
            var rules = new List<RuleDef>(2);
            needUserRules = true;

            switch (mode)
            {
                case FirewallMode.AllowOutgoing:
                    rules.Add(BlockEverything(modeId));
                    rules.Add(new RuleDef(modeId, "Allow outbound", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.Out, Protocol.Any, (ulong)FilterWeights.DefaultPermit)
                    {
                        IsModeDefault = true
                    });
                    break;

                case FirewallMode.BlockAll:
                    needUserRules = false;
                    rules.Add(BlockEverything(modeId));
                    break;

                case FirewallMode.Learning:
                    rules.Add(AllowEverything(modeId));
                    break;

                case FirewallMode.Disabled:
                    needUserRules = false;
                    rules.Add(AllowEverything(modeId));
                    break;

                case FirewallMode.Normal:
                    rules.Add(BlockEverything(modeId));
                    break;

                default:
                    // There was no default arm here once, so an unrecognised mode matched nothing,
                    // added no rule, and left the firewall with user permits and nothing to deny
                    // anything else - which is to say wide open. MODE_SWITCH refuses such a mode
                    // outright; this is the second line, so that no route to an unexpected value
                    // can end in a permissive firewall. IsUnrecognised lets the caller say it happened.
                    rules.Add(BlockEverything(modeId));
                    break;
            }

            return rules;
        }

        /// <summary>Whether <see cref="For"/> fell through to its default arm. The caller logs it;
        /// the rules it returns are safe either way.</summary>
        public static bool IsUnrecognised(FirewallMode mode) => !FirewallModes.IsOperatingMode(mode);

        private static RuleDef BlockEverything(Guid modeId)
            => new(modeId, "Block everything", GlobalSubject.Instance, RuleAction.Block, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultBlock)
            {
                IsModeDefault = true
            };

        private static RuleDef AllowEverything(Guid modeId)
            => new(modeId, "Allow everything", GlobalSubject.Instance, RuleAction.Allow, RuleDirection.InOut, Protocol.Any, (ulong)FilterWeights.DefaultPermit)
            {
                IsModeDefault = true
            };
    }
}
