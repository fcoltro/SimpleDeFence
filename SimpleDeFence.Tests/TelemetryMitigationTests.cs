using System;
using System.Collections.Generic;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class TelemetryMitigationTests
    {
        /// <summary>A machine made of dictionaries. Anything not listed is "not installed".</summary>
        private sealed class FakeSystem : ITelemetrySystem
        {
            public readonly Dictionary<string, uint> Services = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, bool> Tasks = new(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> Policies = new(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Refuse = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Policy values present under another type, such as a REG_SZ.</summary>
            public readonly HashSet<string> WrongType = new(StringComparer.OrdinalIgnoreCase);

            public uint? GetServiceStartMode(string service) => Services.TryGetValue(service, out var m) ? m : null;

            public void SetServiceStartMode(string service, uint startMode)
            {
                if (Refuse.Contains(service)) throw new UnauthorizedAccessException("refused");
                Services[service] = startMode;
            }

            public void StopService(string service) => Running.Remove(service);

            public bool? GetTaskEnabled(string taskPath) => Tasks.TryGetValue(taskPath, out var e) ? e : null;

            public void SetTaskEnabled(string taskPath, bool enabled)
            {
                if (Refuse.Contains(taskPath)) throw new UnauthorizedAccessException("refused");
                Tasks[taskPath] = enabled;
            }

            public int? GetPolicyValue(string key, string name)
            {
                if (WrongType.Contains(key + "|" + name)) throw new System.IO.InvalidDataException("not a DWORD");
                return Policies.TryGetValue(key + "|" + name, out var v) ? v : null;
            }

            public void SetPolicyValue(string key, string name, int? value)
            {
                if (value is int v) Policies[key + "|" + name] = v;
                else Policies.Remove(key + "|" + name);
            }
        }

        private static readonly string Appraiser = TelemetryMitigation.Tasks[0];
        private static readonly string CeipConsolidator = @"\Microsoft\Windows\Customer Experience Improvement Program\Consolidator";

        /// <summary>A typical Home install: both services automatic and running, two tasks on,
        /// one policy value already set by the user to something of their own.</summary>
        private static FakeSystem TypicalMachine()
        {
            var sys = new FakeSystem();
            sys.Services["DiagTrack"] = 2;
            sys.Services["dmwappushservice"] = 3;
            sys.Running.Add("DiagTrack");
            sys.Tasks[Appraiser] = true;
            sys.Tasks[CeipConsolidator] = true;
            sys.Policies[@"SOFTWARE\Policies\Microsoft\Windows\DataCollection|AllowTelemetry"] = 3;
            return sys;
        }

        private static TelemetryBackup TurnOn(FakeSystem sys, List<string>? failures = null)
        {
            failures ??= new List<string>();
            var backup = TelemetryMitigation.CaptureOriginals(sys, null, failures);
            TelemetryMitigation.Apply(sys, backup, failures);
            return backup;
        }

        [Fact]
        public void Never_turned_on_is_off()
        {
            Assert.Equal(TelemetryStatus.Off, TelemetryMitigation.StatusOf(TypicalMachine(), null));
        }

        [Fact]
        public void Turning_on_disables_services_and_tasks_and_sets_policies()
        {
            var sys = TypicalMachine();
            var failures = new List<string>();
            var backup = TurnOn(sys, failures);

            Assert.Empty(failures);
            Assert.Equal(TelemetryMitigation.SERVICE_DISABLED, sys.Services["DiagTrack"]);
            Assert.Equal(TelemetryMitigation.SERVICE_DISABLED, sys.Services["dmwappushservice"]);
            Assert.DoesNotContain("DiagTrack", sys.Running);
            Assert.False(sys.Tasks[Appraiser]);
            Assert.False(sys.Tasks[CeipConsolidator]);
            foreach (var policy in TelemetryMitigation.Policies)
                Assert.Equal(policy.Value, sys.GetPolicyValue(policy.Key, policy.Name));
            Assert.Equal(TelemetryStatus.On, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void Items_missing_on_this_edition_are_skipped_not_created()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            // Only the two tasks that exist were recorded or touched.
            Assert.Equal(2, backup.Tasks.Count);
            Assert.Equal(2, sys.Tasks.Count);
            Assert.Equal(TelemetryStatus.On, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void Undo_puts_back_exactly_what_was_there()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            var failures = new List<string>();
            TelemetryMitigation.Undo(sys, backup, failures);

            Assert.Empty(failures);
            Assert.True(backup.IsEmpty);
            Assert.Equal(2u, sys.Services["DiagTrack"]);
            Assert.Equal(3u, sys.Services["dmwappushservice"]);
            Assert.True(sys.Tasks[Appraiser]);
            Assert.True(sys.Tasks[CeipConsolidator]);

            // The user's own value comes back, and values that were not there are removed again.
            Assert.Equal(3, sys.GetPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"));
            Assert.Single(sys.Policies);
        }

        [Fact]
        public void A_task_turned_back_on_by_Windows_shows_as_partial()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            sys.Tasks[Appraiser] = true;

            Assert.Equal(TelemetryStatus.Partial, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void A_task_Windows_removed_altogether_does_not_count_against_it()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            sys.Tasks.Remove(Appraiser);

            Assert.Equal(TelemetryStatus.On, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void Applying_again_keeps_the_original_values_for_undo()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            // Windows turns a task back on, and the user applies again.
            sys.Tasks[Appraiser] = true;
            var again = TelemetryMitigation.CaptureOriginals(sys, backup, new List<string>());
            TelemetryMitigation.Apply(sys, again, new List<string>());
            Assert.Equal(TelemetryStatus.On, TelemetryMitigation.StatusOf(sys, again));

            // What Undo restores is still the machine from before the first apply, not the
            // mitigation's own values.
            TelemetryMitigation.Undo(sys, again, new List<string>());
            Assert.Equal(2u, sys.Services["DiagTrack"]);
            Assert.Equal(3, sys.GetPolicyValue(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry"));
        }

        [Fact]
        public void A_refused_step_is_reported_and_the_rest_still_happens()
        {
            var sys = TypicalMachine();
            sys.Refuse.Add(Appraiser);
            var failures = new List<string>();
            var backup = TurnOn(sys, failures);

            Assert.Single(failures);
            Assert.Contains(Appraiser, failures[0]);
            Assert.False(sys.Tasks[CeipConsolidator]);
            Assert.Equal(TelemetryMitigation.SERVICE_DISABLED, sys.Services["DiagTrack"]);
            Assert.Equal(TelemetryStatus.Partial, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void What_undo_could_not_restore_stays_in_the_backup()
        {
            var sys = TypicalMachine();
            var backup = TurnOn(sys);

            sys.Refuse.Add("DiagTrack");
            var failures = new List<string>();
            TelemetryMitigation.Undo(sys, backup, failures);

            Assert.Single(failures);
            Assert.False(backup.IsEmpty);
            Assert.True(backup.Services.ContainsKey("DiagTrack"));
            Assert.Single(backup.Services);
            Assert.Empty(backup.Tasks);

            // The user asked for it off, so what is left does not read as On.
            Assert.Equal(TelemetryStatus.Partial, TelemetryMitigation.StatusOf(sys, backup));
        }

        [Fact]
        public void A_policy_value_of_another_type_is_left_alone_by_apply_and_undo()
        {
            const string AllowTelemetry = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection|AllowTelemetry";
            var sys = TypicalMachine();
            sys.Policies.Remove(AllowTelemetry);
            sys.WrongType.Add(AllowTelemetry);

            var failures = new List<string>();
            var backup = TurnOn(sys, failures);

            Assert.Single(failures);
            Assert.False(backup.Policies.ContainsKey(AllowTelemetry));
            Assert.False(sys.Policies.ContainsKey(AllowTelemetry));

            // Undo must not delete a value it never recorded.
            sys.WrongType.Clear();
            sys.Policies[AllowTelemetry] = 7;
            TelemetryMitigation.Undo(sys, backup, new List<string>());
            Assert.Equal(7, sys.Policies[AllowTelemetry]);
        }
    }
}
