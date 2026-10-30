using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    /// <summary>What a lookup found about one remote address. Any part can be null: plenty of
    /// addresses have no reverse name, and the registries do not all publish the same fields.</summary>
    public sealed record IpDetails(string Address, bool IsLocal, string? HostName, string? Organization, string? Network, string? Country)
    {
        public bool FoundAnything => HostName is not null || Organization is not null || Network is not null || Country is not null;
    }

    /// <summary>
    /// Answers "who is this address?" for the Connections page, and only when the user asks.
    ///
    /// Two sources, both free and keyless. The reverse name comes from the DNS server the machine
    /// already uses. The owner, network name and country come from RDAP - the registries' own
    /// JSON replacement for whois - through rdap.org, which redirects each address to the registry
    /// that holds it (ARIN, RIPE, APNIC, LACNIC or AFRINIC).
    ///
    /// Nothing runs in the background: SimpleDeFence says it collects no data, and sending every
    /// address a machine talks to off to a third party would quietly break that. Each lookup is
    /// one click, the addresses go only to those two places, and answers are kept in memory for
    /// the session so a second click costs nothing.
    ///
    /// The country is where the network is registered, not a street address for the server. That
    /// is as close as a source with no account and no tracking gets, and it is labelled as such.
    /// </summary>
    public static class IpLookup
    {
        /// <summary>Most addresses one click looks up. A scan can hit hundreds; asking a registry
        /// about all of them would be slow for the user and rude to a free service.</summary>
        public const int MaxPerRequest = 20;

        private const int MaxCached = 1024;
        private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(4);

        private static readonly HttpClient Client = CreateClient();
        private static readonly ConcurrentDictionary<string, IpDetails> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0";
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SimpleDeFence", version));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/rdap+json"));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return client;
        }

        /// <summary>Looks one address up. Never throws for a failed lookup - an address nothing
        /// is known about comes back with every field null - and returns null only for text that
        /// is not an IP address at all, which is never sent anywhere.</summary>
        public static async Task<IpDetails?> LookUpAsync(string address, CancellationToken cancellationToken = default)
        {
            if (!IPAddress.TryParse(address, out var ip))
                return null;

            // Canonical form, so "::ffff:1.2.3.4"-style variants of one address share a cache
            // entry and the URL below is built from a value we produced rather than one we were given.
            if (ip.IsIPv4MappedToIPv6)
                ip = ip.MapToIPv4();
            string key = ip.ToString();

            if (Cache.TryGetValue(key, out var cached))
                return cached;

            // A private or loopback address is nobody's business but this network's: there is
            // nothing a public registry could say about it, and asking would leak the LAN layout.
            if (IpAddrMask.IsLocalNetwork(ip))
                return Remember(new IpDetails(key, IsLocal: true, null, null, null, null));

            var hostTask = ReverseNameAsync(ip);
            var rdapTask = RdapAsync(key, cancellationToken);
            await Task.WhenAll(hostTask, rdapTask).ConfigureAwait(false);

            var host = hostTask.Result;
            var details = rdapTask.Result is string json
                ? ParseRdap(key, json, host)
                : new IpDetails(key, IsLocal: false, host, null, null, null);

            // A lookup that found nothing may have failed on the way (offline, timed out), so it
            // is not remembered: the next click tries again.
            return details.FoundAnything ? Remember(details) : details;
        }

        private static IpDetails Remember(IpDetails details)
        {
            if (Cache.Count >= MaxCached)
                Cache.Clear();
            Cache[details.Address] = details;
            return details;
        }

        private static async Task<string?> ReverseNameAsync(IPAddress ip)
        {
            try
            {
                var lookup = Dns.GetHostEntryAsync(ip);
                if (await Task.WhenAny(lookup, Task.Delay(DnsTimeout)).ConfigureAwait(false) != lookup)
                    return null;

                var name = (await lookup.ConfigureAwait(false)).HostName;
                // Windows hands the address back as its own "name" when there is no PTR record.
                return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _) ? null : name.TrimEnd('.');
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> RdapAsync(string address, CancellationToken cancellationToken)
        {
            try
            {
                using var response = await Client.GetAsync("https://rdap.org/ip/" + Uri.EscapeDataString(address), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads an RDAP IP network response (RFC 9083). Public for the tests.
        ///
        /// Organization is the registrant's name from its vCard, searched depth-first because some
        /// registries nest the organisation under another entity. Network is the network's own
        /// name, which is often the only readable thing an ARIN record has besides the owner.
        /// Country is the network's registered country when the registry gives one, which RIPE,
        /// APNIC and LACNIC do; ARIN does not, so it falls back to the last line of the
        /// registrant's postal address, which is where every registry puts the country.
        /// </summary>
        public static IpDetails ParseRdap(string address, string json, string? hostName)
        {
            string? organization = null, network = null, country = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                network = NonEmpty(StringProperty(root, "name"));
                country = CountryName(StringProperty(root, "country"));

                if (root.TryGetProperty("entities", out var entities) && FindRegistrant(entities) is { } registrant)
                {
                    organization = NonEmpty(VCardText(registrant, "fn"));
                    country ??= CountryName(LastLine(VCardAddressLabel(registrant)));
                }
            }
            catch (JsonException)
            {
                // A registry that answers with something other than RDAP tells us nothing.
            }

            return new IpDetails(address, IsLocal: false, hostName, organization, network, country);
        }

        private static JsonElement? FindRegistrant(JsonElement entities)
        {
            if (entities.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var entity in entities.EnumerateArray())
            {
                if (entity.ValueKind != JsonValueKind.Object)
                    continue;

                if (entity.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                    && roles.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String
                        && string.Equals(r.GetString(), "registrant", StringComparison.OrdinalIgnoreCase)))
                    return entity;

                if (entity.TryGetProperty("entities", out var nested) && FindRegistrant(nested) is { } found)
                    return found;
            }

            return null;
        }

        /// <summary>The value of a text property in an entity's jCard (RFC 7095): each property is
        /// an array of name, parameters, type, value.</summary>
        private static string? VCardText(JsonElement entity, string name)
        {
            foreach (var property in VCardProperties(entity))
            {
                if (property.GetArrayLength() >= 4
                    && property[0].ValueKind == JsonValueKind.String && property[0].GetString() == name
                    && property[3].ValueKind == JsonValueKind.String)
                    return property[3].GetString();
            }
            return null;
        }

        private static string? VCardAddressLabel(JsonElement entity)
        {
            foreach (var property in VCardProperties(entity))
            {
                if (property.GetArrayLength() >= 2
                    && property[0].ValueKind == JsonValueKind.String && property[0].GetString() == "adr"
                    && property[1].ValueKind == JsonValueKind.Object)
                    return StringProperty(property[1], "label");
            }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<JsonElement> VCardProperties(JsonElement entity)
        {
            if (!entity.TryGetProperty("vcardArray", out var vcard) || vcard.ValueKind != JsonValueKind.Array
                || vcard.GetArrayLength() < 2 || vcard[1].ValueKind != JsonValueKind.Array)
                yield break;

            foreach (var property in vcard[1].EnumerateArray())
            {
                if (property.ValueKind == JsonValueKind.Array)
                    yield return property;
            }
        }

        private static string? StringProperty(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? NonEmpty(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string? LastLine(string? text)
            => text?.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

        /// <summary>A two-letter code becomes the country's name; a name written in capitals, as
        /// RIPE writes them, becomes "Netherlands" rather than "NETHERLANDS".</summary>
        internal static string? CountryName(string? value)
        {
            value = NonEmpty(value);
            if (value is null)
                return null;

            if (value.Length == 2 && value.All(char.IsLetter))
            {
                try { return new RegionInfo(value).EnglishName; }
                catch (ArgumentException) { return value.ToUpperInvariant(); }
            }

            return value.Any(char.IsLower)
                ? value
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
        }
    }
}
