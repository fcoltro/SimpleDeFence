using SimpleDeFence.DatabaseClasses;
using SimpleDeFence.Localization;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleDeFence
{
    /// Which section of the Rules screen a row belongs to.
    public enum RuleGroup
    {
        Applications,
        SpecialRecommended,
        SpecialOptional,
    }

    /// <summary>One row of the Rules list, already grouped and ready to render.</summary>
    public sealed class RuleRow
    {
        /// <summary>Set for application rules; null for special rows.</summary>
        public FirewallExceptionV3? Exception { get; init; }

        /// <summary>Set for special rows (the profile's string id); null for application rules.</summary>
        public string? SpecialId { get; init; }

        public RuleGroup Group { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public string Policy { get; init; } = string.Empty;
        public bool IsBlocked { get; init; }

        /// <summary>Special rows only: whether the profile currently enables this exception.</summary>
        public bool Enabled { get; init; }
    }

    /// <summary>
    /// Pure transformations that turn a profile (+ the app database, for special-exception
    /// definitions) into the grouped, filtered Rules list. Shared so the mapping is unit-tested
    /// without a UI, mirroring ExceptionDescriptor/ConnectionActivity.
    /// </summary>
    public static class RuleListBuilder
    {
        public static IReadOnlyList<RuleRow> Build(ServerProfileConfiguration profile, AppDatabase db)
        {
            var rows = new List<RuleRow>();

            foreach (var ex in profile.AppExceptions)
            {
                var d = ExceptionDescriptor.Describe(ex);
                rows.Add(new RuleRow
                {
                    Exception = ex,
                    Group = RuleGroup.Applications,
                    Name = d.Name,
                    Kind = d.Kind,
                    Detail = d.Detail,
                    Policy = d.Policy,
                    IsBlocked = d.IsBlocked,
                    Enabled = true,
                });
            }

            foreach (var app in db.KnownApplications)
            {
                // Mirrors the WinForms settings UI: special, non-hidden definitions only, split
                // by the recommended flag.
                if (!app.HasFlag("TWUI:Special") || app.HasFlag("TWUI:Hidden"))
                    continue;

                rows.Add(new RuleRow
                {
                    SpecialId = app.Name,
                    Group = app.HasFlag("TWUI:Recommended") ? RuleGroup.SpecialRecommended : RuleGroup.SpecialOptional,
                    Name = app.Name.Replace('_', ' '),
                    Kind = Loc.T(LocKeys.Rules.SpecialKind),
                    Detail = string.Empty,
                    Policy = Loc.T(LocKeys.Rules.SpecialPolicy),
                    IsBlocked = false,
                    Enabled = profile.HasSpecialException(app.Name),
                });
            }

            return rows
                .OrderBy(r => r.Group)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<RuleRow> Filter(IReadOnlyList<RuleRow> rows, string term)
        {
            if (string.IsNullOrWhiteSpace(term))
                return rows;

            return rows.Where(r =>
                r.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0
                || r.Detail.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
        }
    }

    /// <summary>Pure profile mutations behind the Rules screen's commits.</summary>
    public static class RuleEdit
    {
        /// <summary>
        /// Returns a copy of <paramref name="original"/> with the policy swapped, keeping the Id so
        /// ServerProfileConfiguration.AddExceptions replaces the old entry rather than adding a twin.
        /// RuleListPolicy is refused: the preset editor cannot represent custom rules, and silently
        /// discarding them would be a lie.
        /// </summary>
        public static FirewallExceptionV3 ApplyPreset(FirewallExceptionV3 original, ExceptionPolicy newPolicy)
        {
            if (original.Policy.PolicyType == PolicyType.RuleList)
                throw new InvalidOperationException("Preset editing cannot represent a custom rule list.");

            return new FirewallExceptionV3(original.Subject, newPolicy)
            {
                Id = original.Id,
                CreationDate = original.CreationDate,
                Timer = original.Timer,
                ChildProcessesInherit = original.ChildProcessesInherit,
            };
        }

        public static void RemoveExceptions(ServerProfileConfiguration profile, IReadOnlyCollection<Guid> ids)
        {
            profile.AppExceptions.RemoveAll(ex => ids.Contains(ex.Id));
        }

        public static void SetSpecialEnabled(ServerProfileConfiguration profile, string id, bool enabled)
        {
            var present = profile.HasSpecialException(id);
            if (enabled && !present)
                profile.SpecialExceptions.Add(id);
            else if (!enabled && present)
                profile.SpecialExceptions.RemoveAll(s => string.Equals(s, id, StringComparison.OrdinalIgnoreCase));
        }
    }
}