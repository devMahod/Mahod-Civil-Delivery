using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>
/// Project completeness is not a failed measurement of every other object. Only
/// known, independent direct geometry can be separated from known coverage gaps.
/// Unknown codes, methods, identities and source/security failures fail closed.
/// This policy never resolves a finding or grants full-estimate export permission.
/// </summary>
public static class EstimateFindingImpactPolicy
{
    public static Prepared Prepare(DeliveryFinding finding) => new(finding);
    public static bool BlocksLine(DeliveryFinding finding, EstimateLine line) => Prepare(finding).BlocksLine(line);
    public static bool BlocksRecord(DeliveryFinding finding, NeutralQuantityRecord record) => Prepare(finding).BlocksRecord(record);
    public static Index CreateIndex(IEnumerable<DeliveryFinding> findings) => new(findings);

    /// <summary>Index emitted-record findings once; native scans contain tens of thousands of rows.</summary>
    public sealed class Index
    {
        private readonly List<Prepared> _global = new();
        private readonly Dictionary<string, List<Prepared>> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Prepared>> _paths = new(StringComparer.OrdinalIgnoreCase);
        internal Index(IEnumerable<DeliveryFinding> findings)
        {
            foreach (var finding in findings.Where(EstimatePreflightPolicy.IsBlocking))
            {
                var prepared = Prepare(finding);
                if (finding.AffectedRecordIds.Count == 0 || prepared.HardGlobal ||
                    prepared._ids.Count != finding.AffectedRecordIds.Count ||
                    finding.SourceRefs.Count > 0 && !prepared._sourcesComplete)
                    _global.Add(prepared);
                else
                {
                    foreach (var id in prepared._ids) Add(_ids, id, prepared);
                    foreach (var path in prepared._sources.Keys) Add(_paths, path, prepared);
                }
            }
        }
        private static void Add(Dictionary<string, List<Prepared>> index, string key, Prepared value)
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            list.Add(value);
        }
        private IEnumerable<Prepared> Candidates(string id, string? path) => _global
            .Concat(_ids.TryGetValue(id, out var byId) ? byId : Enumerable.Empty<Prepared>())
            .Concat(CanonicalPath(path) is { } canonical && _paths.TryGetValue(canonical, out var byPath)
                ? byPath : Enumerable.Empty<Prepared>()).Distinct();
        public IReadOnlyList<DeliveryFinding> BlockingFindings(EstimateLine line) => Candidates(line.RecordId, line.SourceDrawingPath)
            .Where(impact => impact.BlocksLine(line)).Select(impact => impact._finding).ToArray();
        public bool BlocksLine(EstimateLine line) => Candidates(line.RecordId, line.SourceDrawingPath).Any(impact => impact.BlocksLine(line));
        public bool BlocksRecord(NeutralQuantityRecord record) => Candidates(record.RecordId, record.Source.DrawingPath ?? record.Source.Drawing)
            .Any(impact => impact.BlocksRecord(record));
        public IReadOnlyList<DeliveryFinding> BlockingFindings(NeutralQuantityRecord record) => Candidates(record.RecordId, record.Source.DrawingPath ?? record.Source.Drawing)
            .Where(impact => impact.BlocksRecord(record)).Select(impact => impact._finding).ToArray();
    }

    public sealed class Prepared
    {
        internal readonly DeliveryFinding _finding;
        internal readonly HashSet<string> _ids;
        internal readonly Dictionary<string, ProvenanceRef[]> _sources;
        internal readonly bool _sourcesComplete;
        internal bool HardGlobal => _finding.Code.StartsWith("SEC-", StringComparison.Ordinal) ||
            _finding.Code == EstimateFindingCodes.SourceScopePolicyUnapproved || _finding.Code == EstimateFindingCodes.XrefPolicyUnapproved;

        internal Prepared(DeliveryFinding finding)
        {
            _finding = finding;
            _ids = finding.AffectedRecordIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            _sourcesComplete = finding.SourceRefs.Count > 0 && finding.SourceRefs.All(source =>
                CompleteIdentity(source) &&
                !string.IsNullOrWhiteSpace(source.EntityType) && !string.IsNullOrWhiteSpace(source.MeasurementMethod) &&
                (source.XrefTransform == null || source.XrefTransform.Length is 12 or 16 && source.XrefTransform.All(double.IsFinite)));
            _sources = finding.SourceRefs.Where(source => CanonicalPath(source.SourcePathOrUri) != null)
                .GroupBy(source => CanonicalPath(source.SourcePathOrUri)!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        public bool BlocksLine(EstimateLine line) => Blocks(line.RecordId, line.SourceDrawingPath,
            line.SourceDrawingHash, line.SourceHandle, line.SourceXref, line.SourceEntityType, line.MeasurementMethod,
            IsReadyNativeMaterial(line.SourceEntityType, line.MeasurementMethod, line.SourceXref,
                line.SourceLayer, line.SourceCivilIdentity, line.SourceStationFrom, line.SourceStationTo,
                line.Unit, line.RawQuantity, line.SourceMeasurementStatus) && line.SourceMeasurementKind == "volume" &&
            !line.Findings.Any(EstimatePreflightPolicy.IsBlocking) &&
            line.Status == DeliveryStatus.Ready && line.IncludedInTotals && line.Price is > 0 &&
            HasMappingIdentity(line.CatalogCode, line.ApprovedCatalogId, line.ApprovedCatalogHash,
                line.ApprovedCatalogItemFingerprint, line.MappingApprovedBy, line.MappingApprovedAtUtc));

        public bool BlocksRecord(NeutralQuantityRecord record) => Blocks(record.RecordId,
            record.Source.DrawingPath ?? record.Source.Drawing, record.Source.DrawingHash, record.Source.Handle,
            record.Source.Xref, record.Source.EntityType, record.Measurement.Method,
            IsReadyNativeMaterial(record.Source.EntityType, record.Measurement.Method, record.Source.Xref,
                record.Source.Layer, record.Source.CivilIdentity, record.Source.StationFrom, record.Source.StationTo,
                record.Measurement.Unit, record.Measurement.RawValue, record.Status) && record.Measurement.Kind == "volume" &&
            !record.Findings.Any(EstimatePreflightPolicy.IsBlocking) &&
            HasMappingIdentity(record.Classification.CandidateCatalogCode, record.Classification.ApprovedCatalogId,
                record.Classification.ApprovedCatalogHash, record.Classification.ApprovedCatalogItemFingerprint,
                record.Classification.MappingApprovedBy, record.Classification.MappingApprovedAtUtc));

        private bool Blocks(string recordId, string? path, string? hash, string? handle,
            string? xref, string? entityType, string? method, bool readyNativeMaterial)
        {
            if (!EstimatePreflightPolicy.IsBlocking(_finding)) return false;
            if (HardGlobal) return true;
            if (_ids.Contains(recordId)) return true;

            var identity = new ProvenanceRef
            {
                SourceKind = string.IsNullOrWhiteSpace(xref) ? "drawing" : "xref",
                SourcePathOrUri = path, DrawingChecksum = hash, SourceHandle = handle, XrefPath = xref,
            };
            var complete = CompleteIdentity(identity);
            var sameSource = complete && CanonicalPath(path) is { } canonical && _sources.TryGetValue(canonical, out var sources)
                ? sources : Array.Empty<ProvenanceRef>();
            // A conflicting SHA for one file is uncertainty, not another object.
            // The same underlying leaf through another XREF insertion is also kept
            // blocked conservatively; we never look up a leaf handle in the host.
            var sourceRelated = sameSource.Any(source =>
                !string.Equals(source.DrawingChecksum, hash, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source.SourceHandle, handle, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Leaf(source.SourceHandle), Leaf(handle), StringComparison.OrdinalIgnoreCase) ||
                IsParent(source.SourceHandle, handle) || IsParent(handle, source.SourceHandle));
            if (sourceRelated) return true;

            if (_finding.AffectedRecordIds.Count > 0)
            {
                // Exact emitted IDs remain scoped, but malformed corroborating
                // source identity or an ID/source disagreement never clears a row.
                return _ids.Count != _finding.AffectedRecordIds.Count ||
                    _finding.SourceRefs.Count > 0 && (!_sourcesComplete || !complete);
            }
            // A missing PROJECT earthworks decision is independent of an already
            // priced native material measurement. Only this exact global scope
            // finding gets the exception. Stale/coverage/read/unit/source errors
            // still independently block corridor rows and remain in the result.
            // The result keeps this finding, so full export is still forbidden.
            if (complete && readyNativeMaterial && _finding.Domain == "estimate" &&
                _finding.Code == EstimatePreflightPolicy.EarthworksNotAssessedCode &&
                _finding.Severity == FindingSeverity.ReviewRequired && _finding.SourceRefs.Count == 0)
                return false;
            if (!complete || _finding.Domain != "estimate" || !KnownDirectMeasurement(entityType, method, xref)) return true;
            if (_finding.SourceRefs.Count > 0 && !_sourcesComplete) return true;

            if (_finding.Code is EstimatePreflightPolicy.CorridorOutOfDateCode or
                EstimatePreflightPolicy.CorridorMaterialCoverageIncompleteCode or
                EstimatePreflightPolicy.EarthworksNotAssessedCode)
                return false; // Direct entity measurement does not use a corridor/earthworks calculation.

            if (_finding.Code is EstimateFindingCodes.MeasurementFailed or EstimateFindingCodes.UnsupportedEntityCoverage)
                return !_sourcesComplete;
            return true;
        }
    }

    private static bool IsReadyNativeMaterial(string? entityType, string? method, string? xref,
        string? layer, string? civilIdentity, double? from, double? to, string? unit, double raw, DeliveryStatus? status) =>
        status == DeliveryStatus.Ready && entityType == "CORRIDOR" &&
        method == "corridor-qto-avg-end-area" && string.IsNullOrWhiteSpace(xref) &&
        layer?.StartsWith("corridor:", StringComparison.Ordinal) == true && layer.Length > "corridor:".Length &&
        !string.IsNullOrWhiteSpace(civilIdentity) && from is { } start && double.IsFinite(start) &&
        to is { } end && double.IsFinite(end) && end > start &&
        Units.Parse(unit).Canonical == "m3" && double.IsFinite(raw) && raw > 0;

    private static bool HasMappingIdentity(string? code, string? catalogId, string? catalogHash,
        string? itemFingerprint, string? approvedBy, DateTime? approvedAtUtc) =>
        !string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(catalogId) &&
        CatalogIdentity.IsValidSha256(catalogHash) && CatalogIdentity.IsValidSha256(itemFingerprint) &&
        !string.IsNullOrWhiteSpace(approvedBy) && approvedAtUtc.HasValue;

    private static bool KnownDirectMeasurement(string? entityType, string? method, string? xref)
    {
        const string suffix = "+xref-transform";
        if (string.IsNullOrWhiteSpace(method) || string.IsNullOrWhiteSpace(entityType)) return false;
        if (!string.IsNullOrWhiteSpace(xref))
        {
            if (!method.EndsWith(suffix, StringComparison.Ordinal)) return false;
            method = method[..^suffix.Length];
        }
        else if (method.Contains('+')) return false;
        var type = entityType.ToUpperInvariant();
        return (type, method) switch
        {
            ("LINE", "line-length") or ("ARC", "arc-length") or ("CIRCLE", "circle-circumference") or
            ("SPLINE", "spline-length") or ("HATCH", "hatch-area") or ("HATCH", "hatch-linear-boundary-area") or
            ("HATCH", "hatch-line-arc-boundary-area") or ("REGION", "region-area") or
            ("POLYLINE", "polyline-length") or ("POLYLINE", "closed-polyline-perimeter") or
            ("POLYLINE", "closed-polyline-area") or ("POLYLINE2D", "polyline2d-length") or
            ("POLYLINE2D", "closed-polyline2d-perimeter") or ("POLYLINE2D", "closed-polyline2d-area") or
            ("POLYLINE3D", "polyline3d-length") or ("BLOCKREFERENCE", "block-count") => true,
            ("HATCH", "hatch-exact-retrace-linear-area") or ("HATCH", "hatch-exact-retrace-mixed-line-area") => string.IsNullOrWhiteSpace(xref),
            _ => false,
        };
    }

    private static string? CanonicalPath(string? path)
    {
        if (!FindingSourceLocationPolicy.IsDriveQualifiedDrawingPath(path)) return null;
        try { return Path.GetFullPath(path!); } catch { return null; }
    }
    private static bool CompleteIdentity(ProvenanceRef source)
    {
        // Impact evidence and UI-locator support are different contracts. A Civil
        // baseline/station diagnostic identifies its parent model object even when
        // the navigation UI cannot locate that subcontext. Never interpret that
        // diagnostic string as an AutoCAD handle or allow an unknown parent object.
        if (CanonicalPath(source.SourcePathOrUri) == null || !CatalogIdentity.IsValidSha256(source.DrawingChecksum)) return false;
        var handles = source.SourceHandle?.Split('/');
        if (handles == null || handles.Any(part => !ulong.TryParse(part, NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var value) || value == 0)) return false;
        if (source.SourceKind == "xref") return handles.Length >= 2 && !string.IsNullOrWhiteSpace(source.XrefPath) &&
            string.IsNullOrWhiteSpace(source.SourceSubentityPath);
        if (handles.Length != 1 || !string.IsNullOrWhiteSpace(source.XrefPath) || source.XrefTransform != null) return false;
        if (source.SourceKind == "drawing") return string.IsNullOrWhiteSpace(source.SourceSubentityPath);
        if (source.SourceKind != "civil-model" || string.IsNullOrWhiteSpace(source.EntityType)) return false;
        return source.MeasurementMethod == "civil-model-quantity" && string.IsNullOrWhiteSpace(source.SourceSubentityPath) ||
            source.MeasurementMethod?.StartsWith("civil-model-quantity:", StringComparison.Ordinal) == true &&
            source.MeasurementMethod.Length > "civil-model-quantity:".Length && !string.IsNullOrWhiteSpace(source.SourceSubentityPath);
    }
    private static string Leaf(string? handle) => (handle ?? "").Split('/').Last();
    private static bool IsParent(string? parent, string? child) => !string.IsNullOrWhiteSpace(parent) &&
        child?.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase) == true;
}
