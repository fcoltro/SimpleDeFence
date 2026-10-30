using System.Threading.Tasks;
using Xunit;

namespace SimpleDeFence.Tests
{
    public class IpLookupTests
    {
        // Trimmed from ARIN's answer for 8.8.8.8: no country on the network, the owner nested as a
        // registrant entity whose postal address ends in the country.
        private const string Arin = """
            {
              "objectClassName": "ip network",
              "handle": "NET-8-8-8-0-2",
              "name": "GOGL",
              "entities": [
                {
                  "objectClassName": "entity",
                  "handle": "GOGL",
                  "roles": ["registrant"],
                  "vcardArray": ["vcard", [
                    ["version", {}, "text", "4.0"],
                    ["fn", {}, "text", "Google LLC"],
                    ["adr", {"label": "1600 Amphitheatre Parkway\nMountain View\nCA\n94043\nUnited States"}, "text", ["", "", "", "", "", "", ""]],
                    ["kind", {}, "org", "org"]
                  ]],
                  "entities": [
                    { "roles": ["abuse"], "vcardArray": ["vcard", [["fn", {}, "text", "Abuse"]]] }
                  ]
                }
              ]
            }
            """;

        // Trimmed from RIPE's: a country code on the network, the owner behind a technical contact.
        private const string Ripe = """
            {
              "name": "RIPE-NCC",
              "country": "NL",
              "entities": [
                { "roles": ["technical"], "vcardArray": ["vcard", [["fn", {}, "text", "Ops Team"]]],
                  "entities": [
                    { "roles": ["registrant"], "vcardArray": ["vcard", [
                        ["fn", {}, "text", "Reseaux IP Europeens Network Coordination Centre (RIPE NCC)"],
                        ["adr", {"label": "P.O. Box 10096\n1001EB\nAmsterdam\nNETHERLANDS"}, "text", null]
                    ]] }
                  ] }
              ]
            }
            """;

        [Fact]
        public void An_Arin_record_gives_the_owner_the_network_and_the_country_from_the_address()
        {
            var details = IpLookup.ParseRdap("8.8.8.8", Arin, "dns.google");

            Assert.Equal("Google LLC", details.Organization);
            Assert.Equal("GOGL", details.Network);
            Assert.Equal("United States", details.Country);
            Assert.Equal("dns.google", details.HostName);
            Assert.False(details.IsLocal);
        }

        [Fact]
        public void A_country_code_on_the_network_wins_and_a_nested_registrant_is_found()
        {
            var details = IpLookup.ParseRdap("193.0.6.139", Ripe, null);

            Assert.Equal("Reseaux IP Europeens Network Coordination Centre (RIPE NCC)", details.Organization);
            Assert.Equal("Netherlands", details.Country);
            Assert.Null(details.HostName);
        }

        [Theory]
        [InlineData("SE", "Sweden")]
        [InlineData("NETHERLANDS", "Netherlands")]
        [InlineData("United States", "United States")]
        [InlineData("  ", null)]
        public void Country_names_are_made_readable(string input, string? expected)
        {
            Assert.Equal(expected, IpLookup.CountryName(input));
        }

        [Fact]
        public void Something_that_is_not_RDAP_yields_nothing_rather_than_throwing()
        {
            var details = IpLookup.ParseRdap("1.2.3.4", "<html>rate limited</html>", null);
            Assert.False(details.FoundAnything);
        }

        [Fact]
        public async Task A_local_address_is_never_sent_anywhere()
        {
            var details = await IpLookup.LookUpAsync("192.168.1.20");
            Assert.NotNull(details);
            Assert.True(details!.IsLocal);
            Assert.False(details.FoundAnything);
        }

        [Fact]
        public async Task Text_that_is_not_an_address_is_refused()
        {
            Assert.Null(await IpLookup.LookUpAsync("example.com/../admin"));
        }
    }
}
