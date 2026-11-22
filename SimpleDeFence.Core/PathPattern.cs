using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SimpleDeFence
{
    /// <summary>
    /// The path patterns behind folder rules, in the style Fort Firewall uses for its program
    /// paths: <c>*</c> matches any run of characters inside one folder name, <c>**</c> matches
    /// across any number of folders, and <c>?</c> matches one character. A plain folder path means
    /// every program in it and below it.
    ///
    /// The Filtering Platform only compares exact program paths, so a pattern never reaches WFP.
    /// The service turns it into one ordinary per-program rule for each program that matches: the
    /// ones on disk when the rules are built (<see cref="EnumerateMatches"/>), and the ones that
    /// start later (<see cref="IsMatch"/>, from the process-start watcher).
    /// </summary>
    public static class PathPattern
    {
        /// <summary>How many programs one pattern may expand to when the rules are built. Programs
        /// past it are still covered once they start, through the process-start watcher.</summary>
        public const int MaxExpandedFiles = 2000;

        private static readonly char[] Wildcards = { '*', '?' };

        /// <summary>
        /// The environment variables a pattern may use. Only the machine-wide ones: the service
        /// that applies the rule runs as SYSTEM, so a per-user variable such as %LOCALAPPDATA%
        /// would point at SYSTEM's own profile there and at the user's in this window - the rule
        /// would match one folder and the preview would count another.
        /// </summary>
        public static readonly string[] MachineVariables =
        {
            "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
            "CommonProgramFiles(x86)", "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
            "SystemRoot", "windir", "SystemDrive",
        };

        /// <summary>
        /// The pattern with its machine-wide variables (%ProgramFiles% and the like) replaced by
        /// their values. Any other %name% is left as written, so it simply matches nothing rather
        /// than meaning something different in the service than on screen.
        /// </summary>
        public static string Expand(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.IndexOf('%') < 0)
                return pattern;

            return Regex.Replace(pattern, @"%([^%\\]+)%", m =>
            {
                var name = m.Groups[1].Value;
                foreach (var known in MachineVariables)
                {
                    if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
                    {
                        var value = Environment.GetEnvironmentVariable(known);
                        return string.IsNullOrEmpty(value) ? m.Value : value.TrimEnd('\\');
                    }
                }
                return m.Value;
            });
        }

        /// <summary>
        /// The pattern as it is stored: trimmed, quotes removed, forward slashes turned into
        /// backslashes, and a plain folder turned into "folder\**". Returns null when the text
        /// cannot be a folder rule - not a full path, or a whole drive (which would allow or block
        /// every program on it, almost never what anyone means).
        /// </summary>
        public static string? Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var p = text.Trim().Trim('"').Trim().Replace('/', '\\');
            if (p.Length == 0)
                return null;

            // Collapse doubled separators (but keep a UNC path's leading pair).
            var lead = p.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\" : string.Empty;
            var rest = p.Substring(lead.Length);
            while (rest.Contains(@"\\"))
                rest = rest.Replace(@"\\", @"\");
            p = lead + rest;

            if (p.IndexOfAny(Wildcards) < 0)
                p = p.TrimEnd('\\') + @"\**";
            else if (p.EndsWith(@"\", StringComparison.Ordinal))
                p += "**";

            // Kept as typed (variables and all); checked as the service will see it.
            var baseDir = BaseDirectory(Expand(p));
            if (baseDir is null)
                return null;

            // A drive root ("C:\") or a bare share ("\\server\share") is too wide to mean anything.
            string? root;
            try { root = Path.GetPathRoot(baseDir); }
            catch (ArgumentException) { return null; }
            if (string.IsNullOrEmpty(root)
                || string.Equals(baseDir.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return null;

            return p;
        }

        /// <summary>
        /// The deepest folder that holds every match: the part of the pattern before its first
        /// wildcard segment. Null when the pattern is not a full path (no drive letter or UNC
        /// share before the first wildcard).
        /// </summary>
        public static string? BaseDirectory(string pattern)
        {
            var segments = pattern.Split('\\');
            var sb = new StringBuilder();
            int i = 0;
            if (pattern.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // "\\server\share\..." splits to "", "", "server", "share", ...
                if (segments.Length < 4 || segments[2].IndexOfAny(Wildcards) >= 0 || segments[3].IndexOfAny(Wildcards) >= 0
                    || segments[2].Length == 0 || segments[3].Length == 0)
                    return null;
                sb.Append(@"\\").Append(segments[2]).Append('\\').Append(segments[3]);
                i = 4;
            }
            else
            {
                if (segments.Length == 0 || segments[0].Length != 2 || segments[0][1] != ':' || !char.IsLetter(segments[0][0]))
                    return null;
                sb.Append(segments[0]);
                i = 1;
            }

            // The last segment is the file name part and never a folder of its own.
            for (; i < segments.Length - 1; ++i)
            {
                if (segments[i].IndexOfAny(Wildcards) >= 0)
                    break;
                sb.Append('\\').Append(segments[i]);
            }

            if (sb.Length == 2 && sb[1] == ':')
                sb.Append('\\');
            return sb.ToString();
        }

        /// <summary>The pattern as a case-insensitive regular expression over whole paths.</summary>
        public static Regex ToRegex(string pattern)
        {
            pattern = Expand(pattern);
            var sb = new StringBuilder("^");
            for (int i = 0; i < pattern.Length; ++i)
            {
                char c = pattern[i];
                if (c == '*')
                {
                    if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                    {
                        ++i;
                        // "\**\" also matches no folder at all, so "C:\Tools\**\app.exe" covers
                        // C:\Tools\app.exe as well as C:\Tools\a\b\app.exe.
                        if (i + 1 < pattern.Length && pattern[i + 1] == '\\')
                        {
                            ++i;
                            sb.Append(@"(?:.*\\)?");
                        }
                        else
                        {
                            sb.Append(".*");
                        }
                    }
                    else
                    {
                        sb.Append(@"[^\\]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append(@"[^\\]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        public static bool IsMatch(string pattern, string path)
        {
            if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(path))
                return false;
            return ToRegex(pattern).IsMatch(path);
        }

        /// <summary>
        /// The programs (.exe files) on disk that match <paramref name="pattern"/>, at most
        /// <paramref name="limit"/> of them. Folders that cannot be read are skipped, and so are
        /// junctions and symbolic links, so a link back up the tree cannot loop.
        /// </summary>
        public static List<string> EnumerateMatches(string pattern, int limit = MaxExpandedFiles)
        {
            var results = new List<string>();
            pattern = Expand(pattern);
            var baseDir = BaseDirectory(pattern);
            if (baseDir is null || limit <= 0)
                return results;

            try
            {
                if (!Directory.Exists(baseDir))
                    return results;

                var regex = ToRegex(pattern);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                foreach (var file in Directory.EnumerateFiles(baseDir, "*.exe", options))
                {
                    if (!regex.IsMatch(file))
                        continue;
                    results.Add(file);
                    if (results.Count >= limit)
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is System.Security.SecurityException)
            {
                // A drive that went away mid-scan: keep what was found.
            }

            return results;
        }
    }
}
