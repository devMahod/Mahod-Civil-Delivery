using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared;

/// <summary>
/// Conservative scope of a FAILED entity, not a replacement geometry. Only a
/// native, finite WCS envelope can prove that a failed entity cannot meet a cut.
/// Unknown bounds, unknown roles and traversal/source failures remain global.
/// </summary>
public static class SectionProjectionFailureScope
{
    public const double BoundsPaddingM = 0.01;
    /// <summary>
    /// A cut and how many of its width spans still lack a confident name.
    /// <see cref="int.MaxValue"/> (the default) means "unknown": every failure blocks.
    /// </summary>
    public sealed record Cut(string RecordId, double[]? Endpoints, int UnresolvedSpanCount = int.MaxValue)
    {
        /// <summary>b7: ids of the exact topology findings this cut proved locally. Never
        /// a role/category waiver; every other finding keeps its scope.</summary>
        public IReadOnlyCollection<string>? LocallyProvenFindingIds { get; init; }
    }

    public static bool HasUsableBounds(double[]? b) => b is { Length: 4 } &&
        b.All(double.IsFinite) && b[0] <= b[2] && b[1] <= b[3] &&
        (b[0] < b[2] || b[1] < b[3]);

    private static bool CanScope(DeliveryFinding f) =>
        f.Code == SectionFindingCodes.ProjectionGeometryUnsupported &&
        f.ProjectionRole is "plan-region" or "plan-mark" or "projected-utility" &&
        HasUsableBounds(f.SourceBoundsWcs);

    public static bool AffectsCut(DeliveryFinding finding, double[]? endpoints) =>
        !CanScope(finding) || MayIntersect(finding.SourceBoundsWcs, endpoints);

    /// <summary>
    /// A hatch area from the HA plan is name evidence only: it may suggest a label
    /// for an unresolved span or contradict a chosen one. On a cut whose every width
    /// span already carries a confident name it can do neither, so an unreadable
    /// area there is a visible warning, never a block (live 06/09: sidewalk hatch
    /// 8BB299/262391 blocked STA-12145 although its eight strips were all named).
    /// Marks, utilities, unknown roles and source/traversal failures still block.
    /// </summary>
    public static bool IsNamedRegionOnly(DeliveryFinding finding, int unresolvedSpanCount) =>
        finding.Code == SectionFindingCodes.ProjectionGeometryUnsupported &&
        finding.ProjectionRole == "plan-region" &&
        HasUsableBounds(finding.SourceBoundsWcs) &&
        unresolvedSpanCount == 0;

    public static bool BlocksCreation(DeliveryFinding finding, double[]? endpoints, int unresolvedSpanCount) =>
        finding.Severity == FindingSeverity.Error &&
        AffectsCut(finding, endpoints) &&
        !IsNamedRegionOnly(finding, unresolvedSpanCount);

    public static bool IsLocallyProven(DeliveryFinding finding, Cut cut) =>
        cut.LocallyProvenFindingIds?.Contains(finding.FindingId) == true;

    /// <summary>b7: the exact finding of one source/cut after a decided local cut proof.</summary>
    public static DeliveryFinding ForLocalCut(DeliveryFinding finding, string recordId, string proofEvidence)
    {
        var copy = Copy(finding, FindingSeverity.Warning, SectionFindingCodes.ProjectionRegionLocalCutProven,
            "כיסוי מקומי של אזור HA הוכח לאורך קו החתך; שטח המקור עדיין אינו תקין — אזהרה, אינו חוסם יצירה",
            new() { recordId });
        copy.EvidenceRefs.Add($"{SectionHatchLocalCut.Method}:{proofEvidence}");
        return copy;
    }

    public static DeliveryFinding ForNamedCut(DeliveryFinding finding, string recordId) =>
        Copy(finding, FindingSeverity.Warning, SectionFindingCodes.ProjectionRegionUnreadableNamed,
            "אזור HA לא קריא בחתך שכל רצועותיו כבר נקובות בשם — נרשם כאזהרה, אינו חוסם יצירה",
            new() { recordId });

