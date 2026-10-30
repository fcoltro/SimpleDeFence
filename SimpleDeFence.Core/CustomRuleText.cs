using System;
using System.Collections.Generic;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    /// <summary>
    /// Reads the port and address fields of the custom rule editor into the strings a RuleDef
    /// holds, refusing what the service could not turn into a filter.
    ///
    /// The service splits these on commas and parses each piece while it installs rules
    /// (SimpleDeFenceService.ConstructFilter), where a bad port throws and a bad address is
    /// skipped - either way the rule the user wrote is not the rule that runs. Checking here, when
    /// Apply is pressed, is the only point at which the user can still be told.
    ///
    /// Blank, "*" and "any" all mean "no restriction", stored as null: the service treats an
    /// empty field as unrestricted and asserts it never sees "*".
    /// </summary>
    public static class CustomRuleText
    {
        /// <summary>The address keywords the service understands, in the spelling it compares
        /// against (ordinally).</summary>
        public static readonly IReadOnlyList<string> AddressKeywords = new[] { RuleDef.LOCALSUBNET_ID, "DefaultGateway", "DNS" };

        /// <summary>Ports and ranges: "80", "80,443", "8000-8100". Normalised to the same text with
        /// the spaces taken out.</summary>
        public static bool TryNormalizePorts(string? text, out string? normalized)
        {
            normalized = null;
            if (IsAny(text))
                return true;

            var parts = new List<string>();
            foreach (var raw in text!.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0)
                    continue;

                var dash = part.IndexOf('-');
                if (dash < 0)
                {
                    if (!TryPort(part, out var port))
                        return false;
                    parts.Add($"{port}");
                }
                else
                {
                    if (!TryPort(part.Substring(0, dash).Trim(), out var min) || !TryPort(part.Substring(dash + 1).Trim(), out var max) || min > max)
                        return false;
                    parts.Add(min == max ? $"{min}" : $"{min}-{max}");
                }
            }

            if (parts.Count == 0)
                return true;
            normalized = string.Join(",", parts);
            return true;
        }

        /// <summary>Addresses: single IPv4 or IPv6 addresses, CIDR networks ("10.0.0.0/8"), and
        /// the keywords LocalSubnet, DefaultGateway and DNS in any case.</summary>
        public static bool TryNormalizeAddresses(string? text, out string? normalized)
        {
            normalized = null;
            if (IsAny(text))
                return true;

            var parts = new List<string>();
            foreach (var raw in text!.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0)
                    continue;

                var keyword = Keyword(part);
                if (keyword is not null)
                {
                    parts.Add(keyword);
                    continue;
                }

                // IPAddress.Parse takes "10" for 0.0.0.10 and "10.1" for 10.0.0.1, which is never
                // what someone typing into this box meant. An IPv4 address has to be written out.
                var address = part.Split('/')[0];
                if (!address.Contains(':') && address.Split('.').Length != 4)
                    return false;

                try
                {
                    // Parse range-checks the prefix length too ("/33" on IPv4 throws).
                    parts.Add(IpAddrMask.Parse(part).ToString());
                }
                catch
                {
                    return false;
                }
            }

            if (parts.Count == 0)
                return true;
            normalized = string.Join(",", parts);
            return true;
        }

        /// <summary>Whether a protocol carries ports. Port conditions on any other protocol would
        /// never match, so the editor greys the port fields out for them.</summary>
        public static bool HasPorts(Protocol protocol)
            => protocol is Protocol.TCP or Protocol.UDP or Protocol.TcpUdp;

        private static bool IsAny(string? text)
        {
            var t = text?.Trim();
            return string.IsNullOrEmpty(t) || t == "*" || t.Equals("any", StringComparison.OrdinalIgnoreCase);
        }

        private static string? Keyword(string text)
        {
            foreach (var keyword in AddressKeywords)
            {
                if (keyword.Equals(text, StringComparison.OrdinalIgnoreCase))
                    return keyword;
            }
            return null;
        }

        private static bool TryPort(string text, out ushort port)
        {
            port = 0;
            if (text.Length == 0 || text.Length > 5)
                return false;
            foreach (var c in text)
            {
                if (c < '0' || c > '9')
                    return false;
            }
            if (!int.TryParse(text, out var value) || value > ushort.MaxValue)
                return false;
            port = (ushort)value;
            return true;
        }
    }
}
