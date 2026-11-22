using SimpleDeFence;
using System;
using System.IO;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class PathPatternTests
    {
        [Theory]
        [InlineData(@"C:\Tools", @"C:\Tools\**")]
        [InlineData(@"C:\Tools\", @"C:\Tools\**")]
        [InlineData(@"  ""C:\Tools""  ", @"C:\Tools\**")]
        [InlineData(@"C:/Tools/*/app.exe", @"C:\Tools\*\app.exe")]
        [InlineData(@"C:\Tools\\sub\*", @"C:\Tools\sub\*")]
        [InlineData(@"C:\Games\*\", @"C:\Games\*\**")]
        [InlineData(@"\\nas\share\apps", @"\\nas\share\apps\**")]
        public void Normalize_produces_the_stored_pattern(string input, string expected)
            => Assert.Equal(expected, PathPattern.Normalize(input));

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("*")]
        [InlineData("**")]
        [InlineData(@"C:\")]
        [InlineData(@"C:\**")]
        [InlineData(@"C:\*\app.exe")]
        [InlineData(@"Tools\app.exe")]
        [InlineData(@"\\nas\share")]
        [InlineData(@"*\app.exe")]
        public void Normalize_refuses_what_is_not_a_folder_rule(string input)
            => Assert.Null(PathPattern.Normalize(input));

        [Theory]
        [InlineData(@"C:\Tools\**", @"C:\Tools\a.exe", true)]
        [InlineData(@"C:\Tools\**", @"C:\Tools\x\y\z.exe", true)]
        [InlineData(@"C:\Tools\**", @"c:\tools\A.EXE", true)]
        [InlineData(@"C:\Tools\**", @"C:\ToolsX\a.exe", false)]
        [InlineData(@"C:\Tools\**", @"C:\Other\a.exe", false)]
        [InlineData(@"C:\Tools\*\app.exe", @"C:\Tools\v1\app.exe", true)]
        [InlineData(@"C:\Tools\*\app.exe", @"C:\Tools\app.exe", false)]
        [InlineData(@"C:\Tools\*\app.exe", @"C:\Tools\a\b\app.exe", false)]
        [InlineData(@"C:\Tools\**\app.exe", @"C:\Tools\app.exe", true)]
        [InlineData(@"C:\Tools\**\app.exe", @"C:\Tools\a\b\app.exe", true)]
        [InlineData(@"C:\Tools\**\app.exe", @"C:\Tools\a\b\other.exe", false)]
        [InlineData(@"C:\Tools\app?.exe", @"C:\Tools\app1.exe", true)]
        [InlineData(@"C:\Tools\app?.exe", @"C:\Tools\app12.exe", false)]
        [InlineData(@"C:\Program Files (x86)\Game+\*.exe", @"C:\Program Files (x86)\Game+\run.exe", true)]
        public void IsMatch_follows_the_wildcard_rules(string pattern, string path, bool expected)
            => Assert.Equal(expected, PathPattern.IsMatch(pattern, path));

        [Theory]
        [InlineData(@"C:\Tools\**", @"C:\Tools")]
        [InlineData(@"C:\Tools\*\app.exe", @"C:\Tools")]
        [InlineData(@"C:\Tools\sub\app.exe", @"C:\Tools\sub")]
        [InlineData(@"\\nas\share\apps\**", @"\\nas\share\apps")]
        [InlineData(@"C:\**", @"C:\")]
        public void BaseDirectory_is_the_part_before_the_first_wildcard(string pattern, string expected)
            => Assert.Equal(expected, PathPattern.BaseDirectory(pattern));

        [Fact]
        public void EnumerateMatches_finds_programs_on_disk_and_respects_the_limit()
        {
            var root = Path.Combine(Path.GetTempPath(), "sdf-pattern-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "a"));
                Directory.CreateDirectory(Path.Combine(root, "b", "c"));
                File.WriteAllText(Path.Combine(root, "top.exe"), "");
                File.WriteAllText(Path.Combine(root, "a", "app.exe"), "");
                File.WriteAllText(Path.Combine(root, "b", "c", "app.exe"), "");
                File.WriteAllText(Path.Combine(root, "a", "readme.txt"), "");

                Assert.Equal(3, PathPattern.EnumerateMatches(root + @"\**").Count);
                Assert.Single(PathPattern.EnumerateMatches(root + @"\*\app.exe"));
                Assert.Equal(2, PathPattern.EnumerateMatches(root + @"\**\app.exe").Count);
                Assert.Single(PathPattern.EnumerateMatches(root + @"\**", 1));
                Assert.Empty(PathPattern.EnumerateMatches(root + @"\missing\**"));
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        [Fact]
        public void Folder_rule_survives_a_save_and_load()
        {
            var ex = new FirewallExceptionV3(new PathPatternSubject(@"C:\Tools\**"), new UnrestrictedPolicy());
            var bytes = SerializationHelper.Serialize(ex);
            var back = SerializationHelper.Deserialize(bytes, new FirewallExceptionV3(GlobalSubject.Instance, new HardBlockPolicy()));

            var subject = Assert.IsType<PathPatternSubject>(back.Subject);
            Assert.Equal(@"C:\Tools\**", subject.Pattern);
            Assert.Equal(ex.Id, back.Id);
            Assert.IsType<UnrestrictedPolicy>(back.Policy);
        }

        [Fact]
        public void Folder_rule_is_described_by_its_folder_and_pattern()
        {
            var d = ExceptionDescriptor.Describe(new FirewallExceptionV3(new PathPatternSubject(@"C:\Games\*\game.exe"), new UnrestrictedPolicy()));
            Assert.Equal("Games", d.Name);
            Assert.Equal(@"C:\Games\*\game.exe", d.Detail);
        }

        [Fact]
        public void Folder_rule_is_never_turned_into_a_filter_directly()
        {
            // With no program set a filter covers every program, so a folder rule must be refused.
            Assert.Throws<NotImplementedException>(() =>
                new RuleDef(Guid.NewGuid(), "x", new PathPatternSubject(@"C:\Tools\**"), RuleAction.Allow, RuleDirection.Out, Protocol.Any, 0));
        }
    }
}
