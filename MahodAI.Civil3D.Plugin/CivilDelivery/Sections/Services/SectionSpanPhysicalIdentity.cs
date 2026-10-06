using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>
/// Physical authority for one current measured span, not an output/profile fingerprint.
/// The crossing point, station and tangent bind the relevant local alignment geometry;
/// this is deliberately not a hash of the entire alignment or host drawing.
/// </summary>
public static class SectionSpanPhysicalIdentity
{
    public static string? TryCreate(SectionPlanRecord record, double from, double to)
    {
        var cl = record.Cl;
        var crossing = record.SelectedCrossing;
        if (!Sha(cl.SourceDrawingHash) || string.IsNullOrWhiteSpace(cl.SourceHandle) ||
            string.IsNullOrWhiteSpace(record.SelectedAlignment) || crossing == null ||
            !string.Equals(record.SelectedAlignment, crossing.AlignmentName, StringComparison.OrdinalIgnoreCase) ||
            record.Station is not { } station || !double.IsFinite(station) ||
            !double.IsFinite(crossing.Station) || station != crossing.Station ||
            !Finite(cl.SourceEndpoints, 4) || !Finite(cl.WcsEndpoints, 4) ||
            !Finite(crossing.Point, 2) || !double.IsFinite(crossing.TangentDeg) ||
            !double.IsFinite(crossing.GapDistance) || crossing.GapDistance < 0 ||
            (cl.XrefTransform != null && !Finite(cl.XrefTransform, 12) && !Finite(cl.XrefTransform, 16)) ||
            (!string.IsNullOrWhiteSpace(cl.SourceXref) && cl.XrefTransform == null) ||
            !Sha(record.PreManualSpanEvidenceDigest) ||
            !double.IsFinite(from) || !double.IsFinite(to) || to <= from)
            return null;

        // JSON keeps typed exact doubles with invariant round-trip formatting and
        // unambiguous string boundaries. No tolerances/quantization are introduced.
        var hostPath = HostSourceDrawingPath(record);
        var canonical = JsonSerializer.Serialize(new
        {
            Contract = "section-span-physical/pre-manual-v1",
            SourceHash = hostPath == null ? cl.SourceDrawingHash.ToLowerInvariant() : "host-physical-v1",
            HostDrawingPath = hostPath,
            Handle = cl.SourceHandle.ToUpperInvariant(),
            cl.SourceDrawingPath, cl.SourceXref, cl.SourceEntityType,
            cl.SourceEndpoints, cl.WcsEndpoints, cl.XrefTransform,
            Alignment = record.SelectedAlignment.ToUpperInvariant(),
            Station = station, crossing.Point, crossing.TangentDeg, crossing.GapDistance,
            SourcePresentation = record.PreManualSpanEvidenceDigest!.ToLowerInvariant(),
            From = from, To = to,
        });
        return ArtifactHash.Sha256OfText(canonical);
    }

    /// <summary>No-XREF is not host proof: configured side DWGs also have no XREF chain.</summary>
    public static string? HostSourceDrawingPath(SectionPlanRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Cl.SourceXref)) return null;
        var host = NormalizedPath(record.SpanDecisionHostDrawingPath);
        return host != null && host == NormalizedPath(record.Cl.SourceDrawingPath) ? host : null;
    }

    /// <summary>Scopes history only. Applying still requires the exact current physical key.</summary>
    public static bool SameSourceScope(SectionPlanRecord record,
        ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision decision)
    {
        if (decision.HostSourceDrawingPath != null)
            return HostSourceDrawingPath(record) is { } current &&
                current == NormalizedPath(decision.HostSourceDrawingPath);
        return string.Equals(decision.SourceDrawingHash, record.Cl.SourceDrawingHash, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.GetFullPath(path).ToUpperInvariant(); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    public static bool Matches(SectionPlanRecord record,
        ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision decision) =>
        Sha(decision.PhysicalDecisionKey) && decision.FromOffsetM is { } from &&
        decision.ToOffsetM is { } to &&
        string.Equals(decision.PhysicalDecisionKey, TryCreate(record, from, to), StringComparison.Ordinal);

    public static bool Matches(SectionPlanRecord record,
        ProjectProfile.SectionsProfile.DecisionsProfile.SpanLabelDecision decision, double from, double to) =>
        Sha(decision.PhysicalDecisionKey) &&
        string.Equals(decision.PhysicalDecisionKey, TryCreate(record, from, to), StringComparison.Ordinal);

    private static bool Sha(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Finite(double[]? values, int length) =>
        values is not null && values.Length == length && values.All(double.IsFinite);
}
