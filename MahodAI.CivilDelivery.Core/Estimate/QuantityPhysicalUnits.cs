using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.CivilDelivery.Estimate.BoqRulesV2;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// b24 (Codex 12:04 A, 12:45): the physical unit a scan resolved for its host — a reviewed unit decision included —
    /// travels with every host record, so later geometry (plan chains, centroids, hatch rings, count points, insert
    /// transforms, drawn widths) never re-derives it from the raw INSUNITS label. A stamp is accepted only when it is
    /// self-consistent (a known authority, the standard factor of its unit, a decision digest exactly when a decision
    /// applies); the BoQ adapter also requires it to match its run's own unit evidence. The raw INSUNITS evidence stays
    /// raw, a measurement already in SI is never converted again, and an XREF record keeps its own units (never stamped).
    /// </summary>
    public static class QuantityPhysicalUnits
    {
        public const string MetresPerUnitKey = "cad_physical_metres_per_unit";
        public const string UnitCodeKey = "cad_physical_unit_code";
        public const string AuthorityKey = "cad_physical_unit_authority";
        public const string DigestKey = "cad_physical_unit_digest";
        public const string RawUnitsKey = "cad_entity_database_insunits";

        /// <summary>Authorities under which a scan measured SI; anything else is unresolved.</summary>
        public static readonly IReadOnlyList<string> SiAuthorities = new[]
        {
            "explicit-insunits", "approved-unitless-host-declaration", "approved-recorded-unit", "approved-different-unit",
        };

        /// <summary>The stamp of one host record, in the metadata reader's keys (it adds the "cad_" prefix).</summary>
        public static void Stamp(IDictionary<string, string> values, PhysicalDrawingUnitPolicy.Resolution units)
        {
            values["physical_unit_authority"] = units.Authority;
            if (units.IsSupported)
            {
                values["physical_metres_per_unit"] = units.LinearToMetres.ToString("R", CultureInfo.InvariantCulture);
                if (units.EffectiveUnitCode is { } code)
                    values["physical_unit_code"] = code.ToString(CultureInfo.InvariantCulture);
            }
            if (units.DeclarationDigest != null) values["physical_unit_digest"] = units.DeclarationDigest;
        }

        /// <summary>A record measured from a CAD entity (any "cad_" evidence): its geometry depends on the drawing unit, so
        /// it must carry unit evidence — missing keys never exempt it (Codex 13:21). Only a record without any CAD
        /// evidence is truly non-CAD.</summary>
        public static bool IsUnitDependent(IReadOnlyDictionary<string, string> p) =>
            p.Keys.Any(k => k.StartsWith("cad_", System.StringComparison.Ordinal));

        public static bool IsStamped(IReadOnlyDictionary<string, string> p) =>
            p.ContainsKey(MetresPerUnitKey) || p.ContainsKey(AuthorityKey) || p.ContainsKey(UnitCodeKey) || p.ContainsKey(DigestKey);

        /// <summary>A record stamp read back: its factor when the stamp is self-consistent, otherwise null.</summary>
        public static double? StampedMetresPerUnit(IReadOnlyDictionary<string, string> p) =>
            ReadStamp(p) is { } stamp && Consistent(stamp.Authority, stamp.UnitCode, stamp.Factor, stamp.Digest) ? stamp.Factor : null;

        /// <summary>
        /// Metres per raw unit for one record outside a BoQ run (draft widths, family widths): a self-consistent stamp;
        /// no factor for a stamp that is unresolved or inconsistent; the raw INSUNITS factor only for a record without any
        /// stamp (a scan written before b24 — b23 could not record a different unit).
        /// </summary>
        public static double? MetresPerUnit(IReadOnlyDictionary<string, string> p, bool declaredMetres) =>
            IsStamped(p) ? StampedMetresPerUnit(p)
                : BoqNeutralRecordAdapter.ScanMetresPerUnit(p.TryGetValue(RawUnitsKey, out var raw) ? raw : null, declaredMetres);

        internal sealed record StampValues(string? Authority, int? UnitCode, double? Factor, string? Digest);

        internal static StampValues? ReadStamp(IReadOnlyDictionary<string, string> p)
        {
            if (!IsStamped(p)) return null;
            p.TryGetValue(AuthorityKey, out var authority);
            int? code = p.TryGetValue(UnitCodeKey, out var c) &&
                        int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCode) ? parsedCode : null;
            double? factor = p.TryGetValue(MetresPerUnitKey, out var f) &&
                             double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFactor) ? parsedFactor : null;
            p.TryGetValue(DigestKey, out var digest);
            return new StampValues(authority, code, factor, digest);
        }

        /// <summary>A known SI authority, the standard factor of its physical unit, and a decision digest exactly when a
        /// decision applies (an explicit INSUNITS has none).</summary>
        internal static bool Consistent(string? authority, int? unitCode, double? factor, string? digest) =>
            authority != null && SiAuthorities.Contains(authority) && unitCode is { } code && factor is { } k &&
            double.IsFinite(k) && PhysicalDrawingUnitPolicy.StandardMetresPerUnit(code) == k &&
            (authority == "explicit-insunits" ? digest == null : digest != null && Regex.IsMatch(digest, "^[a-f0-9]{64}$"));

        /// <summary>One factor for a scan's host records that carry unit evidence; null when none, or when they disagree —
        /// that needs a rescan or a review, never a guess.</summary>
        public static double? ScanMetresPerUnit(IEnumerable<IReadOnlyDictionary<string, string>> hostParameters, bool declaredMetres)
        {
            var factors = hostParameters
                .Where(p => IsStamped(p) || p.ContainsKey(RawUnitsKey))
                .Select(p => MetresPerUnit(p, declaredMetres))
                .Distinct()
                .ToList();
            return factors.Count == 1 ? factors[0] : null;
        }
    }

    public enum ScanUnitEvidenceState { Unknown, Legacy, Bound }

    /// <summary>The unit resolution a scan run measured with (persisted with the run as estimate_scan.json PhysicalUnits).</summary>
    public sealed record ScanPhysicalUnits(bool IsSupported, int RawUnitCode, int? EffectiveUnitCode, double LinearToMetres,
        string? Authority, string? DeclarationDigest);

    /// <summary>
    /// b24 (Codex 12:45): the unit authority of one scan, identified positively from its verified run evidence — Bound
    /// (written under the scan-physical-units contract), Legacy (a scan proven to predate it) or Unknown (unread, unlisted,
    /// mismatched or unrecognised evidence, which is never treated as legacy). Only a consistent Bound or a Legacy scan
    /// gives plan geometry.
    /// </summary>
    public sealed record ScanUnitEvidence(ScanUnitEvidenceState State, ScanPhysicalUnits? Units, string Detail)
    {
        public const string Contract = "scan-physical-units/1";
        /// <summary>The unit configuration hash of a profile without any declaration or review (the serialized empty list).</summary>
        public static readonly string EmptyUnitConfigurationHash = ArtifactHash.Sha256OfText("[]");

        public static ScanUnitEvidence Unknown(string detail) => new(ScanUnitEvidenceState.Unknown, null, detail);
        public static ScanUnitEvidence Legacy(string detail) => new(ScanUnitEvidenceState.Legacy, null, detail);
        public static ScanUnitEvidence Bound(ScanPhysicalUnits units) => new(ScanUnitEvidenceState.Bound, units, Contract);

        /// <summary>
        /// A run's evidence as read from its hash-verified estimate_scan.json header: the contract marker identifies a
        /// Bound scan; a verified header with physical units but neither the marker nor an authority (both always written
        /// since the contract) is the pre-contract writer — Legacy; anything else is Unknown, never legacy.
        /// </summary>
        public static ScanUnitEvidence FromHeader(bool contractPresent, string? contract, ScanPhysicalUnits? units,
            bool authorityPresent, bool authorityIsText) =>
            contractPresent
                ? contract == Contract && units != null && authorityIsText ? Bound(units)
                    : Unknown(contract == Contract ? "the contract marker without valid physical units" : "unknown or malformed unit contract")
            // A field that is present but malformed is not a field the old writer never had (Codex 13:21).
            : units != null && !authorityPresent && LegacyCoherent(units)
                ? Legacy("written before the unit contract (hash-verified run, no contract marker, no authority, coherent units)")
            : Unknown(units == null ? "no physical units in the scan header" : "pre-contract physical units that are not coherent");

        /// <summary>The historical contract of a supported pre-b24 scan: an explicit INSUNITS at its standard factor, or a
        /// unitless host declared metres (factor 1, with its declaration digest).</summary>
        public static bool LegacyCoherent(ScanPhysicalUnits u) =>
            u.IsSupported && u.EffectiveUnitCode is { } effective &&
            PhysicalDrawingUnitPolicy.StandardMetresPerUnit(effective) == u.LinearToMetres &&
            (effective == u.RawUnitCode && u.RawUnitCode != PhysicalDrawingUnitPolicy.Unitless && u.DeclarationDigest == null ||
             u.RawUnitCode == PhysicalDrawingUnitPolicy.Unitless && effective == PhysicalDrawingUnitPolicy.Metres &&
             u.DeclarationDigest != null && Regex.IsMatch(u.DeclarationDigest, "^[a-f0-9]{64}$"));

        /// <summary>An in-memory scan: only its contract marker makes it Bound. Without one there is no verified reader
        /// behind it, so it is Unknown — the recovery is a rescan, never an assumed legacy (Codex 13:21).</summary>
        public static ScanUnitEvidence OfScan(string? contract, PhysicalDrawingUnitPolicy.Resolution? units) =>
            contract == Contract ? FromResolution(units)
            : Unknown(contract == null ? "a scan without the unit contract — rescan" : "unknown unit contract '" + contract + "'");

        public static ScanUnitEvidence FromResolution(PhysicalDrawingUnitPolicy.Resolution? units) => units == null
            ? Unknown("the scan has no physical-unit resolution")
            : Bound(new ScanPhysicalUnits(units.IsSupported, units.RawUnitCode, units.EffectiveUnitCode, units.LinearToMetres,
                units.Authority, units.DeclarationDigest));

        /// <summary>Why this scan gives no plan geometry (Hebrew); null when its unit evidence holds.</summary>
        public string? Problem => State switch
        {
            ScanUnitEvidenceState.Legacy => null,
            ScanUnitEvidenceState.Unknown => "ראיית היחידות של הסריקה לא אומתה (" + Detail + ") — נדרשת סריקה חדשה",
            _ when Units is not { IsSupported: true } u => "יחידות הסריקה לא הוכרעו — אין גאומטריה במטרים",
            _ when !QuantityPhysicalUnits.Consistent(Units.Authority, Units.EffectiveUnitCode, Units.LinearToMetres, Units.DeclarationDigest) ||
                   (Units.Authority == "explicit-insunits" && Units.EffectiveUnitCode != Units.RawUnitCode) =>
                "ראיית היחידות של הסריקה אינה עקבית (סמכות, יחידה, מקדם או הכרעה) — נדרשת סריקה חדשה",
            _ => null,
        };

        /// <summary>The factor of one host record in this scan: a Bound scan accepts only a stamp equal to its run's
        /// evidence; a Legacy scan only an unstamped record (raw INSUNITS); an Unknown scan nothing.</summary>
        public double? RecordMetresPerUnit(IReadOnlyDictionary<string, string> p, bool declaredMetres)
        {
            if (Problem != null) return null;
            if (State == ScanUnitEvidenceState.Legacy)
                return QuantityPhysicalUnits.IsStamped(p) ? null
                    : BoqNeutralRecordAdapter.ScanMetresPerUnit(p.TryGetValue(QuantityPhysicalUnits.RawUnitsKey, out var raw) ? raw : null, declaredMetres);
            var stamp = QuantityPhysicalUnits.ReadStamp(p);
            var u = Units!;
            return stamp != null && stamp.Authority == u.Authority && stamp.UnitCode == u.EffectiveUnitCode &&
                   stamp.Factor == u.LinearToMetres && stamp.Digest == u.DeclarationDigest
                ? u.LinearToMetres : null;
        }

        /// <summary>Why the host records of this scan cannot be measured as it claims (Hebrew); null when they hold.
        /// Bound: every record with unit evidence carries exactly its run's stamp (a stripped, mixed, foreign-digest or
        /// non-standard stamp refuses the scan). Legacy: no record may carry a stamp.</summary>
        public string? RecordsProblem(IEnumerable<IReadOnlyDictionary<string, string>> host, bool declaredMetres) =>
            Problem ?? (State == ScanUnitEvidenceState.Legacy
                ? host.Any(QuantityPhysicalUnits.IsStamped) ? "סריקה ישנה עם ראיית יחידות חדשה — נדרשת סריקה חדשה" : null
                : host.Where(QuantityPhysicalUnits.IsUnitDependent)
                      .Any(p => RecordMetresPerUnit(p, declaredMetres) == null)
                    ? "רשומות הסריקה אינן תואמות לראיית היחידות של הסריקה — נדרשת סריקה חדשה" : null);

        /// <summary>The scan-wide factor (failed-hatch diagnostics have no record): the run's own for a Bound scan, the
        /// agreeing raw factor of its host records for a Legacy one, none otherwise.</summary>
        public double? ScanMetresPerUnit(IEnumerable<IReadOnlyDictionary<string, string>> host, bool declaredMetres) =>
            Problem != null ? null
            : State == ScanUnitEvidenceState.Bound ? Units!.LinearToMetres
            : QuantityPhysicalUnits.ScanMetresPerUnit(host.Where(p => !QuantityPhysicalUnits.IsStamped(p)), declaredMetres);
    }
}
