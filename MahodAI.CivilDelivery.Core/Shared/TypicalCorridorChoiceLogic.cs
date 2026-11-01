using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Which corridor the firm's section program cuts for one alignment (typical sections, live 2026-10-26: the
    /// assistant had built Road34's corridor twice, and the first copy had no regions at all). The choice is made from
    /// evidence, in this order: the engineer's choice in this session, the profile's approved name, the only corridor
    /// whose regions cover every picked station, the only one of those that is up to date. Two corridors that both
    /// cover the stations are never decided here: the engineer is asked. A corridor's surfaces are its own corridor
    /// surfaces, not a TIN whose name happens to contain the corridor's name ("Road34_Corridor" is inside
    /// "Road34_Corridor (2) - Top").
    /// </summary>
    public static class TypicalCorridorChoiceLogic
    {
        /// <summary>A station this close outside a region still belongs to it (the firm program's own rounding).</summary>
        public const double RegionToleranceM = 0.01;

        public sealed record Candidate(
            string Name,
            IReadOnlyList<(double Start, double End)> Regions,
            bool OutOfDate,
            IReadOnlyList<string> SurfaceNames);

        public static class Reason
        {
            public const string NoCorridor = "no-corridor";
            public const string SessionChoice = "session-choice";
            public const string ProfileChoice = "profile-choice";
            public const string OnlyCorridor = "only-corridor";
            public const string CoversStations = "covers-stations";
            public const string UpToDate = "up-to-date";
            public const string NoneCovers = "none-covers";
            public const string Ambiguous = "ambiguous";
        }

        /// <param name="Chosen">The corridor to use; null when none fits or the engineer must choose.</param>
        /// <param name="AskAmong">Non-empty only when the engineer must choose among these.</param>
        public sealed record Choice(string? Chosen, IReadOnlyList<string> AskAmong, string Reason);

        public static bool Covers(Candidate candidate, double station) =>
            candidate.Regions.Any(region =>
                station >= Math.Min(region.Start, region.End) - RegionToleranceM &&
                station <= Math.Max(region.Start, region.End) + RegionToleranceM);

        public static Choice Choose(
            IReadOnlyList<Candidate> candidates, IReadOnlyList<double> stations,
            string? profileChoice, string? sessionChoice)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            stations ??= Array.Empty<double>();
            if (candidates.Count == 0) return new Choice(null, Array.Empty<string>(), Reason.NoCorridor);

            var session = Named(candidates, sessionChoice);
            if (session != null) return new Choice(session.Name, Array.Empty<string>(), Reason.SessionChoice);
            var profile = Named(candidates, profileChoice);
            if (profile != null) return new Choice(profile.Name, Array.Empty<string>(), Reason.ProfileChoice);
            if (candidates.Count == 1) return new Choice(candidates[0].Name, Array.Empty<string>(), Reason.OnlyCorridor);

            var covering = candidates.Where(c => stations.All(s => Covers(c, s))).ToList();
            if (covering.Count == 1) return new Choice(covering[0].Name, Array.Empty<string>(), Reason.CoversStations);
            if (covering.Count == 0) return new Choice(null, Array.Empty<string>(), Reason.NoneCovers);

            var fresh = covering.Where(c => !c.OutOfDate).ToList();
            if (fresh.Count == 1) return new Choice(fresh[0].Name, Array.Empty<string>(), Reason.UpToDate);
            return new Choice(null, covering.Select(c => c.Name).ToList(), Reason.Ambiguous);
        }

        /// <summary>
        /// The corridor's own top and bottom surfaces: an approved profile name when that TIN exists, else the
        /// corridor surface named Top (or the corridor's only surface), and for the bottom the one named Bot or Datum,
        /// else the top. Null top when the corridor has no usable surface.
        /// </summary>
        public static (string? Top, string? Bottom) Surfaces(
            IReadOnlyList<string> corridorSurfaces, IReadOnlyList<string> tins,
            string? profileTop, string? profileBottom)
        {
            string? Existing(string? name) => string.IsNullOrWhiteSpace(name)
                ? null
                : tins.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
            var own = corridorSurfaces
                .Where(name => tins.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            var top = Existing(profileTop)
                ?? own.FirstOrDefault(n => n.IndexOf("top", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? (own.Count == 1 ? own[0] : null);
            var bottom = Existing(profileBottom)
                ?? own.FirstOrDefault(n => n.IndexOf("bot", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? own.FirstOrDefault(n => n.IndexOf("datum", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? top;
            return (top, bottom);
        }

        /// <param name="Chosen">The existing-ground TIN; null when the engineer must choose or none exists.</param>
        /// <param name="AskAmong">Non-empty only when the engineer must choose among these.</param>
        /// <param name="Problem">Why there is no candidate at all (Hebrew); null otherwise.</param>
        public sealed record SurfaceChoice(string? Chosen, IReadOnlyList<string> AskAmong, string? Problem);

        /// <summary>
        /// The existing ground the firm's sections are cut against, decided once per drawing: the engineer's choice in
        /// this session, else the profile's approved name, else the one TIN that is no corridor's surface (matching the
        /// existing-surface patterns first). Several candidates are the engineer's call, asked in the window (live
        /// 2026-10-28, 6426: MK-2025-11-18 and MK-2025-11-10) — never a profile edit, never a guess.
        /// </summary>
        /// <param name="matches">Wildcard test of a name against a pattern (the host's, as PLAN uses).</param>
        public static SurfaceChoice ExistingGround(
            IReadOnlyList<string> tins, IReadOnlyCollection<string> corridorSurfaces, IReadOnlyCollection<string> corridorNames,
            IReadOnlyList<string> patterns, Func<string, string, bool> matches, string? profileChoice, string? sessionChoice)
        {
            if (tins == null) throw new ArgumentNullException(nameof(tins));
            if (matches == null) throw new ArgumentNullException(nameof(matches));
            string? Existing(string? name) => string.IsNullOrWhiteSpace(name)
                ? null
                : tins.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));

            if (Existing(sessionChoice) is { } session) return new SurfaceChoice(session, Array.Empty<string>(), null);
            if (Existing(profileChoice) is { } profile) return new SurfaceChoice(profile, Array.Empty<string>(), null);

            var nonCorridor = tins.Where(name =>
                    !corridorSurfaces.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                    !corridorNames.Any(corridor => name.IndexOf(corridor, StringComparison.OrdinalIgnoreCase) >= 0) &&
                    name.IndexOf("top", StringComparison.OrdinalIgnoreCase) < 0 &&
                    name.IndexOf("bot", StringComparison.OrdinalIgnoreCase) < 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var matched = nonCorridor.Where(name => patterns.Any(pattern => matches(name, pattern))).ToList();
            var candidates = matched.Count > 0 ? matched : nonCorridor;
            if (candidates.Count == 1) return new SurfaceChoice(candidates[0], Array.Empty<string>(), null);
            if (candidates.Count > 1) return new SurfaceChoice(null, candidates, null);
            return new SurfaceChoice(null, Array.Empty<string>(),
                "לא נמצא בשרטוט משטח קרקע קיימת (TIN) — יש לטעון אותו ואז לנסות שוב.");
        }

        private static Candidate? Named(IReadOnlyList<Candidate> candidates, string? name) =>
            string.IsNullOrWhiteSpace(name)
                ? null
                : candidates.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }
}
