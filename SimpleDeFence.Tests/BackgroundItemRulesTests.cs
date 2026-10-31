using SimpleDeFence;
using System;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class BackgroundItemRulesTests
    {
        [Theory]
        [InlineData("\"C:\\Program Files (x86)\\Common Files\\Adobe\\AdobeGCClient\\AGSService.exe\"", @"C:\Program Files (x86)\Common Files\Adobe\AdobeGCClient\AGSService.exe")]
        [InlineData("\"C:\\Program Files\\Autodesk\\AdskAccess.exe\" --service", @"C:\Program Files\Autodesk\AdskAccess.exe")]
        [InlineData(@"C:\Program Files\Vendor App\updater.exe /silent", @"C:\Program Files\Vendor App\updater.exe")]
        [InlineData(@"\??\C:\Vendor\agent.exe", @"C:\Vendor\agent.exe")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ExecutableOf_finds_the_program_in_a_command_line(string? commandLine, string expected)
        {
            Assert.Equal(expected, BackgroundItemRules.ExecutableOf(commandLine));
        }

        [Fact]
        public void ExecutableOf_expands_variables_before_reading_the_path()
        {
            var result = BackgroundItemRules.ExecutableOf("%ProgramFiles%\\Adobe\\cc.exe -x", s => s.Replace("%ProgramFiles%", @"C:\Program Files"));
            Assert.Equal(@"C:\Program Files\Adobe\cc.exe", result);
        }

        [Fact]
        public void Microsoft_is_never_offered()
        {
            Assert.False(BackgroundItemRules.MayQuiet("Microsoft Windows"));
            Assert.False(BackgroundItemRules.MayQuiet("Microsoft Corporation"));
            Assert.False(BackgroundItemRules.MayQuiet(""));
            Assert.False(BackgroundItemRules.MayQuiet(null));
            Assert.True(BackgroundItemRules.MayQuiet("Adobe Inc."));
            Assert.True(BackgroundItemRules.MayQuiet("Autodesk, Inc."));
        }

        [Fact]
        public void Licence_checkers_are_marked_as_needed()
        {
            Assert.True(BackgroundItemRules.NeededToOpen(@"C:\Program Files (x86)\Common Files\Autodesk Shared\AdskLicensing\Current\AdskLicensingService\AdskLicensingService.exe"));
            Assert.False(BackgroundItemRules.NeededToOpen(@"C:\Program Files (x86)\Common Files\Adobe\AdobeGCClient\AGSService.exe"));
            Assert.False(BackgroundItemRules.NeededToOpen(""));
        }

        [Fact]
        public void StartupApproved_values_round_trip()
        {
            var now = DateTime.UtcNow;
            Assert.True(BackgroundItemRules.StartupApprovedIsOff(BackgroundItemRules.StartupApprovedValue(false, now)));
            Assert.False(BackgroundItemRules.StartupApprovedIsOff(BackgroundItemRules.StartupApprovedValue(true, now)));
            Assert.False(BackgroundItemRules.StartupApprovedIsOff(null));
            Assert.Equal(12, BackgroundItemRules.StartupApprovedValue(false, now).Length);
        }

        [Fact]
        public void StartupApproved_reads_what_Task_Manager_writes()
        {
            Assert.False(BackgroundItemRules.StartupApprovedIsOff(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.False(BackgroundItemRules.StartupApprovedIsOff(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
            Assert.True(BackgroundItemRules.StartupApprovedIsOff(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }));
        }

        [Fact]
        public void Programs_in_the_Windows_folder_are_recognised()
        {
            Assert.True(BackgroundItemRules.IsInWindowsFolder(@"C:\Windows\System32\svchost.exe", @"C:\Windows"));
            Assert.False(BackgroundItemRules.IsInWindowsFolder(@"C:\WindowsApps\x.exe", @"C:\Windows"));
            Assert.False(BackgroundItemRules.IsInWindowsFolder(@"C:\Program Files\Adobe\x.exe", @"C:\Windows\"));
        }

        [Fact]
        public void Publisher_match_ignores_case_and_never_matches_empty()
        {
            Assert.True(BackgroundItemRules.SamePublisher("Adobe Inc.", "ADOBE INC."));
            Assert.False(BackgroundItemRules.SamePublisher("", ""));
            Assert.False(BackgroundItemRules.SamePublisher("Adobe Inc.", "Autodesk, Inc."));
        }

        [Fact]
        public void Neighbours_name_the_publisher_most_of_them_are_signed_by()
        {
            var publishers = new[] { "Adobe Inc.", null, "Adobe Inc.", "Intel Corporation", "adobe inc." };
            Assert.Equal("Adobe Inc.", BackgroundItemRules.PublisherFromNeighbours(publishers));
        }

        [Fact]
        public void One_signed_neighbour_is_not_enough()
        {
            Assert.Equal("", BackgroundItemRules.PublisherFromNeighbours(new[] { "Adobe Inc.", null, null }));
            Assert.Equal("", BackgroundItemRules.PublisherFromNeighbours(Array.Empty<string?>()));
        }

        [Fact]
        public void A_tie_between_neighbours_names_no_one()
        {
            var publishers = new[] { "Adobe Inc.", "Adobe Inc.", "Autodesk, Inc.", "Autodesk, Inc." };
            Assert.Equal("", BackgroundItemRules.PublisherFromNeighbours(publishers));
        }

        [Fact]
        public void Microsoft_neighbours_never_count()
        {
            var publishers = new[] { "Microsoft Corporation", "Microsoft Corporation", "Microsoft Corporation", "Adobe Inc." };
            Assert.Equal("", BackgroundItemRules.PublisherFromNeighbours(publishers));
        }

        private static readonly string[] Shared =
        {
            @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)",
            @"C:\Program Files\Common Files", @"C:\ProgramData",
        };

        [Theory]
        [InlineData(@"C:\")]
        [InlineData(@"D:\")]
        [InlineData(@"C:\Program Files")]
        [InlineData(@"C:\Program Files\")]
        [InlineData(@"C:\program files (x86)")]
        [InlineData(@"C:\ProgramData")]
        [InlineData(@"C:\Users")]
        [InlineData(@"C:\Users\fabio")]
        [InlineData(@"C:\Users\fabio\Downloads")]
        [InlineData(@"C:\Users\fabio\Desktop")]
        [InlineData(@"C:\Users\fabio\AppData\Local")]
        [InlineData(@"C:\Users\fabio\AppData\Local\Programs")]
        [InlineData(@"C:\Users\fabio\AppData\Roaming")]
        [InlineData("")]
        public void Shared_folders_are_too_broad(string folder)
        {
            Assert.True(BackgroundItemRules.FolderTooBroad(folder, Shared, @"C:\Users"));
        }

        [Theory]
        [InlineData(@"C:\Program Files\Vendor App")]
        [InlineData(@"C:\Program Files\Adobe\Adobe Illustrator 2026\Support Files\Contents\Windows")]
        [InlineData(@"C:\Program Files (x86)\Some Game\bin")]
        [InlineData(@"C:\ProgramData\Vendor\Updater")]
        [InlineData(@"C:\Users\fabio\AppData\Local\Programs\Tool")]
        [InlineData(@"C:\Users\fabio\Downloads\portable-app")]
        [InlineData(@"D:\Games\Something")]
        public void A_program_s_own_folder_is_not(string folder)
        {
            Assert.False(BackgroundItemRules.FolderTooBroad(folder, Shared, @"C:\Users"));
        }

        [Fact]
        public void IsUnder_needs_a_whole_folder_name()
        {
            Assert.True(BackgroundItemRules.IsUnder(@"C:\Program Files\Vendor\a.exe", @"C:\Program Files\Vendor"));
            Assert.True(BackgroundItemRules.IsUnder(@"C:\Program Files\Vendor\x\a.exe", @"C:\Program Files\Vendor\"));
            Assert.False(BackgroundItemRules.IsUnder(@"C:\Program Files\VendorTwo\a.exe", @"C:\Program Files\Vendor"));
        }
    }
}
