using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Autodesk-free traffic-direction evidence rules.
    ///
    /// A vehicle view must never be chosen from the sign of its section offset.  The
    /// only automatic direction source accepted here is a known road-marking arrow
    /// close to the lane being decorated.  Missing, off-axis, or competing evidence
    /// deliberately returns <see cref="ResolutionState.Unknown"/> or
    /// <see cref="ResolutionState.Ambiguous"/> so the caller can stop and request an
    /// explicit engineering decision.
    /// </summary>
    public static class TrafficDirectionEvidenceLogic
    {
        public const double DefaultMaxSearchDistanceM = 40.0;
        public const double DefaultNearestCompetitionBandM = 2.0;
        public const double DefaultHeadingAgreementDegrees = 20.0;
        public const double DefaultAxisToleranceDegrees = 40.0;

        public enum SourceClass
        {
            Unapproved,
            ApprovedTrafficArrow,
            ExcludedBikeArrow
        }

        public enum ResolutionState
        {
            Unknown,
            Resolved,
            Ambiguous
        }

        public enum RelativeFlow
        {
            Unknown,
            AlongAlignment,
            AgainstAlignment
        }

        /// <param name="HeadingRadians">
        /// WCS heading of the arrow tip direction, counter-clockwise from WCS +X.
        /// For the audited Mahod/TR blocks this is the transformed local +Y axis
        /// (equivalent to insertion Rotation + 90 degrees when there is no parent
        /// transform or mirroring).
        /// </param>
        public sealed record ArrowEvidence(
            double X,
            double Y,
            double HeadingRadians,
            string Layer,
            string BlockName,
            string? Source,
            string HandlePath);

        public sealed record ResolveOptions(
            double MaxSearchDistanceM = DefaultMaxSearchDistanceM,
            double NearestCompetitionBandM = DefaultNearestCompetitionBandM,
            double HeadingAgreementDegrees = DefaultHeadingAgreementDegrees,
            double AxisToleranceDegrees = DefaultAxisToleranceDegrees);

        public sealed record DirectionResolution(
            ResolutionState State,
            double? HeadingRadians,
            ArrowEvidence? Primary,
            IReadOnlyList<ArrowEvidence> Contenders,
            int ApprovedWithinRadius,
            int ExcludedBikeCount,
            int UnapprovedCount,
            string Reason)
        {
            public bool IsResolved => State == ResolutionState.Resolved && HeadingRadians.HasValue;
        }

        private static readonly string[] ApprovedLayerPrefixes =
        {
            "BL-TR-ARRW",
            "BL-TR-MARK-2",
            "TR-MARK-ARW-BL",
            "TR-MARK-ARW-YLW"
        };

        private static readonly string[] ExcludedTokens =
        {
            "BIKE", "BICYCLE", "CYCLE", "OFAN", "OFANAIM", "אופניים", "אופני"
        };

        private static readonly string[] ExcludedExactLayers = { "HA-BIKE" };
        private static readonly string[] ExcludedExactBlocks = { "ARROW-D", "BL-ARW-W-Y" };

        /// <summary>
        /// Classifies only audited, deliberately narrow traffic-arrow conventions.
        /// Bike evidence has precedence even when it sits on a normally approved
        /// traffic-arrow layer (the 6422 source contains exactly that situation).
        /// </summary>
        public static SourceClass ClassifySource(string? layer, string? blockName)
        {
            var normalizedLayer = NormalizeName(layer);
            var normalizedBlock = NormalizeName(blockName);
            var localLayer = LastXrefSegment(normalizedLayer);

            if (IsBikeArrow(localLayer, normalizedBlock))
                return SourceClass.ExcludedBikeArrow;

            if (ApprovedLayerPrefixes.Any(prefix =>
                    localLayer.Equals(prefix, StringComparison.Ordinal) ||
                    localLayer.StartsWith(prefix + "-", StringComparison.Ordinal)))
                return SourceClass.ApprovedTrafficArrow;

            // Named TR arrow definitions are also accepted when nested on layer 0.
            // Generic ARROW/ARRW names are intentionally not accepted: north arrows,
            // leaders, and utility-flow symbols are common false positives.
            if (normalizedBlock.StartsWith("TR-ARW", StringComparison.Ordinal) ||
                normalizedBlock.StartsWith("TR-ARRW", StringComparison.Ordinal) ||
                normalizedBlock.StartsWith("TR-ARROW", StringComparison.Ordinal))
                return SourceClass.ApprovedTrafficArrow;

            return SourceClass.Unapproved;
        }

        /// <summary>
        /// Resolves the nearest evidence at a lane/sample-line target.  Only arrows
        /// parallel or anti-parallel to the local alignment tangent participate.  All
        /// arrows in the nearest competition band must agree in the directed sense;
        /// otherwise the result is Ambiguous rather than a guess.
        /// </summary>
        public static DirectionResolution ResolveNearest(
            double targetX,
            double targetY,
            double alignmentHeadingRadians,
            IEnumerable<ArrowEvidence>? evidence,
            ResolveOptions? options = null) =>
            ResolveNearestForClass(
                targetX,
                targetY,
                alignmentHeadingRadians,
                evidence,
                SourceClass.ApprovedTrafficArrow,
                "no-approved-arrow-in-range",
                "nearest-arrows-conflict",
                "nearest-arrow-heading-cancels",
                "nearest-approved-arrows-agree",
                options);

        /// <summary>
        /// Separate bicycle-strip resolver.  It accepts only the bike arrows that
        /// <see cref="ResolveNearest"/> deliberately excludes from motor-traffic
        /// evidence, so a bicycle symbol can never orient a car/bus and vice versa.
        /// </summary>
        public static DirectionResolution ResolveNearestBike(
            double targetX,
            double targetY,
            double alignmentHeadingRadians,
            IEnumerable<ArrowEvidence>? evidence,
            ResolveOptions? options = null) =>
            ResolveNearestForClass(
                targetX,
                targetY,
                alignmentHeadingRadians,
                evidence,
                SourceClass.ExcludedBikeArrow,
                "no-approved-bike-arrow-in-range",
                "nearest-bike-arrows-conflict",
                "nearest-bike-arrow-heading-cancels",
                "nearest-approved-bike-arrows-agree",
                options);

        private static DirectionResolution ResolveNearestForClass(
            double targetX,
            double targetY,
            double alignmentHeadingRadians,
            IEnumerable<ArrowEvidence>? evidence,
            SourceClass requiredClass,
            string noEvidenceReason,
            string conflictReason,
            string cancellationReason,
            string resolvedReason,
            ResolveOptions? options)
        {
            options ??= new ResolveOptions();
            if (!Finite(targetX) || !Finite(targetY) || !Finite(alignmentHeadingRadians) ||
                !ValidOptions(options))
            {
                return Unknown("invalid-query", 0, 0);
            }

            var all = evidence?.ToList() ?? new List<ArrowEvidence>();
            int excludedBike = 0;
            int unapproved = 0;
            var approved = new List<(ArrowEvidence Evidence, double Distance)>();
            var axisTolerance = DegreesToRadians(options.AxisToleranceDegrees);

            foreach (var item in all)
            {
                var sourceClass = ClassifySource(item.Layer, item.BlockName);
                if (sourceClass == SourceClass.ExcludedBikeArrow)
                {
                    excludedBike++;
                    if (requiredClass != SourceClass.ExcludedBikeArrow)
                        continue;
                }
                if (sourceClass != requiredClass ||
                    !Finite(item.X) || !Finite(item.Y) || !Finite(item.HeadingRadians))
                {
                    unapproved++;
                    continue;
                }

                var dx = item.X - targetX;
                var dy = item.Y - targetY;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                if (!Finite(distance) || distance > options.MaxSearchDistanceM)
                    continue;

                // Alignment is an undirected axis for this filter.  An arrow may be
                // travelling either with or against it, but a side-road arrow is not
                // evidence for this section lane.
                if (UndirectedAngleDifference(item.HeadingRadians, alignmentHeadingRadians) >
                    axisTolerance)
                    continue;

                approved.Add((item, distance));
            }

            if (approved.Count == 0)
                return Unknown(noEvidenceReason, excludedBike, unapproved);

            approved.Sort((a, b) =>
            {
                var byDistance = a.Distance.CompareTo(b.Distance);
                return byDistance != 0
                    ? byDistance
                    : string.CompareOrdinal(StableKey(a.Evidence), StableKey(b.Evidence));
            });

            var nearestDistance = approved[0].Distance;
            var contenders = approved
                .Where(x => x.Distance <= nearestDistance + options.NearestCompetitionBandM + 1e-9)
                .Select(x => x.Evidence)
                .ToList();

            var agreement = DegreesToRadians(options.HeadingAgreementDegrees);
            for (int i = 0; i < contenders.Count; i++)
            {
                for (int j = i + 1; j < contenders.Count; j++)
                {
                    if (DirectedAngleDifference(
                            contenders[i].HeadingRadians,
                            contenders[j].HeadingRadians) > agreement)
                    {
                        return new DirectionResolution(
                            ResolutionState.Ambiguous,
                            null,
                            approved[0].Evidence,
                            contenders,
                            approved.Count,
                            excludedBike,
                            unapproved,
                            conflictReason);
                    }
                }
            }

            var heading = CircularMean(contenders.Select(x => x.HeadingRadians));
            if (!heading.HasValue)
            {
                return new DirectionResolution(
                    ResolutionState.Ambiguous,
                    null,
                    approved[0].Evidence,
                    contenders,
                    approved.Count,
                    excludedBike,
                    unapproved,
                    cancellationReason);
            }

            return new DirectionResolution(
                ResolutionState.Resolved,
                heading,
                approved[0].Evidence,
                contenders,
                approved.Count,
                excludedBike,
                unapproved,
                resolvedReason);
        }

        public static RelativeFlow RelativeToAlignment(
            double arrowHeadingRadians,
            double alignmentHeadingRadians,
            double axisToleranceDegrees = DefaultAxisToleranceDegrees)
        {
            if (!Finite(arrowHeadingRadians) || !Finite(alignmentHeadingRadians) ||
                !Finite(axisToleranceDegrees) || axisToleranceDegrees < 0 || axisToleranceDegrees >= 90)
                return RelativeFlow.Unknown;

            var delta = DirectedAngleDifference(arrowHeadingRadians, alignmentHeadingRadians);
            var tolerance = DegreesToRadians(axisToleranceDegrees);
            if (delta <= tolerance) return RelativeFlow.AlongAlignment;
            if (Math.Abs(Math.PI - delta) <= tolerance) return RelativeFlow.AgainstAlignment;
            return RelativeFlow.Unknown;
        }

        public static double NormalizeHeading(double radians)
        {
            if (!Finite(radians)) return double.NaN;
            var normalized = radians % (Math.PI * 2.0);
            return normalized < 0 ? normalized + Math.PI * 2.0 : normalized;
        }

        private static bool IsBikeArrow(string normalizedLayer, string normalizedBlock)
        {
            if (ExcludedExactLayers.Contains(normalizedLayer, StringComparer.Ordinal) ||
                ExcludedExactBlocks.Contains(normalizedBlock, StringComparer.Ordinal) ||
                normalizedBlock.StartsWith("ARROW-D", StringComparison.Ordinal))
                return true;

            return ExcludedTokens.Any(token =>
                ContainsToken(normalizedLayer, token) || ContainsToken(normalizedBlock, token));
        }

        private static bool ContainsToken(string value, string token) =>
            value.Equals(token, StringComparison.Ordinal) ||
            value.StartsWith(token + "-", StringComparison.Ordinal) ||
            value.EndsWith("-" + token, StringComparison.Ordinal) ||
            value.Contains("-" + token + "-", StringComparison.Ordinal);

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = value.Trim().ToUpperInvariant()
                .Select(c => char.IsLetterOrDigit(c) || c is '|' or '$' ? c : '-')
                .ToArray();
            var normalized = new string(chars);
            while (normalized.Contains("--", StringComparison.Ordinal))
                normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
            return normalized.Trim('-');
        }

        private static string LastXrefSegment(string layer)
        {
            // Attached XREF layers use '|'; bound-XREF layers normally use '$0$'.
            var separator = Math.Max(layer.LastIndexOf('|'), layer.LastIndexOf('$'));
            return separator >= 0 && separator + 1 < layer.Length
                ? layer[(separator + 1)..]
                : layer;
        }

        private static DirectionResolution Unknown(string reason, int bikes, int unapproved) =>
            new(ResolutionState.Unknown, null, null, Array.Empty<ArrowEvidence>(),
                0, bikes, unapproved, reason);

        private static bool ValidOptions(ResolveOptions options) =>
            Finite(options.MaxSearchDistanceM) && options.MaxSearchDistanceM > 0 &&
            Finite(options.NearestCompetitionBandM) && options.NearestCompetitionBandM >= 0 &&
            Finite(options.HeadingAgreementDegrees) && options.HeadingAgreementDegrees >= 0 &&
            options.HeadingAgreementDegrees < 90 &&
            Finite(options.AxisToleranceDegrees) && options.AxisToleranceDegrees >= 0 &&
            options.AxisToleranceDegrees < 90;

        private static bool Finite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);

        private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

        private static double DirectedAngleDifference(double a, double b)
        {
            var d = Math.Abs(NormalizeHeading(a) - NormalizeHeading(b));
            return Math.Min(d, Math.PI * 2.0 - d);
        }

        private static double UndirectedAngleDifference(double a, double b)
        {
            var directed = DirectedAngleDifference(a, b);
            return Math.Min(directed, Math.Abs(Math.PI - directed));
        }

        private static double? CircularMean(IEnumerable<double> headings)
        {
            double x = 0;
            double y = 0;
            int count = 0;
            foreach (var heading in headings)
            {
                x += Math.Cos(heading);
                y += Math.Sin(heading);
                count++;
            }
            if (count == 0 || Math.Sqrt(x * x + y * y) < 1e-9) return null;
            return NormalizeHeading(Math.Atan2(y, x));
        }

        private static string StableKey(ArrowEvidence evidence) =>
            string.Join("|", evidence.Source ?? string.Empty, evidence.HandlePath,
                evidence.Layer, evidence.BlockName);
    }
}