    public static bool BlocksUtilityScan(DeliveryFinding finding, double[]? endpoints) =>
        finding.Severity == FindingSeverity.Error &&
        // A failed area/road marking is not evidence of missing utility geometry.
        !(finding.Code == SectionFindingCodes.ProjectionGeometryUnsupported &&
          finding.ProjectionRole is "plan-region" or "plan-mark") &&
        AffectsCut(finding, endpoints);

    public static bool MayIntersect(double[]? bounds, double[]? endpoints)
    {
        if (!HasUsableBounds(bounds) || endpoints is not { Length: 4 } ||
            !endpoints.All(double.IsFinite)) return true;
        var x = endpoints[0]; var y = endpoints[1];
        var dx = endpoints[2] - x; var dy = endpoints[3] - y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) ||
            (dx == 0 && dy == 0)) return true;
        var lo = 0d; var hi = 1d;
        return Slab(x, dx, bounds![0] - BoundsPaddingM, bounds[2] + BoundsPaddingM, ref lo, ref hi) &&
               Slab(y, dy, bounds[1] - BoundsPaddingM, bounds[3] + BoundsPaddingM, ref lo, ref hi);
    }

    private static bool Slab(double origin, double direction, double min, double max,
        ref double lo, ref double hi)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return true;
        if (direction == 0) return origin >= min && origin <= max;
        var a = (min - origin) / direction; var b = (max - origin) / direction;
        if (!double.IsFinite(a) || !double.IsFinite(b)) return true;
        if (a > b) (a, b) = (b, a);
        lo = Math.Max(lo, a); hi = Math.Min(hi, b);
        return lo <= hi;
    }

    public static DeliveryFinding ForCut(DeliveryFinding finding, string recordId) =>
        Copy(finding, finding.Severity, finding.Code, finding.Title, new() { recordId });

    public static DeliveryFinding ForPlan(DeliveryFinding finding, IReadOnlyList<Cut> cuts)
    {
        if (!CanScope(finding) || cuts.Count == 0) return finding;
        var affected = cuts.Where(c => AffectsCut(finding, c.Endpoints)).ToList();
        var ids = affected.Where(c => !IsNamedRegionOnly(finding, c.UnresolvedSpanCount) && !IsLocallyProven(finding, c))
            .Select(c => c.RecordId).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > 0) return Copy(finding, finding.Severity, finding.Code, finding.Title, ids);
        if (affected.Any(c => IsLocallyProven(finding, c)))
            return Copy(finding, FindingSeverity.Warning, SectionFindingCodes.ProjectionRegionLocalCutProven,
                "אזור HA לא תקין — בכל חתך שהוא פוגש כיסוי מקומי הוכח או שהרצועות כבר נקובות; נרשם כאזהרה ואינו חוסם",
                affected.Select(c => c.RecordId).Distinct(StringComparer.Ordinal).ToList());
        if (affected.Count > 0)
            return Copy(finding, FindingSeverity.Warning, SectionFindingCodes.ProjectionRegionUnreadableNamed,
                "אזור HA לא קריא — החתכים שהוא פוגש כבר נקובים בשם במלואם; נרשם כאזהרה ואינו חוסם",
                affected.Select(c => c.RecordId).Distinct(StringComparer.Ordinal).ToList());
        return Copy(finding, FindingSeverity.Warning, SectionFindingCodes.ProjectionGeometryOutsideCuts,
            "גאומטריה לא קריאה מחוץ לכל קווי החתך — אינה משפיעה על החתכים בתכנון זה", ids);
    }

    private static DeliveryFinding Copy(DeliveryFinding f, FindingSeverity severity,
        string code, string title, List<string> ids) => new()
    {
        FindingId = f.FindingId, Code = code, Domain = f.Domain, Severity = severity,
        Title = title, Message = f.Message, ProjectProfileId = f.ProjectProfileId,
        RunId = f.RunId, SourceRefs = new(f.SourceRefs), AffectedRecordIds = ids,
        ProjectionRole = f.ProjectionRole, SourceBoundsWcs = f.SourceBoundsWcs?.ToArray(),
        EvidenceRefs = new(f.EvidenceRefs), RecommendedAction = f.RecommendedAction,
        CreatedAtUtc = f.CreatedAtUtc, ResolvedAtUtc = f.ResolvedAtUtc,
        Resolution = f.Resolution, ResolvedBy = f.ResolvedBy,
    };
}
