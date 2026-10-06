using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Autodesk-free resolver for SEC-02.  A manually-authored Civil SectionView may
    /// be reused only when alignment, station and the two CL endpoints identify one
    /// and only one foreign SampleLine/SectionView pair.  Anything uncertain is a
    /// review decision, never a best-effort adoption.
    /// </summary>
    public static class ManualSectionReuseResolver
    {
        public const double DefaultStationToleranceM = 0.05;
        public const double DefaultEndpointToleranceM = 0.05;

        public readonly record struct Point(double X, double Y);

        public sealed record Candidate(
            string AlignmentName,
            double Station,
            bool StationReadable,
            IReadOnlyList<Point> Vertices,
            bool GeometryReadable,
            string SampleLineGroupHandle,
            string SampleLineHandle,
            string SectionViewHandle,
            string? SampleLineName = null,
            string? SectionViewName = null,
            string? SectionViewStyleName = null,
            IReadOnlyList<string>? BandStyleNames = null,
            bool PresentationReadable = false,
            bool SingleDatumPresentationCompatible = false,
            string? PresentationEvidence = null);

        public enum DecisionKind
        {
            None,
            Reuse,
            ReviewRequired,
        }

        public enum DecisionReason
        {
            NoCandidate,
            UniqueMatch,
            Ambiguous,
            StationUnreadable,
            GeometryUnreadable,
            GeometryMismatch,
            PresentationUnreadable,
            PresentationIncompatible,
            InvalidInput,
        }

        public sealed record Decision(
            DecisionKind Kind,
            DecisionReason Reason,
            Candidate? Selected,
            IReadOnlyList<Candidate> RelevantCandidates)
        {
            public bool CanReuse => Kind == DecisionKind.Reuse && Selected != null;
        }

        public static Decision Resolve(
            string? alignmentName,
            double station,
            IReadOnlyList<Point> expectedClEndpoints,
            IEnumerable<Candidate> candidates,
            double stationToleranceM = DefaultStationToleranceM,
            double endpointToleranceM = DefaultEndpointToleranceM)
        {
            if (string.IsNullOrWhiteSpace(alignmentName) ||
                !double.IsFinite(station) ||
                expectedClEndpoints == null || expectedClEndpoints.Count != 2 ||
                expectedClEndpoints.Any(p => !Finite(p)) ||
                !double.IsFinite(stationToleranceM) || stationToleranceM < 0 ||
                !double.IsFinite(endpointToleranceM) || endpointToleranceM < 0)
            {
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.InvalidInput,
                    null,
                    Array.Empty<Candidate>());
            }

            var sameAlignment = (candidates ?? Enumerable.Empty<Candidate>())
                .Where(c => string.Equals(
                    c.AlignmentName, alignmentName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.StationReadable ? c.Station : double.MaxValue)
                .ThenBy(c => c.SampleLineHandle, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.SectionViewHandle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // An unreadable station on the selected alignment cannot safely be proved
            // unrelated to this CL.  Fail closed instead of creating a possible twin.
            var unreadableStation = sameAlignment.Where(c =>
                !c.StationReadable || !double.IsFinite(c.Station)).ToList();
            if (unreadableStation.Count > 0)
            {
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.StationUnreadable,
                    null,
                    unreadableStation);
            }

            var atStation = sameAlignment
                .Where(c => Math.Abs(c.Station - station) <= stationToleranceM)
                .ToList();

            if (atStation.Count == 0)
                return new Decision(
                    DecisionKind.None,
                    DecisionReason.NoCandidate,
                    null,
                    Array.Empty<Candidate>());

            // Strictly one candidate at this station is required.  We deliberately do
            // not choose the sole geometry match from a larger set: two views at one
            // station are an engineering ambiguity even if one looks closer.
            if (atStation.Count != 1)
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.Ambiguous,
                    null,
                    atStation);

            var candidate = atStation[0];
            if (!candidate.GeometryReadable || candidate.Vertices == null ||
                candidate.Vertices.Count < 2 || candidate.Vertices.Any(p => !Finite(p)))
            {
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.GeometryUnreadable,
                    null,
                    atStation);
            }

            // Civil commonly inserts the alignment crossing as a middle vertex.  The
            // first and last vertices are therefore the governing swath endpoints; the
            // storage direction is allowed to be reversed.
            var first = candidate.Vertices[0];
            var last = candidate.Vertices[candidate.Vertices.Count - 1];
            var a = expectedClEndpoints[0];
            var b = expectedClEndpoints[1];
            var geometryMatches =
                (Near(first, a, endpointToleranceM) && Near(last, b, endpointToleranceM)) ||
                (Near(first, b, endpointToleranceM) && Near(last, a, endpointToleranceM));

            if (!geometryMatches)
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.GeometryMismatch,
                    null,
                    atStation);

            // Reuse is optional, but Natalie's one-datum/no-repeated-elevations
            // presentation contract is mandatory.  An unreadable foreign view may
            // conceal a conflicting presentation and therefore blocks for review.
            // A readable but incompatible view stays foreign and untouched; PLAN is
            // allowed to create a separate Mahod-managed clean view for this CL.
            if (!candidate.PresentationReadable)
                return new Decision(
                    DecisionKind.ReviewRequired,
                    DecisionReason.PresentationUnreadable,
                    null,
                    atStation);
            if (!candidate.SingleDatumPresentationCompatible)
                return new Decision(
                    DecisionKind.None,
                    DecisionReason.PresentationIncompatible,
                    null,
                    atStation);

            return new Decision(
                DecisionKind.Reuse,
                DecisionReason.UniqueMatch,
                candidate,
                atStation);
        }

        private static bool Finite(Point p) =>
            double.IsFinite(p.X) && double.IsFinite(p.Y);

        private static bool Near(Point a, Point b, double tolerance)
        {
            var dx = a.X - b.X;
            var dy = a.Y - b.Y;
            return dx * dx + dy * dy <= tolerance * tolerance;
        }
    }
}
