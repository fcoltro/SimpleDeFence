using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
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
        /// How specific a pattern is: the number of characters in it that are not wildcards, once
        /// its variables are expanded. When several folder rules match the same program the most
        /// specific one decides, so "Block C:\Tools\bad\**" wins over "Allow C:\Tools\**"
        /// whichever was added first.
        /// </summary>
        public static int Specificity(string pattern)
        {
            int n = 0;
            foreach (var c in Expand(pattern ?? string.Empty))
            {
                if (c != '*' && c != '?')
                    ++n;
            }
            return n;
        }

        /// <summary>
        /// Folder rules in the order the service tries them: most specific pattern first (see
        /// <see cref="Specificity"/>), and a block before an allow of the same specificity. The
        /// first one whose pattern matches a program decides for it. A rule with several folders
        /// gives one entry per folder, each placed by its own specificity. Rules that are not
        /// folder rules are left out.
        /// </summary>
        public static List<(string Pattern, FirewallExceptionV3 Rule)> InPrecedenceOrder(IEnumerable<FirewallExceptionV3> rules)
        {
            var entries = new List<(string Pattern, FirewallExceptionV3 Rule)>();
            foreach (var r in rules)
            {
                if (r.Subject is PathPatternSubject folder)
                {
                    foreach (var p in folder.UsablePatterns)
                        entries.Add((p, r));
                }
            }

            // List.Sort is not stable; OrderBy is, so equal entries keep the order they were added in.
            return System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.ThenBy(
                    System.Linq.Enumerable.OrderByDescending(entries, e => Specificity(e.Pattern)),
                    e => e.Rule.Policy is HardBlockPolicy ? 0 : 1));
        }

        /// <summary>
        /// How many folders below <see cref="BaseDirectory"/> a match can be, or -1 for any depth
        /// (the pattern has a **). "C:\Program Files\*\app.exe" only ever matches one folder
        /// down, so there is no reason to walk the rest of Program Files to find it.
        /// </summary>
        public static int MaxDepthBelowBase(string pattern)
        {
            pattern = Expand(pattern);
            if (pattern.Contains("**", StringComparison.Ordinal))
                return -1;
            var baseDir = BaseDirectory(pattern);
            if (baseDir is null)
                return -1;
            var rest = pattern.Substring(Math.Min(baseDir.TrimEnd('\\').Length, pattern.Length)).TrimStart('\\');
            int separators = 0;
            foreach (var c in rest)
            {
                if (c == '\\')
                    ++separators;
            }
            return separators;
        }

        /// <summary>
        /// The programs (.exe files) on disk that match <paramref name="pattern"/>, at most
        /// <paramref name="limit"/> of them. Folders that cannot be read are skipped, and so are
        /// junctions and symbolic links, so a link back up the tree cannot loop. Only goes as deep
        /// as the pattern can match (see <see cref="MaxDepthBelowBase"/>).
        /// </summary>
        public static List<string> EnumerateMatches(string pattern, int limit = MaxExpandedFiles, CancellationToken cancel = default)
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
                int depth = MaxDepthBelowBase(pattern);
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = depth != 0,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    MatchCasing = MatchCasing.CaseInsensitive,
                };
                if (depth > 0)
                    options.MaxRecursionDepth = depth;

                foreach (var file in Directory.EnumerateFiles(baseDir, "*.exe", options))
                {
                    if (cancel.IsCancellationRequested)
                        break;
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

        private static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

        /// <summary>
        /// Whether someone other than SYSTEM, Administrators or TrustedInstaller can add or replace
        /// programs in the pattern's folder: it is owned by someone else, or grants someone else
        /// the right to create, write or delete in it. A folder that does not exist yet is judged
        /// by the nearest one above it, since whoever can create it can fill it. An Allow rule on
        /// such a folder lets through anything a standard user (or malware running as one) copies
        /// there, under any name. False when the permissions cannot be read.
        /// </summary>
        public static bool StandardUsersCanWrite(string pattern)
        {
            try
            {
                var baseDir = BaseDirectory(Expand(pattern));
                if (baseDir is null)
                    return false;

                var dir = new DirectoryInfo(baseDir);
                while (dir is not null && !dir.Exists)
                    dir = dir.Parent;
                if (dir is null)
                    return false;

                var acl = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !IsAdminLike(owner))
                    return true;

                const FileSystemRights Dangerous = FileSystemRights.WriteData | FileSystemRights.AppendData
                    | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                    | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & Dangerous) == 0)
                        continue;
                    // Applies only to what is inside, not to this folder itself.
                    if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)
                        continue;
                    if (rule.IdentityReference is SecurityIdentifier sid
                        && (IsAdminLike(sid) || sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid)))
                        continue;
                    return true;
                }
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAdminLike(SecurityIdentifier sid)
            => sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || sid.Equals(TrustedInstallerSid);
    }
}
