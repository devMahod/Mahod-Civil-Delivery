using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Ordered adjustment chain (plan §8.8). Only CONFIRMED rules with real approval
    /// metadata are applied; UNCONFIRMED candidates (the 0.90) surface as findings
    /// and never touch the quantity.
    /// </summary>
    public static class AdjustmentEngine
    {
        public sealed record Outcome(double BoqValue, List<AdjustmentStep> Steps, List<DeliveryFinding> Findings);

        public static Outcome Apply(
            double rawValue,
            IEnumerable<ProjectProfile.EstimateProfile.AdjustmentRule> approved,
            IEnumerable<ProjectProfile.EstimateProfile.AdjustmentRule> candidates,
            string recordId,
            string? ruleKey = null,
            string? catalogCode = null)
        {
            var steps = new List<AdjustmentStep>();
            var findings = new List<DeliveryFinding>();
            double value = rawValue;
            var approvedRules = approved.ToList();

            foreach (var rule in approvedRules.OrderBy(r => r.Order))
            {
                var confirmed = string.Equals(rule.Status, "CONFIRMED", StringComparison.OrdinalIgnoreCase);
                var hasAuthority = !string.IsNullOrWhiteSpace(rule.ApprovedBy) && rule.ApprovedAtUtc != null;
                var conflicted = EstimateConfigurationPolicy.IsConflictedAdjustment(rule, approvedRules);

                if (!confirmed || !hasAuthority || rule.Factor is null ||
                    !double.IsFinite(rule.Factor.Value) || rule.Factor.Value <= 0 || conflicted)
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.AdjustmentUnverified,
                        Domain = "estimate",
                        Severity = FindingSeverity.Error,
                        Title = conflicted
                            ? $"Adjustment '{rule.RuleId}' conflicts with another approved rule — NOT applied"
                            : $"Adjustment '{rule.RuleId}' is unapproved, non-positive or invalid — NOT applied",
                        AffectedRecordIds = { recordId },
                    });
                    continue;
                }

                var scope = EvaluateScope(rule.Scope, recordId, ruleKey, catalogCode);
                if (scope == ScopeMatch.Unsupported)
                {
                    findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.AdjustmentUnverified,
                        Domain = "estimate",
                        Severity = FindingSeverity.Error,
                        Title = $"Adjustment '{rule.RuleId}' has no supported explicit scope — NOT applied",
                        Message = "Supported scopes are record:<record_id>, rule:<rule_key>, or catalog:<catalog_code>. Global/blank scopes are rejected.",
                        AffectedRecordIds = { recordId },
                    });
                    continue;
                }
                if (scope == ScopeMatch.NotMatched) continue;

                var output = value * rule.Factor.Value;
                steps.Add(new AdjustmentStep
                {
                    RuleId = rule.RuleId ?? "(unnamed)",
                    Factor = rule.Factor,
                    Input = value,
                    Output = output,
                    Reason = rule.Reason,
                    Source = rule.Source,
                    ApprovedBy = rule.ApprovedBy,
                    ApprovedAtUtc = rule.ApprovedAtUtc,
                    Order = rule.Order,
                });
                value = output;
            }

            foreach (var candidate in candidates)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.AdjustmentUnverified,
                    Domain = "estimate",
                    Severity = FindingSeverity.Warning,
                    Title = $"מקדם מועמד '{Bidi.Ltr(candidate.RuleId)}' (פקטור {candidate.Factor}) אינו מאושר — לא הוחל על אף כמות",
                    Message = candidate.Reason ?? string.Empty,
                    RecommendedAction = "Engineer approval via project profile moves it to approved_adjustments.",
                    AffectedRecordIds = { recordId },
                });
            }

            return new Outcome(value, steps, findings);
        }

        private enum ScopeMatch { Matched, NotMatched, Unsupported }

        public static bool HasSupportedExplicitScope(string? scope) =>
            EvaluateScope(scope, "__record__", "__rule__", "__catalog__") != ScopeMatch.Unsupported;

        private static ScopeMatch EvaluateScope(
            string? scope, string recordId, string? ruleKey, string? catalogCode)
        {
            if (string.IsNullOrWhiteSpace(scope)) return ScopeMatch.Unsupported;
            var separator = scope.IndexOf(':');
            if (separator <= 0 || separator == scope.Length - 1) return ScopeMatch.Unsupported;
            var kind = scope[..separator].Trim();
            var value = scope[(separator + 1)..].Trim();
            if (value.Length == 0) return ScopeMatch.Unsupported;

            if (string.Equals(kind, "record", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, recordId, StringComparison.Ordinal)
                    ? ScopeMatch.Matched : ScopeMatch.NotMatched;
            if (string.Equals(kind, "rule", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, ruleKey, StringComparison.OrdinalIgnoreCase)
                    ? ScopeMatch.Matched : ScopeMatch.NotMatched;
            if (string.Equals(kind, "catalog", StringComparison.OrdinalIgnoreCase))
                return string.Equals(value, catalogCode, StringComparison.OrdinalIgnoreCase)
                    ? ScopeMatch.Matched : ScopeMatch.NotMatched;
            return ScopeMatch.Unsupported;
        }
    }

    /// <summary>Unit hard gate (plan §8.5): no silent conversion between units, even same-dimension.</summary>
    public static class UnitValidator
    {
        public static DeliveryFinding? Validate(NeutralQuantityRecord record, CatalogItem catalogItem)
        {
            var recordUnit = Units.Parse(record.Measurement.Unit);
            var catalogUnit = catalogItem.Unit;

            if (recordUnit.Dimension == UnitDimension.Unknown)
            {
                return new DeliveryFinding
                {
                    Code = EstimateFindingCodes.UnitUnknown,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"יחידת המדידה '{record.Measurement.Unit}' אינה יחידה מוכרת",
                    AffectedRecordIds = { record.RecordId },
                };
            }

            if (!recordUnit.SameUnit(catalogUnit))
            {
                return new DeliveryFinding
                {
                    Code = EstimateFindingCodes.UnitMismatch,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"אי-התאמת יחידות: נמדד '{recordUnit.Canonical}' מול סעיף '{catalogUnit.Canonical}' " +
                            $"({catalogItem.Code})",
                    Message = "A dimensional conversion is allowed only through an explicit verified rule with parameters.",
                    AffectedRecordIds = { record.RecordId },
                };
            }

            return null;
        }
    }

    /// <summary>Duplicate/double-count risk detection (plan §8.6). Reports, never silently merges.</summary>
    public static class DuplicateRiskDetector
    {
        public static List<DeliveryFinding> Detect(IReadOnlyList<NeutralQuantityRecord> records)
        {
            var findings = new List<DeliveryFinding>();

            // 1. The same measured source emitted twice. A Civil object can legitimately
            //    produce several quantities: one corridor handle emits a volume per
            //    material code/run, and one alignment emits separate cut/fill runs. The
            //    old handle+kind key called all of those duplicates. Layer, method,
            //    Civil identity and station range are part of the measured source
            //    identity; RuleKey is deliberately NOT, so two rules classifying the
            //    same measurement are still caught as a double count.
            foreach (var group in records
                         .GroupBy(r => new
                         {
                             r.Source.DrawingHash,
                             r.Source.Handle,
                             r.Source.Xref,
                             r.Source.Layer,
                             r.Source.CivilIdentity,
                             r.Source.StationFrom,
                             r.Source.StationTo,
                             r.Measurement.Kind,
                             r.Measurement.Method,
                         })
                         .Where(g => g.Count() > 1))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.DuplicateSource,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"עצם המקור נספר {group.Count()} פעמים (אותו שרטוט+handle+סוג)",
                    AffectedRecordIds = group.Select(r => r.RecordId).ToList(),
                });
            }

            // 2. Host + XREF duplicate representation, or the same source entity from
            //    one external drawing inserted through two XREF paths. Handles are
            //    database-local, so XREF↔XREF identity additionally requires the same
            //    source hash and terminal source handle. Both cases also require equal
            //    rule/dimension, value and transformed WCS bounds.
            var hostXrefPairs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var xrefXrefPairs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            // Equal count=1 values are common: the saved 74,900-record working copy
            // still required 149 million pairs after value sorting. These findings
            // report the set of involved records, not a pair count. Query whether
            // each record has a qualifying counterpart instead of visiting every
            // edge of a dense duplicate group. The exact predicates remain below.
            foreach (var bucket in records.GroupBy(record => (
                         RuleKey: record.Classification.RuleKey,
                         Kind: record.Measurement.Kind?.ToUpperInvariant(),
                         Unit: Units.Parse(record.Measurement.Unit).Canonical)))
            {
                var points = bucket.Where(RepresentationPoint.IsUsable)
                    .OrderBy(record => record.Measurement.RawValue)
                    .Select((record, order) => new RepresentationPoint(record, order)).ToList();
                var kinds = points.GroupBy(point => point.Record.Measurement.Kind, StringComparer.Ordinal)
                    .Select(group => new
                    {
                        Kind = group.Key,
                        Host = new RepresentationIndex(group.Where(point => !point.External)),
                        External = new RepresentationIndex(group.Where(point => point.External)),
                    }).ToList();
                foreach (var point in points)
                {
                    foreach (var kind in kinds)
                        AddRepresentationMembership(point, kind.Kind,
                            point.External ? kind.Host : kind.External, hostXrefPairs,
                            differentHandle: false);
                }

                // An external↔external pair also requires the same source drawing
                // and terminal handle. Index that identity before any geometry query.
                foreach (var source in points.Where(point => point.External).GroupBy(point => (
                             Drawing: point.Record.Source.DrawingHash?.ToUpperInvariant(),
                             Handle: TerminalSourceHandle(point.Record.Source.Handle).ToUpperInvariant())))
                {
                    var members = source.ToList();
                    if (members.Select(point => point.Record.Source.Handle)
                        .Distinct(StringComparer.Ordinal).Take(2).Count() < 2) continue;
                    var sourceKinds = members.GroupBy(point => point.Record.Measurement.Kind, StringComparer.Ordinal)
                        .Select(group => (Kind: group.Key, Index: new RepresentationIndex(group))).ToList();
                    foreach (var point in members)
                        foreach (var kind in sourceKinds)
                            AddRepresentationMembership(point, kind.Kind, kind.Index,
                                xrefXrefPairs, differentHandle: true);
                }
            }
            foreach (var pair in hostXrefPairs)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.XrefDoubleCountRisk,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "אותה גיאומטריה מופיעה גם בשרטוט וגם דרך XREF",
                    Message = $"rule={pair.Key}; transformed bounds and quantity are equal",
                    AffectedRecordIds = pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                });
            }
            foreach (var pair in xrefXrefPairs)
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.XrefDoubleCountRisk,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "אותה ישות מקור מופיעה דרך יותר ממופע XREF אחד",
                    Message = $"rule={pair.Key}; source hash + terminal handle + transformed bounds and quantity are equal",
                    AffectedRecordIds = pair.Value.OrderBy(id => id, StringComparer.Ordinal).ToList(),
                });
            }

            // 3. Overlapping geometry classified under the same rule (area double-count).
            var withBox = records
                .Where(r => r.Measurement.GeometryEvidence is { Length: 4 } &&
                            r.Classification.RuleKey != null &&
                            r.Measurement.Kind == "area")
                .ToList();
            // One finding per RULE, listing every record involved - not one per pair.
            // 50 overlapping kerb polygons produced 14 identical findings on the real
            // 6422 scan and buried the signal; the engineer needs one line with the set.
            var overlapsByRule = new Dictionary<string, (HashSet<string> Records, int Pairs)>(StringComparer.Ordinal);
            // Only records under the same rule can overlap-count; pairs are formed inside
            // each rule group instead of across every area record of the drawing.
            foreach (var ruleGroup in withBox.GroupBy(record => record.Classification.RuleKey!, StringComparer.Ordinal))
            {
                var members = ruleGroup.ToList();
                for (int i = 0; i < members.Count; i++)
                {
                    for (int j = i + 1; j < members.Count; j++)
                    {
                        var a = members[i];
                        var b = members[j];
                        if (a.Classification.RuleKey != b.Classification.RuleKey) continue;
                        if (a.Source.Handle == b.Source.Handle && a.Source.DrawingHash == b.Source.DrawingHash) continue;
                        if (!BoxesOverlap(a.Measurement.GeometryEvidence!, b.Measurement.GeometryEvidence!)) continue;

                        var key = a.Classification.RuleKey!;
                        if (!overlapsByRule.TryGetValue(key, out var acc))
                            acc = (new HashSet<string>(StringComparer.Ordinal), 0);
                        acc.Records.Add(a.RecordId);
                        acc.Records.Add(b.RecordId);
                        acc.Pairs++;
                        overlapsByRule[key] = acc;
                    }
                }
            }
            foreach (var (rule, acc) in overlapsByRule.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var f = new DeliveryFinding
                {
                    Code = EstimateFindingCodes.OverlapRisk,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"Overlapping '{rule}' areas — {acc.Pairs} overlapping pair(s) across {acc.Records.Count} objects, possible double count",
                    RecommendedAction = "Check whether these areas are the same surface drawn twice (e.g. host + detail) or genuinely adjacent. Overlaps are reported, never silently summed.",
                };
                foreach (var id in acc.Records.OrderBy(x => x, StringComparer.Ordinal)) f.AffectedRecordIds.Add(id);
                findings.Add(f);
            }

            // 4. A physical area can be classified through two different layer/rule
            // paths yet land in the same approved BOQ item. Looking only at RuleKey
            // misses that money-risk. Compare bound catalog identity + exact unit and
            // report overlaps across different rule keys; never merge or subtract.
            var approvedAreas = withBox
                .Where(r => HasBoundCatalogApproval(r.Classification) &&
                            Units.Parse(r.Measurement.Unit).Canonical != "?")
                .ToList();
            var crossRuleByCatalog = new Dictionary<
                (string CatalogId, string CatalogHash, string Code, string Unit),
                (HashSet<string> Records, HashSet<string> Rules, int Pairs)>();
            // A cross-rule pair must share drawing, approved catalog identity and unit;
            // pairs are formed inside that bucket only.
            foreach (var catalogGroup in approvedAreas.GroupBy(record => (
                         Drawing: record.Source.DrawingHash?.ToUpperInvariant(),
                         CatalogId: record.Classification.ApprovedCatalogId?.ToUpperInvariant(),
                         CatalogHash: record.Classification.ApprovedCatalogHash?.ToUpperInvariant(),
                         Code: record.Classification.CandidateCatalogCode?.ToUpperInvariant(),
                         Fingerprint: record.Classification.ApprovedCatalogItemFingerprint?.ToUpperInvariant(),
                         Unit: Units.Parse(record.Measurement.Unit).Canonical)))
            {
                var members = catalogGroup.ToList();
                for (int i = 0; i < members.Count; i++)
                {
                    for (int j = i + 1; j < members.Count; j++)
                    {
                        var a = members[i];
                        var b = members[j];
                        if (string.Equals(a.Classification.RuleKey, b.Classification.RuleKey,
                                StringComparison.Ordinal)) continue;
                        if (!string.Equals(a.Source.DrawingHash, b.Source.DrawingHash,
                                StringComparison.OrdinalIgnoreCase)) continue;
                        if (a.Source.Handle == b.Source.Handle) continue;
                        if (!string.Equals(a.Classification.ApprovedCatalogId,
                                b.Classification.ApprovedCatalogId, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(a.Classification.ApprovedCatalogHash,
                                b.Classification.ApprovedCatalogHash, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(a.Classification.CandidateCatalogCode,
                                b.Classification.CandidateCatalogCode, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(a.Classification.ApprovedCatalogItemFingerprint,
                                b.Classification.ApprovedCatalogItemFingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                        var aUnit = Units.Parse(a.Measurement.Unit).Canonical;
                        var bUnit = Units.Parse(b.Measurement.Unit).Canonical;
                        if (!string.Equals(aUnit, bUnit, StringComparison.Ordinal)) continue;
                        if (!BoxesOverlap(a.Measurement.GeometryEvidence!, b.Measurement.GeometryEvidence!)) continue;

                        var key = (
                            a.Classification.ApprovedCatalogId!.Trim().ToUpperInvariant(),
                            a.Classification.ApprovedCatalogHash!.Trim().ToLowerInvariant(),
                            a.Classification.CandidateCatalogCode!.Trim().ToUpperInvariant(),
                            aUnit);
                        if (!crossRuleByCatalog.TryGetValue(key, out var acc))
                            acc = (new HashSet<string>(StringComparer.Ordinal),
                                   new HashSet<string>(StringComparer.Ordinal), 0);
                        acc.Records.Add(a.RecordId);
                        acc.Records.Add(b.RecordId);
                        acc.Rules.Add(a.Classification.RuleKey ?? "?");
                        acc.Rules.Add(b.Classification.RuleKey ?? "?");
                        acc.Pairs++;
                        crossRuleByCatalog[key] = acc;
                    }
                }
            }
            foreach (var (key, acc) in crossRuleByCatalog
                         .OrderBy(k => k.Key.Code, StringComparer.Ordinal)
                         .ThenBy(k => k.Key.Unit, StringComparer.Ordinal))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.CrossSourceDuplicateRisk,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = $"שטחים חופפים משויכים לאותו סעיף מאושר {key.Code} ({key.Unit}) דרך {acc.Rules.Count} חוקים",
                    Message = $"נמצאו {acc.Pairs} זוגות חופפים. חוקים: " +
                              string.Join(", ", acc.Rules.OrderBy(x => x, StringComparer.Ordinal)),
                    RecommendedAction = "יש לבדוק את עצמי המקור. הכלי אינו מאחד או מחסיר שטחים אוטומטית.",
                    AffectedRecordIds = acc.Records.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                });
            }

            return findings;
        }

        private static void AddRepresentationMembership(
            RepresentationPoint point, string? otherKind, RepresentationIndex index,
            Dictionary<string, HashSet<string>> findings, bool differentHandle)
        {
            void Add(string? kind)
            {
                var key = $"{point.Record.Classification.RuleKey}|{kind}";
                if (!findings.TryGetValue(key, out var ids))
                    findings[key] = ids = new HashSet<string>(StringComparer.Ordinal);
                ids.Add(point.Record.RecordId);
            }

            if (string.Equals(point.Record.Measurement.Kind, otherKind, StringComparison.Ordinal))
            {
                if (index.HasMatch(point, int.MinValue, int.MaxValue, differentHandle)) Add(otherKind);
                return;
            }
            // Preserve the original reporting key even when measurement kinds differ
            // only in casing: it came from the earlier value-sorted record of a pair.
            if (index.HasMatch(point, int.MinValue, point.Order - 1, differentHandle)) Add(otherKind);
            if (index.HasMatch(point, point.Order + 1, int.MaxValue, differentHandle))
                Add(point.Record.Measurement.Kind);
        }

        private sealed class RepresentationPoint
        {
            internal readonly NeutralQuantityRecord Record;
            internal readonly int Order;
            internal readonly bool External;
            internal readonly double[] Values;

            internal RepresentationPoint(NeutralQuantityRecord record, int order)
            {
                Record = record;
                Order = order;
                External = !string.IsNullOrWhiteSpace(record.Source.Xref);
                var bounds = record.Measurement.GeometryEvidence!;
                Values = new[] { record.Measurement.RawValue, bounds[0], bounds[1], bounds[2], bounds[3] };
            }

            internal static bool IsUsable(NeutralQuantityRecord record) =>
                double.IsFinite(record.Measurement.RawValue) &&
                record.Measurement.GeometryEvidence is { Length: 4 } bounds && bounds.All(double.IsFinite);
        }

        /// <summary>
        /// A balanced range index over quantity and the four bounds coordinates.
        /// Range boxes are conservative; NearlyEqual is still the final authority.
        /// Uniform-handle subtrees are rejected at once for external pairs, so many
        /// occurrences of the same full source handle cannot restore an all-pairs scan.
        /// </summary>
        private sealed class RepresentationIndex
        {
            private readonly Node? _root;

            internal RepresentationIndex(IEnumerable<RepresentationPoint> points)
            {
                var ordered = points.ToArray();
                _root = Build(ordered, 0, ordered.Length, 0);
            }

            internal bool HasMatch(RepresentationPoint point, int first, int last, bool differentHandle)
            {
                if (_root == null) return false;
                var lower = new double[5];
                var upper = new double[5];
                for (var i = 0; i < 5; i++)
                {
                    // |x-y| <= e*max(1,|x|,|y|) implies a conservative radius
                    // e*max(1,|x|)/(1-e) about x, including across zero. Expand
                    // one ULP so rounding the range cannot exclude an exact match.
                    var radius = NearTolerance(point.Values[i], point.Values[i]) / (1.0 - 1e-7);
                    lower[i] = Math.BitDecrement(point.Values[i] - radius);
                    upper[i] = Math.BitIncrement(point.Values[i] + radius);
                }
                return Search(_root, point, lower, upper, first, last, differentHandle);
            }

            private static bool Search(Node? node, RepresentationPoint point, double[] lower,
                double[] upper, int first, int last, bool differentHandle)
            {
                if (node == null || node.Last < first || node.First > last ||
                    differentHandle && node.UniformHandle && string.Equals(
                        node.Point.Record.Source.Handle, point.Record.Source.Handle, StringComparison.Ordinal))
                    return false;
                for (var i = 0; i < 5; i++)
                    if (node.Maximum[i] < lower[i] || node.Minimum[i] > upper[i]) return false;
                var candidate = node.Point;
                if (candidate.Order >= first && candidate.Order <= last &&
                    (!differentHandle || !string.Equals(candidate.Record.Source.Handle,
                        point.Record.Source.Handle, StringComparison.Ordinal)) &&
                    NearlyEqual(point.Values[0], candidate.Values[0]) &&
                    SameBounds(point.Record.Measurement.GeometryEvidence,
                        candidate.Record.Measurement.GeometryEvidence)) return true;
                return Search(node.Left, point, lower, upper, first, last, differentHandle) ||
                       Search(node.Right, point, lower, upper, first, last, differentHandle);
            }

            private static Node? Build(RepresentationPoint[] points, int start, int count, int depth)
            {
                if (count == 0) return null;
                var axis = depth % 5;
                Array.Sort(points, start, count, Comparer<RepresentationPoint>.Create((a, b) =>
                    a.Values[axis].CompareTo(b.Values[axis])));
                var middle = start + count / 2;
                return new Node(points[middle], Build(points, start, middle - start, depth + 1),
                    Build(points, middle + 1, start + count - middle - 1, depth + 1));
            }

            private sealed class Node
            {
                internal readonly RepresentationPoint Point;
                internal readonly Node? Left, Right;
                internal readonly double[] Minimum, Maximum;
                internal int First, Last;
                internal bool UniformHandle = true;

                internal Node(RepresentationPoint point, Node? left, Node? right)
                {
                    Point = point;
                    Left = left;
                    Right = right;
                    First = Last = point.Order;
                    Minimum = (double[])point.Values.Clone();
                    Maximum = (double[])point.Values.Clone();
                    foreach (var child in new[] { left, right })
                    {
                        if (child == null) continue;
                        First = Math.Min(First, child.First);
                        Last = Math.Max(Last, child.Last);
                        UniformHandle &= child.UniformHandle && string.Equals(
                            point.Record.Source.Handle, child.Point.Record.Source.Handle, StringComparison.Ordinal);
                        for (var i = 0; i < 5; i++)
                        {
                            Minimum[i] = Math.Min(Minimum[i], child.Minimum[i]);
                            Maximum[i] = Math.Max(Maximum[i], child.Maximum[i]);
                        }
                    }
                }
            }
        }

        private static bool HasBoundCatalogApproval(QuantityClassification classification) =>
            !string.IsNullOrWhiteSpace(classification.CandidateCatalogCode) &&
            !string.IsNullOrWhiteSpace(classification.ApprovedCatalogId) &&
            CatalogIdentity.IsValidSha256(classification.ApprovedCatalogHash) &&
            CatalogIdentity.IsValidSha256(classification.ApprovedCatalogItemFingerprint);

        private static bool BoxesOverlap(double[] a, double[] b) =>
            a[0] < b[2] && b[0] < a[2] && a[1] < b[3] && b[1] < a[3];

        private static bool SameBounds(double[]? left, double[]? right)
        {
            if (left is not { Length: 4 } || right is not { Length: 4 }) return false;
            return left.Zip(right, NearlyEqual).All(equal => equal);
        }

        private static string TerminalSourceHandle(string? handlePath)
        {
            if (string.IsNullOrWhiteSpace(handlePath)) return string.Empty;
            var separator = handlePath.LastIndexOf('/');
            return separator < 0 ? handlePath : handlePath[(separator + 1)..];
        }

        private static double NearTolerance(double left, double right) =>
            1e-7 * Math.Max(1.0, Math.Max(Math.Abs(left), Math.Abs(right)));

        private static bool NearlyEqual(double left, double right)
        {
            if (!double.IsFinite(left) || !double.IsFinite(right)) return false;
            return Math.Abs(left - right) <= NearTolerance(left, right);
        }
    }

    /// <summary>
    /// The single monetary interpretation shared by the audit and the workbook.
    /// Object quantities retain four decimals; money is calculated once per printed
    /// BOQ group and rounded away from zero exactly like Excel ROUND(...,2).
    /// </summary>
    public static class EstimateBoqSemantics
    {
        public const int QuantityDecimals = 4;
        public const int MoneyDecimals = 2;

        public sealed record Group(
            string? CatalogCode,
            string? Description,
            string? SourceLayer,
            string Unit,
            decimal Quantity,
            decimal? Price,
            bool IncludedInTotals,
            string Status,
            int ObjectCount,
            decimal? Total);

        public static IReadOnlyList<Group> GroupLines(IEnumerable<EstimateLine> lines)
        {
            return lines
                .GroupBy(l => (
                    Key: l.CatalogCode ?? ("layer:" + (l.SourceLayer ?? "?")),
                    Unit: l.Unit ?? "?",
                    Price: l.Price,
                    Included: l.IncludedInTotals,
                    Status: StatusText(l)))
                .Select(g =>
                {
                    // EstimateBuilder stores every line at the canonical four-decimal
                    // precision. Do not round it a second time while grouping.
                    var quantity = g.Sum(x => StoredQuantity(x.BoqQuantity));
                    var total = g.Key.Included && g.Key.Price != null
                        ? RoundMoney(quantity * g.Key.Price.Value)
                        : (decimal?)null;
                    var first = g.First();
                    return new Group(
                        first.CatalogCode,
                        first.Description,
                        first.SourceLayer,
                        g.Key.Unit,
                        quantity,
                        g.Key.Price,
                        g.Key.Included,
                        g.Key.Status,
                        g.Count(),
                        total);
                })
                .ToList();
        }

        public static decimal CalculateCleanTotal(IEnumerable<EstimateLine> lines) =>
            GroupLines(lines.Where(l => l.IncludedInTotals))
                .Sum(g => g.Total ?? 0m);

        public const string MonetaryRangeExceededCode = "EST-MONETARY-RANGE-EXCEEDED";

        internal static void AssignCleanTotalOrBlock(EstimateResult result)
        {
            try { result.CleanTotal = CalculateCleanTotal(result.Lines); }
            catch (OverflowException)
            {
                BlockMonetaryOverflow(result, result.Lines.Where(line => line.IncludedInTotals).ToList(),
                    "The grouped quantity × price or sum of monetary subtotals exceeds the decimal calculation range.");
                // Non-authoritative storage sentinel: every affected line is failed,
                // excluded and has no total. The error forbids export, never a zero estimate.
                result.CleanTotal = 0m;
            }
        }

        internal static void BlockMonetaryOverflow(EstimateResult result, IReadOnlyList<EstimateLine> lines, string detail)
        {
            var finding = new DeliveryFinding
            {
                Code = MonetaryRangeExceededCode, Domain = "estimate", Severity = FindingSeverity.Error,
                Title = "הסכום חורג מטווח החישוב — אין סכום מאומת ואין ייצוא",
                Message = detail + " Raw measurements and entered prices are preserved; no clamped or partial total is substituted.",
                RecommendedAction = "בדוק את הכמות, היחידה והמחיר שהוזנו; תקן את הקלט השגוי ובנה מחדש. לא נקבעה תקרת מחיר הנדסית.",
                AffectedRecordIds = lines.Select(line => line.RecordId).Distinct(StringComparer.Ordinal).ToList(),
            };
            result.Findings.Add(finding);
            foreach (var line in lines)
            {
                line.Findings.Add(finding);
                line.Total = null;
                line.IncludedInTotals = false;
                line.Status = DeliveryStatus.Failed;
            }
            result.Status = DeliveryStatus.Failed;
        }

        public static decimal CanonicalQuantity(double value)
        {
            if (!double.IsFinite(value))
                throw new InvalidOperationException("A non-finite quantity cannot enter a BOQ total.");
            return Math.Round(Convert.ToDecimal(value), QuantityDecimals,
                MidpointRounding.AwayFromZero);
        }

        /// <summary>Reads an already-canonical line quantity without rounding it again.</summary>
        public static decimal StoredQuantity(double value)
        {
            if (!double.IsFinite(value))
                throw new InvalidOperationException("A non-finite quantity cannot enter a BOQ total.");
            return Convert.ToDecimal(value);
        }

        public static decimal RoundMoney(decimal value) =>
            Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);

        public static string StatusText(EstimateLine line) => line.PriceStatus switch
        {
            PriceStatus.Priced when line.IncludedInTotals => "תקין",
            PriceStatus.ProjectOverride => "מחיר פרויקט מאושר",
            PriceStatus.MissingPrice => "חסר מחיר במחירון",
            PriceStatus.Unmapped => "דרוש מיפוי קטלוגי",
            _ => "דרושה בדיקה",
        } + (line.Findings.Any(f => f.Code == EstimateFindingCodes.UnitMismatch)
            ? " | אי-התאמת יחידות"
            : "");
    }

    /// <summary>
    /// Estimate preflight findings that can change money are hard gates. They travel
    /// from Scan into the built result, exclude the affected records from totals and
    /// prevent export until the source model is corrected and rescanned. A build may
    /// remain visible as an audit draft, but final export is fail-closed: every line
    /// must be mapped, unit-safe, priced, included and Ready.
    /// </summary>
    public static class EstimatePreflightPolicy
    {
        public const string CorridorOutOfDateCode = "EST-CORRIDOR-OUT-OF-DATE";
        public const string CorridorQtoFailedCode = "EST-CORRIDOR-QTO-FAILED";
        public const string CorridorMaterialReadFailedCode = "EST-CORRIDOR-MATERIAL-QTO-FAILED";
        public const string CorridorMaterialCoverageIncompleteCode = "EST-CORRIDOR-MATERIAL-COVERAGE-INCOMPLETE";
        public const string EarthworksQtoFailedCode = "EST-EARTHWORKS-QTO-FAILED";
        public const string EarthworksSourceUnverifiedCode = "EST-EARTHWORKS-SOURCE-UNVERIFIED";
        public const string EarthworksNotAssessedCode = "EST-EARTHWORKS-NOT-ASSESSED";

        public const string DiscoverAllSourceScopePolicy = "discover-all";
        public const string ReviewedSourcesPolicy = "reviewed-sources";
        public const string RulesOnlySourceScopePolicy = "rules-only";
        public const string HostOnlyXrefPolicy = "host-only";
        public const string IncludeXrefsPolicy = "include-xrefs";
        public const string HostOnlyScopeNotice = "HOST ONLY — XREF CONTENT EXCLUDED";
        public const string CompleteScopeNotice = "HOST + XREF — RECURSIVE VERIFIED SOURCES";
        public const string NeutralRecordScopeNotice = "NEUTRAL RECORD SET — NO LIVE CAD / XREF VERIFICATION";

        public static string ReviewedScopeNotice(EstimateSourceSelection selection, string? snapshotKind) =>
            (string.Equals(snapshotKind, EstimateBuildContext.NeutralRecordSet, StringComparison.OrdinalIgnoreCase)
                ? NeutralRecordScopeNotice + " · " : "REVIEWED SOURCE SCOPE · ") +
            $"{selection.Sources.Count(s => s.Included)} מקורות כלולים; {selection.Sources.Count(s => !s.Included)} מוחרגים שלא נמדדו. " +
            "הפירוט והאישור בגיליון זהות ראיות; האומדן מתייחס להיקף שנבחר בלבד.";

        /// <summary>
        /// ReviewRequired/Error means an engineering decision is unresolved. A
        /// whitelist is unsafe here: a newly introduced finding code must fail closed
        /// until it is explicitly resolved, rather than silently becoming exportable.
        /// </summary>
        public static bool IsBlocking(DeliveryFinding finding) =>
            finding.Severity >= FindingSeverity.ReviewRequired &&
            (finding.ResolvedAtUtc == null ||
             string.IsNullOrWhiteSpace(finding.ResolvedBy) ||
             string.IsNullOrWhiteSpace(finding.Resolution));

        /// <summary>
        /// Validates the explicit drawing-source contract. A missing value is not a
        /// host-only default: the scan may continue to collect evidence, but export is
        /// blocked because the product contract requires host plus recursive XREF
        /// coverage.  Only an explicit include-xrefs approval can become exportable.
        /// </summary>
        public static IReadOnlyList<DeliveryFinding> ValidateSourcePolicies(ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var findings = new List<DeliveryFinding>();
            var sourceScope = profile.Estimate.QuantitySources.SourceScopePolicy?.Trim();
            var xref = profile.Estimate.QuantitySources.XrefPolicy?.Trim();

            if (string.IsNullOrWhiteSpace(sourceScope))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.SourceScopePolicyUnapproved,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "לא אושרה מדיניות מקורות הכמות",
                    Message = $"יש לבחור במפורש '{DiscoverAllSourceScopePolicy}'. הסריקה ממשיכה לצורכי ביקורת בלבד.",
                    RecommendedAction = "יש לאשר את היקף מקורות הכמות בפרופיל הפרויקט ולהריץ סריקה חדשה.",
                    ProjectProfileId = profile.ProfileId,
                });
            }
            else if (!string.Equals(sourceScope, DiscoverAllSourceScopePolicy, StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(sourceScope, ReviewedSourcesPolicy, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.SourceScopePolicyUnapproved,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = string.Equals(sourceScope, RulesOnlySourceScopePolicy, StringComparison.OrdinalIgnoreCase)
                        ? "סריקת rules-only אסורה: היא עלולה להסתיר עבודות ללא מיפוי"
                        : $"מדיניות מקורות הכמות '{Bidi.Ltr(sourceScope)}' אינה נתמכת",
                    Message = "החילוץ תמיד סורק את כל הישויות הנתמכות בשרטוט המארח; חוקים מאושרים מסווגים אך לעולם אינם מצמצמים את הסריקה.",
                    RecommendedAction = $"יש לבחור '{DiscoverAllSourceScopePolicy}' ולהריץ סריקה חדשה.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            var selectedScope = string.Equals(sourceScope, ReviewedSourcesPolicy, StringComparison.OrdinalIgnoreCase);
            var selection = profile.Estimate.SourceSelection;
            var selectionProblems = selection == null ? new List<string>() : EstimateSourceSelectionPolicy.ValidationProblems(selection).ToList();
            if (selectedScope && selection == null) selectionProblems.Add("חסרה בחירת מקורות מאושרת");
            if (!selectedScope && selection != null) selectionProblems.Add("בחירת מקורות מחייבת מדיניות reviewed-sources");
            if (selection?.Sources?.Any(s => s.Key == EstimateSourceSelectionPolicy.HostKey && !s.Included) == true &&
                profile.Estimate.Earthworks.Requested == true)
                selectionProblems.Add("המארח מוחרג אך עבודות העפר שלו נדרשות — יש ליישב את החלטות ההיקף");
            foreach (var problem in selectionProblems)
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateSourceSelectionPolicy.InvalidScopeCode, Domain = "estimate", Severity = FindingSeverity.Error,
                    Title = "נדרשת סקירת היקף המקורות", Message = problem, ProjectProfileId = profile.ProfileId,
                });

            if (string.IsNullOrWhiteSpace(xref))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.XrefPolicyUnapproved,
                    Domain = "estimate",
                    Severity = FindingSeverity.ReviewRequired,
                    Title = "לא אושרה מדיניות XREF לכמויות",
                    Message = "הסריקה אוספת מדידות גם מהפניות שניתן לאמת; היקף המקורות עדיין אינו מאושר לאומדן סופי.",
                    RecommendedAction = $"יש לאשר '{IncludeXrefsPolicy}' בפרופיל הפרויקט ולהריץ סריקה חדשה.",
                    ProjectProfileId = profile.ProfileId,
                });
            }
            else if (string.Equals(xref, HostOnlyXrefPolicy, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.XrefPolicyUnapproved,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = "סריקת host-only אינה מכסה את מקורות האומדן הנדרשים",
                    Message = "חוזה האומדן מחייב סריקה רקורסיבית של host+XREF עם provenance ובקרת כפל.",
                    RecommendedAction = $"יש לבחור '{IncludeXrefsPolicy}' ולהריץ סריקה חדשה.",
                    ProjectProfileId = profile.ProfileId,
                });
            }
            else if (!string.Equals(xref, IncludeXrefsPolicy, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new DeliveryFinding
                {
                    Code = EstimateFindingCodes.XrefPolicyUnapproved,
                    Domain = "estimate",
                    Severity = FindingSeverity.Error,
                    Title = $"מדיניות XREF '{Bidi.Ltr(xref)}' אינה נתמכת",
                    RecommendedAction = $"יש לבחור '{IncludeXrefsPolicy}' ולהריץ סריקה חדשה.",
                    ProjectProfileId = profile.ProfileId,
                });
            }

            return findings;
        }

        /// <summary>
        /// A finding with record ids blocks only when at least one of those records is
        /// present in this result (ignored/non-priced rules must not poison the rest).
        /// A blocker without ids is global by definition.
        /// </summary>
        public static IReadOnlyList<DeliveryFinding> BlockingFindings(EstimateResult result)
        {
            var recordIds = result.Lines.Select(l => l.RecordId)
                .ToHashSet(StringComparer.Ordinal);
            var applicableTopLevel = result.Findings
                .Where(IsBlocking)
                .Where(f => f.AffectedRecordIds.Count == 0 ||
                            f.AffectedRecordIds.Any(recordIds.Contains));
            // A finding physically attached to a retained line is applicable even if
            // a malformed producer omitted/mistyped its affected_record_ids list.
            var attachedToLines = result.Lines
                .SelectMany(l => l.Findings)
                .Where(IsBlocking);
            return DistinctEquivalentFindings(applicableTopLevel.Concat(attachedToLines)).ToList();
        }

        // One finding may occur on the scan and every affected line. Only identical
        // engineering content is redundant: code + empty record IDs is shared by
        // independent corridor failures, and even a reused FindingId is not evidence
        // that two different source references or repair explanations are equivalent.
        private static readonly JsonSerializerOptions FindingKeyJson = new()
        {
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        internal static string FindingEquivalenceKey(DeliveryFinding finding) => JsonSerializer.Serialize(new
        {
            finding.Code, finding.Domain, finding.Severity, finding.Title, finding.Message,
            finding.ProjectProfileId, finding.RunId, finding.ProjectionRole,
            Sources = finding.SourceRefs.Select(source => JsonSerializer.Serialize(source, FindingKeyJson))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
            Records = finding.AffectedRecordIds.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
            Evidence = finding.EvidenceRefs.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal),
            Bounds = finding.SourceBoundsWcs?.Select(value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
            finding.RecommendedAction, finding.ResolvedAtUtc, finding.Resolution, finding.ResolvedBy,
        });

        internal static IEnumerable<DeliveryFinding> DistinctEquivalentFindings(IEnumerable<DeliveryFinding> findings)
        {
            var references = new HashSet<DeliveryFinding>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var finding in findings)
                if (references.Add(finding) && keys.Add(FindingEquivalenceKey(finding)))
                    yield return finding;
        }

        /// <summary>Stable, human-readable reasons used by every export boundary.</summary>
        public static IReadOnlyList<string> ExportBlockingReasons(EstimateResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            var reasons = new List<string>();

            if (result.Status != DeliveryStatus.Ready)
                reasons.Add($"result-status:{result.Status}");
            if (result.Lines.Count == 0)
                reasons.Add("no-estimate-lines");
            if (result.ExcludedLineCount != 0)
                reasons.Add($"excluded-lines:{result.ExcludedLineCount}");
            var selectedScope = string.Equals(result.SourceScopePolicy, ReviewedSourcesPolicy, StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(result.SourceScopePolicy, DiscoverAllSourceScopePolicy,
                    StringComparison.OrdinalIgnoreCase) && !selectedScope)
                reasons.Add("source-scope-untrusted");
            if (selectedScope && (result.SourceSelection == null ||
                EstimateSourceSelectionPolicy.ValidationProblems(result.SourceSelection).Count != 0) ||
                !selectedScope && result.SourceSelection != null)
                reasons.Add("source-selection-untrusted");
            if (!string.Equals(result.XrefPolicy, IncludeXrefsPolicy,
                    StringComparison.OrdinalIgnoreCase))
                reasons.Add("xref-scope-untrusted");
            var expectedScopeNotice = string.Equals(result.SourceSnapshotKind, EstimateBuildContext.NeutralRecordSet,
                StringComparison.OrdinalIgnoreCase) ? NeutralRecordScopeNotice : CompleteScopeNotice;
            if (selectedScope && result.SourceSelection != null)
                expectedScopeNotice = ReviewedScopeNotice(result.SourceSelection, result.SourceSnapshotKind);
            if (!string.Equals(result.ScopeNotice, expectedScopeNotice,
                    StringComparison.Ordinal))
                reasons.Add("scope-notice-missing");
            if (!CatalogIdentity.IsValidSha256(result.ProjectProfileHash))
                reasons.Add("trace:profile-hash-missing");
            if (!CatalogIdentity.IsValidSha256(result.ProjectProfileEffectiveHash))
                reasons.Add("trace:effective-profile-hash-missing");
            if (string.IsNullOrWhiteSpace(result.ProjectProfileHashKind))
                reasons.Add("trace:profile-hash-kind-missing");
            if (string.IsNullOrWhiteSpace(result.SourceSnapshotKind))
                reasons.Add("trace:source-snapshot-kind-missing");
            if (string.IsNullOrWhiteSpace(result.SourceDrawingPath))
                reasons.Add("trace:source-drawing-path-missing");
            if (!CatalogIdentity.IsValidSha256(result.SourceDrawingHash))
                reasons.Add("trace:source-drawing-hash-missing");
            if (string.IsNullOrWhiteSpace(result.SourceDatabaseRevision))
                reasons.Add("trace:source-revision-missing");
            foreach (var external in result.ExternalSources)
            {
                if (string.IsNullOrWhiteSpace(external.DrawingPath) ||
                    !CatalogIdentity.IsValidSha256(external.DrawingHash) ||
                    string.IsNullOrWhiteSpace(external.XrefChain) ||
                    string.IsNullOrWhiteSpace(external.ReferenceHandlePath))
                    reasons.Add("trace:xref-source-incomplete");
            }
            if (result.Lines.Any(line => !string.IsNullOrWhiteSpace(line.SourceXref) &&
                    !result.ExternalSources.Any(source =>
                        string.Equals(source.DrawingPath, line.SourceDrawingPath,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(source.DrawingHash, line.SourceDrawingHash,
                            StringComparison.OrdinalIgnoreCase))))
                reasons.Add("trace:xref-source-unregistered");
            if (string.Equals(result.SourceSnapshotKind, EstimateBuildContext.CivilLiveSaved,
                    StringComparison.Ordinal) && result.SourceDbMod != 0)
                reasons.Add("trace:source-drawing-not-saved");

            foreach (var line in result.Lines)
            {
                var prefix = $"line:{line.LineId}";
                if (line.Status != DeliveryStatus.Ready) reasons.Add($"{prefix}:status:{line.Status}");
                if (!line.IncludedInTotals) reasons.Add($"{prefix}:excluded");
                if (line.PriceStatus is not (PriceStatus.Priced or PriceStatus.ProjectOverride))
                    reasons.Add($"{prefix}:price-status:{line.PriceStatus}");
                if (line.Price == null) reasons.Add($"{prefix}:unpriced");
                if (line.Total == null) reasons.Add($"{prefix}:no-total");
                if (string.IsNullOrWhiteSpace(line.CatalogCode)) reasons.Add($"{prefix}:unmapped");
                if (Units.Parse(line.Unit).Canonical == "?") reasons.Add($"{prefix}:unit-unknown");
                if (string.IsNullOrWhiteSpace(line.SourceDrawingPath)) reasons.Add($"{prefix}:source-path-missing");
                if (!CatalogIdentity.IsValidSha256(line.SourceDrawingHash)) reasons.Add($"{prefix}:source-hash-missing");
                if (string.IsNullOrWhiteSpace(line.SourceDatabaseRevision)) reasons.Add($"{prefix}:source-revision-missing");
                if (string.IsNullOrWhiteSpace(line.SourceHandle)) reasons.Add($"{prefix}:source-handle-missing");
                if (string.IsNullOrWhiteSpace(line.MeasurementMethod)) reasons.Add($"{prefix}:measurement-method-missing");
                if (string.IsNullOrWhiteSpace(line.RuleKey)) reasons.Add($"{prefix}:rule-key-missing");
                if (string.IsNullOrWhiteSpace(line.MappingApprovedBy) || line.MappingApprovedAtUtc == null)
                    reasons.Add($"{prefix}:mapping-authority-missing");
                if (!string.Equals(line.ApprovedCatalogId, result.PriceBookId, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(line.ApprovedCatalogHash, result.PriceBookHash, StringComparison.OrdinalIgnoreCase) ||
                    !CatalogIdentity.IsValidSha256(line.ApprovedCatalogItemFingerprint))
                    reasons.Add($"{prefix}:mapping-catalog-binding-missing");
                if (string.IsNullOrWhiteSpace(line.PriceDecisionSource))
                    reasons.Add($"{prefix}:price-source-missing");
            }

            reasons.AddRange(BlockingFindings(result).Select(f => f.Code));
            return reasons.Distinct(StringComparer.Ordinal).ToList();
        }

        public static bool CanExport(EstimateResult result) =>
            ExportBlockingReasons(result).Count == 0;

        internal static void Apply(
            EstimateResult result,
            IEnumerable<DeliveryFinding> preflightFindings)
        {
            var findings = DistinctEquivalentFindings(preflightFindings).ToList();
            var findingKeys = findings.ToDictionary(finding => finding, FindingEquivalenceKey);
            var impacts = EstimateFindingImpactPolicy.CreateIndex(findings);
            var knownKeys = result.Findings.Select(FindingEquivalenceKey)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var finding in findings)
            {
                if (knownKeys.Add(findingKeys[finding])) result.Findings.Add(finding);
            }

            var resultRecordIds = result.Lines.Select(l => l.RecordId)
                .ToHashSet(StringComparer.Ordinal);
            var blockers = findings
                .Where(IsBlocking)
                .Where(f => f.AffectedRecordIds.Count == 0 ||
                            f.AffectedRecordIds.Any(resultRecordIds.Contains) ||
                            f.SourceRefs.Count > 0 || f.Code.StartsWith("SEC-", StringComparison.Ordinal))
                .ToList();
            if (blockers.Count == 0)
            {
                result.Status = DeliveryStatusRules.CapByFindings(result.Status, result.Findings);
                return;
            }

            foreach (var line in result.Lines)
            {
                var affecting = impacts.BlockingFindings(line);
                if (affecting.Count == 0) continue;

                var lineKeys = line.Findings.Select(FindingEquivalenceKey).ToHashSet(StringComparer.Ordinal);
                foreach (var finding in affecting)
                {
                    if (lineKeys.Add(findingKeys[finding]))
                        line.Findings.Add(finding);
                }
                line.Total = null;
                line.IncludedInTotals = false;
                line.Status = line.Findings.Any(f => f.Severity == FindingSeverity.Error)
                    ? DeliveryStatus.Failed
                    : DeliveryStatus.ReviewRequired;
            }

            EstimateBoqSemantics.AssignCleanTotalOrBlock(result);
            result.ExcludedLineCount = result.Lines.Count(l => !l.IncludedInTotals);
            result.Status = DeliveryStatusRules.Aggregate(result.Lines.Select(l => l.Status)
                .Append(blockers.Any(f => f.Severity == FindingSeverity.Error)
                    ? DeliveryStatus.Failed
                    : DeliveryStatus.ReviewRequired)
                .ToList());
        }
    }

    /// <summary>
    /// Deterministic estimate assembly: neutral records → lines with catalog identity,
    /// price resolution (override → snapshot → MISSING_PRICE), unit gate, adjustment
    /// chain, statuses and totals. Headless; zero Autodesk deps.
    /// </summary>
    public static class EstimateBuilder
    {
        public static EstimateResult Build(
            IReadOnlyList<NeutralQuantityRecord> records,
            CatalogSnapshot snapshot,
            ProjectProfile profile,
            string? runId = null,
            IEnumerable<DeliveryFinding>? preflightFindings = null,
            EstimateBuildContext? traceContext = null)
        {
            traceContext ??= EstimateTraceIdentity.InferHeadless(records, profile);
            var result = new EstimateResult
            {
                RunId = runId ?? RunManifest.NewRunId("estimate", "build"),
                ProjectProfileId = profile.ProfileId,
                ProjectProfileHash = traceContext.ProjectProfileHash,
                ProjectProfileHashKind = traceContext.ProjectProfileHashKind,
                ProjectProfileEffectiveHash = traceContext.ProjectProfileEffectiveHash,
                PriceBookId = snapshot.SnapshotId,
                PriceBookHash = snapshot.FileHash,
                SourceScopePolicy = profile.Estimate.QuantitySources.SourceScopePolicy,
                SourceSelection = profile.Estimate.SourceSelection == null ? null : JsonSerializer.Deserialize<EstimateSourceSelection>(
                    JsonSerializer.Serialize(profile.Estimate.SourceSelection)),
                XrefPolicy = profile.Estimate.QuantitySources.XrefPolicy,
                ScopeNotice = profile.Estimate.SourceSelection != null
                    ? EstimatePreflightPolicy.ReviewedScopeNotice(profile.Estimate.SourceSelection, traceContext.SourceSnapshotKind)
                    : string.Equals(traceContext.SourceSnapshotKind, EstimateBuildContext.NeutralRecordSet,
                    StringComparison.OrdinalIgnoreCase)
                    ? EstimatePreflightPolicy.NeutralRecordScopeNotice
                    : string.Equals(profile.Estimate.QuantitySources.XrefPolicy,
                    EstimatePreflightPolicy.IncludeXrefsPolicy, StringComparison.OrdinalIgnoreCase)
                    ? EstimatePreflightPolicy.CompleteScopeNotice
                    : EstimatePreflightPolicy.HostOnlyScopeNotice,
                SourceSnapshotKind = traceContext.SourceSnapshotKind,
                SourceDrawingPath = traceContext.SourceDrawingPath,
                SourceDrawingHash = traceContext.SourceDrawingHash,
                SourceDatabaseRevision = traceContext.SourceDatabaseRevision,
                SourceDbMod = traceContext.SourceDbMod,
                ExternalSources = traceContext.ExternalSources.ToList(),
            };

            // Caller findings augment the mandatory local detector; passing a benign
            // list must never suppress duplicate/overlap gates. Prefer the caller's
            // instance when Scan already emitted truly equivalent evidence. Distinct
            // global corridor/source reasons must retain their independent identities.
            var preflight = EstimatePreflightPolicy.DistinctEquivalentFindings(
                (preflightFindings ?? Enumerable.Empty<DeliveryFinding>())
                // The public build boundary must not depend on a Civil-side caller
                // remembering to forward source-policy findings from Scan.
                .Concat(EstimatePreflightPolicy.ValidateSourcePolicies(profile))
                .Concat(EstimateConfigurationPolicy.Validate(profile))
                .Concat(DuplicateRiskDetector.Detect(records))
                // Build is a public boundary too.  A caller must not be able to
                // bypass the scan-stage significance/mixed-dimension gates by
                // invoking EstimateBuilder directly or by dropping scan findings.
                .Concat(QuantitySignificance.DetectReviewFindings(
                    records, IgnoredRulePolicy.ApprovedKeys(profile))))
                .ToList();

            int lineNo = 0;
            foreach (var record in records)
            {
                lineNo++;
                var line = new EstimateLine
                {
                    LineId = $"L{lineNo:D4}",
                    RecordId = record.RecordId,
                    SourceLayer = record.Source.Layer,
                    SourceDrawing = record.Source.Drawing,
                    SourceDrawingPath = record.Source.DrawingPath ?? record.Source.Drawing,
                    SourceDrawingHash = record.Source.DrawingHash,
                    SourceDatabaseRevision = traceContext.SourceDatabaseRevision,
                    SourceHandle = record.Source.Handle,
                    SourceEntityType = record.Source.EntityType,
                    SourceXref = record.Source.Xref,
                    SourceCivilIdentity = record.Source.CivilIdentity,
                    SourceStationFrom = record.Source.StationFrom,
                    SourceStationTo = record.Source.StationTo,
                    MeasurementMethod = record.Measurement.Method,
                    SourceMeasurementStatus = record.Status,
                    SourceMeasurementKind = record.Measurement.Kind,
                    RuleKey = record.Classification.RuleKey,
                    MappingApprovedBy = record.Classification.MappingApprovedBy,
                    MappingApprovedAtUtc = record.Classification.MappingApprovedAtUtc,
                    ApprovedCatalogId = record.Classification.ApprovedCatalogId,
                    ApprovedCatalogHash = record.Classification.ApprovedCatalogHash,
                    ApprovedCatalogItemFingerprint = record.Classification.ApprovedCatalogItemFingerprint,
                    RawQuantity = record.Measurement.RawValue,
                    Unit = record.Measurement.Unit,
                };
                result.Lines.Add(line);
                // Source measurement failures remain failures after item/price resolution.
                // Preserve the original evidence on its row; do not promote it to a
                // global finding or silently reinterpret its source/affected IDs.
                line.Findings.AddRange(record.Findings);

                // ------------------------------------------------------- mapping
                var code = record.Classification.CandidateCatalogCode;
                if (!double.IsFinite(line.RawQuantity) || line.RawQuantity <= 0)
                {
                    line.CatalogCode = code;
                    var preflightMeasurementFailure = preflight.FirstOrDefault(f =>
                        f.Code == EstimateFindingCodes.MeasurementFailed &&
                        f.AffectedRecordIds.Contains(record.RecordId, StringComparer.Ordinal));
                    MarkInvalidQuantity(result, line, line.RawQuantity, "raw measurement",
                        preflightMeasurementFailure);
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(code) &&
                    !CatalogIdentity.IsClassificationCurrent(record.Classification, snapshot))
                {
                    line.CatalogCode = code;
                    line.PriceStatus = PriceStatus.Unmapped;
                    line.Status = DeliveryStatus.ReviewRequired;
                    line.BoqQuantity = (double)EstimateBoqSemantics.CanonicalQuantity(line.RawQuantity);
                    line.Findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.Unmapped,
                        Domain = "estimate",
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"המיפוי לרשומה {record.RecordId} אינו קשור למחירון הפעיל — נדרשת סריקה/אישור מחדש",
                        Message = "A mapped neutral record must carry the approved catalog id, 64-hex hash and exact item fingerprint used during extraction.",
                        AffectedRecordIds = { record.RecordId },
                    });
                    continue;
                }
                if (string.IsNullOrWhiteSpace(code))
                {
                    line.PriceStatus = PriceStatus.Unmapped;
                    line.Status = DeliveryStatus.ReviewRequired;
                    line.BoqQuantity = (double)EstimateBoqSemantics.CanonicalQuantity(line.RawQuantity);
                    line.Findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.Unmapped,
                        Domain = "estimate",
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"אין שיוך לסעיף מחירון לרשומה {record.RecordId} ({record.Classification.RuleKey ?? record.Source.Layer})",
                        AffectedRecordIds = { record.RecordId },
                    });
                    continue;
                }

                if (!snapshot.Items.TryGetValue(code, out var item))
                {
                    line.CatalogCode = code;
                    line.PriceStatus = PriceStatus.Unmapped;
                    line.Status = DeliveryStatus.ReviewRequired;
                    line.BoqQuantity = (double)EstimateBoqSemantics.CanonicalQuantity(line.RawQuantity);
                    line.Findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.Unmapped,
                        Domain = "estimate",
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"הסעיף '{Bidi.Ltr(code)}' אינו קיים במהדורת המחירון {Bidi.Ltr(snapshot.SnapshotId)}",
                        AffectedRecordIds = { record.RecordId },
                    });
                    continue;
                }

                line.CatalogCode = item.Code;
                line.Description = item.Description;
                line.Unit = item.UnitRaw;

                // ---------------------------------------------------------- unit
                var unitFinding = UnitValidator.Validate(record, item);
                bool unitOk = unitFinding == null;
                if (unitFinding != null) line.Findings.Add(unitFinding);

                // --------------------------------------------------- adjustments
                var adjOutcome = AdjustmentEngine.Apply(
                    record.Measurement.RawValue,
                    profile.Estimate.ApprovedAdjustments,
                    profile.Estimate.CandidateAdjustments,
                    record.RecordId,
                    record.Classification.RuleKey,
                    item.Code);
                line.Adjustments.AddRange(adjOutcome.Steps);
                line.Findings.AddRange(adjOutcome.Findings);
                if (!double.IsFinite(adjOutcome.BoqValue) || adjOutcome.BoqValue <= 0)
                {
                    MarkInvalidQuantity(result, line, adjOutcome.BoqValue, "adjusted BOQ quantity");
                    continue;
                }
                line.BoqQuantity = (double)EstimateBoqSemantics.CanonicalQuantity(adjOutcome.BoqValue);

                // ---------------------------------------------------------- price
                var overrideRules = profile.Estimate.ProjectOverrides.Where(o =>
                    string.Equals(o.ItemCode?.Trim(), item.Code.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (overrideRules.Count > 1)
                {
                    line.Price = null;
                    line.PriceStatus = PriceStatus.MissingPrice;
                    line.PriceBookId = "override:AMBIGUOUS";
                    line.Findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.ConfigurationAmbiguous,
                        Domain = "estimate",
                        Severity = FindingSeverity.Error,
                        Title = $"לסעיף '{Bidi.Ltr(item.Code)}' נמצאו {overrideRules.Count} מחירי פרויקט מתחרים",
                        Message = "No project price was selected; list order is never price authority.",
                        AffectedRecordIds = { record.RecordId },
                    });
                }
                else if (overrideRules.Count == 1)
                {
                    var overrideRule = overrideRules[0];
                    var complete = overrideRule.Price != null &&
                                   overrideRule.Price > 0 &&
                                   !string.IsNullOrWhiteSpace(overrideRule.Source) &&
                                   !string.IsNullOrWhiteSpace(overrideRule.Reason) &&
                                   !string.IsNullOrWhiteSpace(overrideRule.ApprovedBy) &&
                                   overrideRule.ApprovedAtUtc != null;
                    if (!ProjectPriceApprovalPolicy.BindingMatches(overrideRule, snapshot, item))
                    {
                        line.Price = null;
                        line.PriceStatus = PriceStatus.MissingPrice;
                        line.PriceBookId = "override:STALE-CATALOG";
                        line.Findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.PriceSourceUnverified,
                            Domain = "estimate", Severity = FindingSeverity.Error,
                            Title = "מחיר הפרויקט אושר לסעיף או למהדורת מחירון אחרים",
                            Message = "The project-price catalog binding is incomplete or no longer matches the exact item/hash/unit.",
                            RecommendedAction = "פתח אישור מחיר פרויקט מחדש ובדוק מקור, יחידה וסעיף; אין נפילה אוטומטית למחיר המחירון.",
                            AffectedRecordIds = { record.RecordId },
                        });
                    }
                    else if (complete)
                    {
                        line.Price = overrideRule.Price;
                        line.PriceStatus = PriceStatus.ProjectOverride;
                        line.PriceBookId = $"override:{overrideRule.Source}";
                        line.PriceDecisionSource = overrideRule.Source;
                        line.PriceDecisionReason = overrideRule.Reason;
                        line.PriceApprovedBy = overrideRule.ApprovedBy;
                        line.PriceApprovedAtUtc = overrideRule.ApprovedAtUtc;
                    }
                    else
                    {
                        // A half-approved override must not silently fall back to the
                        // catalog price: its presence asserts an intentional project
                        // price decision whose authority is incomplete.
                        line.Price = null;
                        line.PriceStatus = PriceStatus.MissingPrice;
                        line.PriceBookId = "override:INCOMPLETE";
                        line.Findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.OverrideIncomplete,
                            Domain = "estimate",
                            Severity = FindingSeverity.Error,
                            Title = $"מחיר הפרויקט לסעיף '{Bidi.Ltr(item.Code)}' אינו מאושר במלואו",
                            Message = "Project override requires price, source, reason, approver and approved_at_utc.",
                            RecommendedAction = "יש להשלים מאשר וחותמת זמן, או להסיר את מחיר הפרויקט ולהריץ בנייה מחדש.",
                            AffectedRecordIds = { record.RecordId },
                        });
                    }
                }
                else if (snapshot.Prices.TryGetValue(item.Code, out var priceRecord) &&
                         priceRecord.Price is > 0)
                {
                    if (CatalogIdentity.PriceRecordMatchesSnapshot(
                            snapshot, item.Code, priceRecord))
                    {
                        line.Price = priceRecord.Price;
                        line.PriceStatus = PriceStatus.Priced;
                        line.PriceBookId = priceRecord.PriceBookId;
                        line.PriceDecisionSource = priceRecord.SourceHash;
                    }
                    else
                    {
                        line.Price = null;
                        line.PriceStatus = PriceStatus.MissingPrice;
                        line.PriceBookId = snapshot.SnapshotId;
                        line.Findings.Add(new DeliveryFinding
                        {
                            Code = EstimateFindingCodes.PriceSourceUnverified,
                            Domain = "estimate",
                            Severity = FindingSeverity.Error,
                            Title = $"מקור המחיר לסעיף '{Bidi.Ltr(item.Code)}' אינו תואם למחירון המאומת",
                            Message = "Price record id/code/source SHA must equal the immutable catalog snapshot.",
                            AffectedRecordIds = { record.RecordId },
                        });
                    }
                }
                else
                {
                    line.Price = null; // MISSING_PRICE is never zero (plan §8.9)
                    line.PriceStatus = PriceStatus.MissingPrice;
                    line.PriceBookId = snapshot.SnapshotId;
                    line.Findings.Add(new DeliveryFinding
                    {
                        Code = EstimateFindingCodes.MissingPrice,
                        Domain = "estimate",
                        Severity = FindingSeverity.ReviewRequired,
                        Title = $"חסר מחיר לסעיף '{Bidi.Ltr(item.Code)}' במהדורת המחירון {Bidi.Ltr(snapshot.SnapshotId)}",
                        AffectedRecordIds = { record.RecordId },
                    });
                }

                // --------------------------------------------------------- total
                bool hasError = record.Status == DeliveryStatus.Failed ||
                    line.Findings.Any(f => f.Severity == FindingSeverity.Error);
                bool sourceBlocked = record.Status == DeliveryStatus.Blocked;
                bool needsReview = record.Status == DeliveryStatus.ReviewRequired ||
                    line.Findings.Any(f => f.Severity == FindingSeverity.ReviewRequired);

                if (unitOk && line.Price != null && !hasError && !sourceBlocked && !needsReview)
                {
                    try
                    {
                        line.Total = EstimateBoqSemantics.RoundMoney(
                            EstimateBoqSemantics.StoredQuantity(line.BoqQuantity) * line.Price.Value);
                        line.IncludedInTotals = true;
                        line.Status = DeliveryStatus.Ready;
                    }
                    catch (OverflowException)
                    {
                        EstimateBoqSemantics.BlockMonetaryOverflow(result, new[] { line },
                            FormattableString.Invariant($"Record {line.RecordId}: quantity {line.BoqQuantity:R} × price {line.Price.Value} exceeds the decimal calculation range."));
                    }
                }
                else
                {
                    line.Total = null;
                    line.IncludedInTotals = false;
                    line.Status = hasError ? DeliveryStatus.Failed
                        : sourceBlocked ? DeliveryStatus.Blocked
                        : needsReview ? DeliveryStatus.ReviewRequired
                        : DeliveryStatus.Ready;
                }
            }

            EstimateBoqSemantics.AssignCleanTotalOrBlock(result);
            result.ExcludedLineCount = result.Lines.Count(l => !l.IncludedInTotals);
            result.Status = DeliveryStatusRules.Aggregate(result.Lines.Select(l => l.Status).ToList());
            EstimatePreflightPolicy.Apply(result, preflight);
            return result;
        }

        private static void MarkInvalidQuantity(
            EstimateResult result, EstimateLine line, double value, string stage,
            DeliveryFinding? existingFinding = null)
        {
            // Keep the raw value on the trace line, but never copy NaN/Infinity into
            // the decimal-backed BOQ/workbook path. Zero here is only a safe storage
            // sentinel on an explicitly failed and excluded line.
            line.BoqQuantity = 0;
            line.Total = null;
            line.IncludedInTotals = false;
            line.Status = DeliveryStatus.Failed;
            var rendered = double.IsNaN(value)
                ? "NaN"
                : double.IsPositiveInfinity(value) ? "+Infinity"
                : double.IsNegativeInfinity(value) ? "-Infinity"
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var finding = existingFinding ?? new DeliveryFinding
            {
                Code = EstimateFindingCodes.MeasurementFailed,
                Domain = "estimate",
                Severity = FindingSeverity.Error,
                Title = $"Non-positive or non-finite {stage} for record {line.RecordId}: {rendered}",
                RecommendedAction = "Repair the source geometry/rule and run a fresh quantity scan.",
                AffectedRecordIds = { line.RecordId },
            };
            if (line.Findings.All(f => f.FindingId != finding.FindingId))
                line.Findings.Add(finding);
            if (result.Findings.All(f => f.FindingId != finding.FindingId))
                result.Findings.Add(finding);
        }
    }
}
