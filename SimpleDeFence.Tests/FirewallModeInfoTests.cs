using SimpleDeFence;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class FirewallModeInfoTests
    {
        [Fact]
        public void Selectable_lists_the_five_user_choosable_modes_in_order()
        {
            var modes = new System.Collections.Generic.List<FirewallMode>();
            foreach (var m in FirewallModes.Selectable)
                modes.Add(m.Mode);

            Assert.Equal(
                new[]
                {
                    FirewallMode.Normal,
                    FirewallMode.BlockAll,
                    FirewallMode.AllowOutgoing,
                    FirewallMode.Disabled,
                    FirewallMode.Learning,
                },
                modes);
        }

        [Theory]
        [InlineData(FirewallMode.Normal, "Normal")]
        [InlineData(FirewallMode.BlockAll, "Block all")]
        [InlineData(FirewallMode.AllowOutgoing, "Allow outgoing")]
        [InlineData(FirewallMode.Disabled, "Disabled")]
        [InlineData(FirewallMode.Learning, "Autolearn")]
        public void Labels_match_the_WinForms_wording(FirewallMode mode, string expected)
        {
            Assert.Equal(expected, FirewallModes.LabelFor(mode));
        }

        [Fact]
        public void Unknown_is_labelled_but_not_selectable()
        {
            Assert.Equal("Unknown", FirewallModes.LabelFor(FirewallMode.Unknown));
            Assert.Equal(-1, FirewallModes.IndexOf(FirewallMode.Unknown));
        }

        [Fact]
        public void IndexOf_matches_the_selectable_order()
        {
            Assert.Equal(0, FirewallModes.IndexOf(FirewallMode.Normal));
            Assert.Equal(4, FirewallModes.IndexOf(FirewallMode.Learning));
        }

        [Fact]
        public void Every_selectable_mode_has_a_non_empty_description()
        {
            foreach (var m in FirewallModes.Selectable)
                Assert.False(string.IsNullOrWhiteSpace(m.Description));
        }
    }
}
