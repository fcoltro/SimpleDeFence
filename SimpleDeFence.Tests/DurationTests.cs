using System;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>Temporary rules and Learning's time limit: the arithmetic the Rules list shows, and
    /// the wire field a GUI that predates it does not send.</summary>
    public class DurationTests
    {
        private static FirewallExceptionV3 Rule(AppExceptionTimer timer, DateTime created)
            => new(new ExecutableSubject(@"C:\Tools\setup.exe"), new UnrestrictedPolicy())
            {
                Timer = timer,
                CreationDate = created,
            };

        [Fact]
        public void A_timed_rule_expires_when_the_service_would_prune_it()
        {
            var created = new DateTime(2026, 10, 30, 9, 0, 0);

            Assert.Equal(created.AddMinutes(60), AppExceptionTimers.ExpiresAt(Rule(AppExceptionTimer.For_1_Hour, created)));
            Assert.Equal(created.AddHours(24), AppExceptionTimers.ExpiresAt(Rule(AppExceptionTimer.For_24_Hours, created)));
        }

        [Theory]
        [InlineData(AppExceptionTimer.Permanent)]
        [InlineData(AppExceptionTimer.Until_Reboot)]
        [InlineData(AppExceptionTimer.Invalid)]
        public void Rules_without_a_clock_time_have_no_expiry(AppExceptionTimer timer)
        {
            Assert.Null(AppExceptionTimers.ExpiresAt(Rule(timer, DateTime.Now)));
        }

        [Fact]
        public void Every_offered_duration_has_its_own_label()
        {
            var labels = new System.Collections.Generic.HashSet<string>();
            foreach (var timer in AppExceptionTimers.Offered)
                Assert.True(labels.Add(AppExceptionTimers.Label(timer)), $"{timer} shares its label with another duration.");

            Assert.Equal(AppExceptionTimer.Permanent, AppExceptionTimers.Offered[0]);
        }

        [Fact]
        public void The_rules_list_says_when_a_rule_stops_applying()
        {
            var permanent = ExceptionDescriptor.Describe(Rule(AppExceptionTimer.Permanent, DateTime.Now));
            var untilRestart = ExceptionDescriptor.Describe(Rule(AppExceptionTimer.Until_Reboot, DateTime.Now));
            var oneHour = ExceptionDescriptor.Describe(Rule(AppExceptionTimer.For_1_Hour, DateTime.Now));

            Assert.Equal("Unrestricted", permanent.Policy);
            Assert.Equal("Unrestricted, until restart", untilRestart.Policy);
            Assert.StartsWith("Unrestricted, until ", oneHour.Policy);
            Assert.NotEqual(untilRestart.Policy, oneHour.Policy);
        }

        [Fact]
        public void A_mode_switch_carries_the_learning_limit()
        {
            var bytes = SerializationHelper.Serialize<TwMessage>(TwMessageModeSwitch.CreateRequest(FirewallMode.Learning, 30));
            var back = Assert.IsType<TwMessageModeSwitch>(SerializationHelper.Deserialize<TwMessage>(bytes, TwMessageComError.Instance));

            Assert.Equal(FirewallMode.Learning, back.Mode);
            Assert.Equal(30, back.LearningMinutes);
        }

        /// <summary>A GUI from before the limit existed sends no LearningMinutes at all. That has to
        /// read as "no limit", not fail to parse - the service would refuse the switch.</summary>
        [Fact]
        public void A_mode_switch_without_the_field_means_no_limit()
        {
            var bytes = SerializationHelper.Serialize<TwMessage>(TwMessageModeSwitch.CreateRequest(FirewallMode.Learning, 30));
            var json = JsonNode.Parse(bytes)!.AsObject();
            Assert.True(json.Remove(nameof(TwMessageModeSwitch.LearningMinutes)));

            var old = Encoding.UTF8.GetBytes(json.ToJsonString());
            var back = Assert.IsType<TwMessageModeSwitch>(SerializationHelper.Deserialize<TwMessage>(old, TwMessageComError.Instance));

            Assert.Equal(FirewallMode.Learning, back.Mode);
            Assert.Equal(0, back.LearningMinutes);
        }
    }
}
