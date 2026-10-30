using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SimpleDeFence
{
    /// <summary>
    /// "Allow every program from this publisher": the rules for trusting a code-signing
    /// certificate instead of one path. Kept free of Windows calls so they can be tested; the
    /// service supplies each program's signature.
    ///
    /// A publisher rule is what keeps an app working through its updates when the updater
    /// installs to a new versioned folder each time, and what covers the helper programs a large
    /// app starts that nobody would think to allow one by one. The service turns it into ordinary
    /// per-path rules for the programs it sees running or starting, so WFP itself only ever sees
    /// paths, as it does for every other rule.
    ///
    /// Only a signature Windows verifies counts. A program that merely carries the publisher's
    /// name in a broken or untrusted signature is not trusted, and programs inside the Windows
    /// folder are never trusted this way: they are Windows components, allowed or not through the
    /// Rules page's special exceptions, and trusting "Microsoft" must not open them all at once.
    /// </summary>
    public static class PublisherTrust
    {
        /// <summary>The subject the program should be trusted under, or null. certSubject and
        /// certValid are what the signed file reports; trusted is the profile's list.</summary>
        public static string? TrustedSubject(IEnumerable<string> trusted, string path, string? certSubject, bool certValid, string windowsDirectory)
        {
            if (!certValid || string.IsNullOrEmpty(certSubject) || string.IsNullOrEmpty(path))
                return null;

            if (!string.IsNullOrEmpty(windowsDirectory))
            {
                var windows = windowsDirectory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
                    return null;
            }

            foreach (var subject in trusted)
            {
                if (string.Equals(subject, certSubject, StringComparison.OrdinalIgnoreCase))
                    return subject;
            }
            return null;
        }

        /// <summary>
        /// The name to show for a certificate subject: its CN, else its O, else the whole subject.
        /// "CN=Discord Inc., O=Discord Inc., L=San Francisco, S=California, C=US" is shown as
        /// "Discord Inc.". A value with a comma in it comes quoted ("CN=\"Foo, Inc.\""), and the
        /// quotes are taken off.
        /// </summary>
        public static string DisplayName(string subject)
        {
            string? organization = null;
            foreach (var (key, value) in Parts(subject))
            {
                if (key.Equals("CN", StringComparison.OrdinalIgnoreCase) && value.Length != 0)
                    return value;
                if (key.Equals("O", StringComparison.OrdinalIgnoreCase) && value.Length != 0)
                    organization ??= value;
            }
            return organization ?? subject;
        }

        private static IEnumerable<(string Key, string Value)> Parts(string subject)
        {
            int i = 0;
            while (i < subject.Length)
            {
                while (i < subject.Length && (subject[i] == ' ' || subject[i] == ','))
                    ++i;

                int eq = subject.IndexOf('=', i);
                if (eq < 0)
                    yield break;
                var key = subject.Substring(i, eq - i).Trim();
                i = eq + 1;

                var value = new StringBuilder();
                if (i < subject.Length && subject[i] == '"')
                {
                    // Quoted: runs to the next quote not doubled ("" is a literal quote).
                    for (++i; i < subject.Length; ++i)
                    {
                        if (subject[i] == '"')
                        {
                            if (i + 1 < subject.Length && subject[i + 1] == '"') { value.Append('"'); ++i; continue; }
                            ++i;
                            break;
                        }
                        value.Append(subject[i]);
                    }
                    while (i < subject.Length && subject[i] != ',')
                        ++i;
                }
                else
                {
                    for (; i < subject.Length && subject[i] != ','; ++i)
                        value.Append(subject[i]);
                }

                yield return (key, value.ToString().Trim());
            }
        }
    }
}
