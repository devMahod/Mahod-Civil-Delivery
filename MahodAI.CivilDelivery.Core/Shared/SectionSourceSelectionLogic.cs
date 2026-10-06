using System;
using System.Collections.Generic;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Pure, fail-closed selection of the two surface sources a road section needs:
    /// one explicitly identifiable existing-ground surface and one design surface
    /// tied to the selected alignment.  A surface from another alignment is never a
    /// fallback; ambiguity is returned to PLAN for engineering review.
    /// </summary>
    public static class SectionSourceSelectionLogic
    {
        public sealed record Identity(string Name, string Handle);
        public sealed record ExplicitPairResult(bool IsConfigured,
            ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair? Pair, string? Error)
        {
            public bool IsValid => IsConfigured && Pair != null && Error == null;
        }

        /// <summary>Explicit roles never fall back to name guessing after an identity failure.
        /// Fingerprint is drawing identity (not a save hash); handles and names are both required.</summary>
        public static ExplicitPairResult SelectExplicitPair(string? drawingFingerprint,
            string? alignmentName, string? alignmentHandle, IReadOnlyList<Identity> surfaces,
            IReadOnlyList<ProjectProfile.SectionsProfile.SourcesProfile.SurfacePair> pairs)
        {
            var matches = pairs.Where(p => Same(p.AlignmentName, alignmentName) ||
                Same(p.AlignmentHandle, alignmentHandle)).ToArray();
            if (matches.Length == 0) return new(false, null, null);
            ExplicitPairResult Fail(string message) => new(true, null, message);
            if (matches.Length != 1) return Fail("More than one reviewed surface pair matches this alignment.");
            var pair = matches[0];
            if (!Guid.TryParse(drawingFingerprint, out var liveGuid) || liveGuid == Guid.Empty ||
                !Guid.TryParse(pair.DrawingFingerprint, out var approvedGuid) || approvedGuid != liveGuid)
                return Fail("The reviewed surface pair belongs to another or unreadable drawing identity.");
            if (!Same(pair.AlignmentName, alignmentName) || !Same(pair.AlignmentHandle, alignmentHandle) ||
                !ValidHandle(pair.AlignmentHandle) || string.IsNullOrWhiteSpace(pair.ApprovedBy) ||
                pair.ApprovedAtUtc is not { Kind: DateTimeKind.Utc })
                return Fail("The reviewed alignment identity or approval is incomplete or changed.");
            if (!ValidHandle(pair.ExistingHandle) || !ValidHandle(pair.DesignHandle) ||
                Same(pair.ExistingName, pair.DesignName) || Same(pair.ExistingHandle, pair.DesignHandle))
                return Fail("Existing and design surfaces must be distinct complete identities.");
            bool Exact(string? name, string? handle) => !string.IsNullOrWhiteSpace(name) &&
                surfaces.Count(s => Same(s.Name, name) || Same(s.Handle, handle)) == 1 &&
                surfaces.Any(s => Same(s.Name, name) && Same(s.Handle, handle));
            if (!Exact(pair.ExistingName, pair.ExistingHandle) || !Exact(pair.DesignName, pair.DesignHandle))
                return Fail("A reviewed surface is missing, duplicated, renamed or replaced; review Setup again.");
            return new(true, pair, null);
        }

        private static bool Same(string? a, string? b) => !string.IsNullOrWhiteSpace(a) &&
            !string.IsNullOrWhiteSpace(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool ValidHandle(string? value) => !string.IsNullOrWhiteSpace(value) &&
            ulong.TryParse(value, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var handle) && handle != 0;

        public static readonly IReadOnlyList<string> DefaultExistingSurfacePatterns =
            new[] { "MK", "MK*", "*EG*", "*EXIST*", "*KAYAM*", "*מצב קיים*", "*קיים*" };

        public enum ChoiceState
        {
            Selected,
            Missing,
            Ambiguous,
        }

        public sealed record Choice(ChoiceState State, string? Name, IReadOnlyList<string> Candidates)
        {
            public bool IsSelected => State == ChoiceState.Selected && !string.IsNullOrWhiteSpace(Name);
        }

        public sealed record Selection(Choice ExistingGround, Choice Design)
        {
            public bool IsReady => ExistingGround.IsSelected && Design.IsSelected;
        }

        public static Selection Select(
            IEnumerable<string> surfaceNames,
            string? alignmentName,
            IReadOnlyList<string>? existingSurfacePatterns)
        {
            var names = surfaceNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var patterns = existingSurfacePatterns is { Count: > 0 }
                ? existingSurfacePatterns
                : DefaultExistingSurfacePatterns;

            return new Selection(
                SelectExistingGround(names, patterns),
                SelectDesign(names, alignmentName));
        }

        public static Choice SelectExistingGround(
            IReadOnlyList<string> surfaceNames,
            IReadOnlyList<string> patterns)
        {
            // A literal configured name is stronger evidence than a wildcard.  The
            // active 6422 profile deliberately contains both MK and MK*: MK must win
            // over the unrelated MK-EAST surface instead of sampling both.
            var literalPatterns = patterns
                .Where(p => !string.IsNullOrWhiteSpace(p) && p.IndexOfAny(new[] { '*', '?' }) < 0)
                .ToList();
            var exact = surfaceNames
                .Where(n => literalPatterns.Any(p => string.Equals(n, p, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (exact.Count > 0) return Choose(exact);

            var wildcard = surfaceNames
                .Where(n => patterns.Any(p => SectionProjectionLogic.Wildcard(n, p)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return Choose(wildcard);
        }

        public static Choice SelectDesign(IReadOnlyList<string> surfaceNames, string? alignmentName)
        {
            if (string.IsNullOrWhiteSpace(alignmentName))
                return new Choice(ChoiceState.Missing, null, Array.Empty<string>());

            var scoped = surfaceNames
                .Where(n => BelongsToAlignment(n, alignmentName!))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Priority tiers are evidence strength. Never fall through an ambiguous
            // stronger tier to a weaker guess.
            var final = scoped.Where(n =>
                n.IndexOf("DESIGN", StringComparison.OrdinalIgnoreCase) >= 0 &&
                n.IndexOf("FINAL", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (final.Count > 0) return Choose(final);

            var design = scoped.Where(n =>
                n.IndexOf("DESIGN", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (design.Count > 0) return Choose(design);

            // Civil corridor-generated top surfaces in the Nataly drawings use the
            // explicit <alignment>D@Top@<index> convention.  This is alignment-scoped
            // design evidence, unlike a generic name containing "top".  It remains
            // fail-closed when more than one such surface exists.
            var corridorTop = scoped.Where(n => IsCorridorDesignTop(n, alignmentName!)).ToList();
            if (corridorTop.Count > 0) return Choose(corridorTop);

            // Keep other alignment-scoped names as review evidence without guessing
            // that generic "top" / "bot" text implies the design role.
            return new Choice(ChoiceState.Missing, null, scoped);
        }

        private static bool IsCorridorDesignTop(string surfaceName, string alignmentName)
        {
            if (surfaceName.Length <= alignmentName.Length) return false;
            return surfaceName.Substring(alignmentName.Length)
                .StartsWith("D@Top@", StringComparison.OrdinalIgnoreCase);
        }

        private static Choice Choose(IReadOnlyList<string> candidates) => candidates.Count switch
        {
            0 => new Choice(ChoiceState.Missing, null, candidates),
            1 => new Choice(ChoiceState.Selected, candidates[0], candidates),
            _ => new Choice(ChoiceState.Ambiguous, null, candidates),
        };

        private static bool BelongsToAlignment(string surfaceName, string alignmentName)
        {
            if (!surfaceName.StartsWith(alignmentName, StringComparison.OrdinalIgnoreCase)) return false;
            if (surfaceName.Length == alignmentName.Length) return true;

            // Avoid treating axis 7000 as a surface of axis 700. Office names such as
            // 700D@Top@01 intentionally use a letter immediately after the number.
            return !char.IsDigit(surfaceName[alignmentName.Length]);
        }

    }
}
