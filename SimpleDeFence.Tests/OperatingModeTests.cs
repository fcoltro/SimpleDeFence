using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>The service gates MODE_SWITCH on this before it will change mode, because a mode it
    /// does not recognise matches no case in AssembleActiveRules and so assembles no default rule at
    /// all - leaving user permits and nothing denying anything else. What counts as a real mode is
    /// decided here rather than assumed, so the service and the UI cannot disagree about it.</summary>
    public class OperatingModeTests
    {
        [Theory]
        [InlineData(FirewallMode.Normal)]
        [InlineData(FirewallMode.BlockAll)]
        [InlineData(FirewallMode.AllowOutgoing)]
        [InlineData(FirewallMode.Disabled)]
        [InlineData(FirewallMode.Learning)]
        public void The_five_real_modes_are_accepted(FirewallMode mode)
        {
            Assert.True(FirewallModes.IsOperatingMode(mode));
        }

        [Fact]
        public void Unknown_is_rejected_even_though_it_is_a_defined_value()
        {
            // Enum.IsDefined would pass this - Unknown is declared as 100. It is a client-side
            // "no state yet" sentinel and the initial value of ServerState.Mode, so a client of a
            // different build echoing its own state back must not be able to set it.
            Assert.True(System.Enum.IsDefined(typeof(FirewallMode), FirewallMode.Unknown));
            Assert.False(FirewallModes.IsOperatingMode(FirewallMode.Unknown));
        }

        [Theory]
        [InlineData(6)]
        [InlineData(42)]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void Values_off_the_end_of_the_enum_are_rejected(int raw)
        {
            // JSON deserialising an enum does not range-check, so the pipe can deliver any int.
            Assert.False(FirewallModes.IsOperatingMode((FirewallMode)raw));
        }

        [Fact]
        public void Every_selectable_mode_is_an_operating_mode()
        {
            // Keeps the two in step: anything offered in the UI must be something the service will
            // accept, or the chip would present a mode that MODE_SWITCH then refuses.
            foreach (var info in FirewallModes.Selectable)
                Assert.True(FirewallModes.IsOperatingMode(info.Mode), $"{info.Mode} is selectable but not an operating mode");
        }
    }
}
