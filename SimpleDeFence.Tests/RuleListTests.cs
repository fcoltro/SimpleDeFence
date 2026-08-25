using SimpleDeFence;
using SimpleDeFence.DatabaseClasses;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class RuleListTests
    {
        private static Application Special(string name, bool recommended, bool hidden = false)
        {
            var app = new Application { Name = name };
            app.Flags!["TWUI:SPECIAL"] = null;
            if (recommended) app.Flags["TWUI:RECOMMENDED"] = null;
            if (hidden) app.Flags["TWUI:HIDDEN"] = null;
            return app;
        }

        private static FirewallExceptionV3 ExeRule(string path)
            => new(new ExecutableSubject(path), new TcpUdpPolicy(true));

        [Fact]
        public void Build_groups_applications_and_splits_special_by_recommendation()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\a.exe"));
            profile.SpecialExceptions.Add("Windows_Update");

            var db = new AppDatabase(new List<Application>
            {
                Special("Windows_Update", recommended: true),
                Special("Gaming", recommended: false),
                Special("Secret", recommended: true, hidden: true),
                new Application { Name = "NotSpecial" }, // no TWUI:Special flag
            });

            var rows = RuleListBuilder.Build(profile, db);

            Assert.Equal(3, rows.Count); // hidden and non-special definitions never appear
            Assert.Equal(RuleGroup.Applications, rows.Single(r => r.Name == "a.exe").Group);
            var winUpd = rows.Single(r => r.SpecialId == "Windows_Update");
            Assert.Equal(RuleGroup.SpecialRecommended, winUpd.Group);
            Assert.True(winUpd.Enabled);
            var gaming = rows.Single(r => r.SpecialId == "Gaming");
            Assert.Equal(RuleGroup.SpecialOptional, gaming.Group);
            Assert.False(gaming.Enabled);
        }

        [Fact]
        public void Build_sorts_applications_by_name_case_insensitively()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\zed.exe"));
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\Alpha.exe"));

            var rows = RuleListBuilder.Build(profile, new AppDatabase());

            Assert.Equal(new[] { "Alpha.exe", "zed.exe" }, rows.Where(r => r.Group == RuleGroup.Applications).Select(r => r.Name).ToArray());
        }

        [Fact]
        public void Filter_matches_name_and_detail_case_insensitively()
        {
            var profile = new ServerProfileConfiguration("Default");
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\Firefox\firefox.exe"));
            profile.AppExceptions.Add(ExeRule(@"C:\Apps\git.exe"));

            var rows = RuleListBuilder.Build(profile, new AppDatabase());

            Assert.Single(RuleListBuilder.Filter(rows, "FIRE"));
            Assert.Single(RuleListBuilder.Filter(rows, @"C:\Apps\git"));
            Assert.Equal(2, RuleListBuilder.Filter(rows, "").Count);
        }

        [Fact]
        public void ApplyPreset_keeps_id_and_swaps_policy()
        {
            var original = ExeRule(@"C:\Apps\a.exe");
            var edited = RuleEdit.ApplyPreset(original, new HardBlockPolicy());

            Assert.Equal(original.Id, edited.Id);
            Assert.Equal(PolicyType.HardBlock, edited.Policy.PolicyType);
            Assert.Same(original.Subject, edited.Subject);
        }

        [Fact]
        public void ApplyPreset_refuses_rule_list_policies()
        {
            var original = new FirewallExceptionV3(new ExecutableSubject(@"C:\Apps\a.exe"), new RuleListPolicy());

            Assert.Throws<InvalidOperationException>(() => RuleEdit.ApplyPreset(original, new UnrestrictedPolicy()));
        }

        [Fact]
        public void RemoveExceptions_removes_exactly_the_given_ids()
        {
            var profile = new ServerProfileConfiguration("Default");
            var keep = ExeRule(@"C:\Apps\keep.exe");
            var drop = ExeRule(@"C:\Apps\drop.exe");
            profile.AppExceptions.Add(keep);
            profile.AppExceptions.Add(drop);

            RuleEdit.RemoveExceptions(profile, new[] { drop.Id });

            Assert.Single(profile.AppExceptions);
            Assert.Equal(keep.Id, profile.AppExceptions[0].Id);
        }

        [Fact]
        public void SetSpecialEnabled_adds_once_and_removes()
        {
            var profile = new ServerProfileConfiguration("Default");

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", true);
            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", true); // idempotent
            Assert.Single(profile.SpecialExceptions);

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", false);
            Assert.Empty(profile.SpecialExceptions);

            RuleEdit.SetSpecialEnabled(profile, "Windows_Update", false); // removing absent is a no-op
            Assert.Empty(profile.SpecialExceptions);
        }
    }
}