using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// The full contracts of the drawing resources every annotation depends on.
    /// A resource that "exists" is not enough: a nearly transparent layer, a text
    /// style with a 0.01 width factor, or a linetype that silently fell back to
    /// Continuous all render the deliverable unreadable while a name-only check
    /// stays green (Codex review of 1.2.26). APPLY normalizes/validates through
    /// these, VERIFY re-reads live values through the same functions.
    /// </summary>
    public static class SectionAnnotationResourceContracts
    {
        // Versioned names isolate selected-record migration from resources still
        // referenced by omitted legacy sections. Selected APPLY may create them,
        // but never has to rewrite the old shared definitions.
        public const string AnnotationLayerName = "MHD-SECT-ANNO-V6";
        public const string LegacyAnnotationLayerName = "MHD-SECT-ANNO";
        public const string AnnotationTextStyleName = "MHD-ANNO-V6";
        public const string AnnotationTypeface = "Arial";
        public const short AnnotationLayerColorIndex = 7;
        public const byte OpaqueAlpha = 255;
        public const string DashedLinetypeName = "MHD-DASHED2";
        public const string CenterLinetypeName = "MHD-CENTER";

        public static bool IsKnownAnnotationLayer(string? name) =>
            string.Equals(name, AnnotationLayerName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, LegacyAnnotationLayerName, StringComparison.OrdinalIgnoreCase);

        public sealed record LinetypeSpec(
            string Name, string Description, double PatternLength,
            IReadOnlyList<double> DashLengths);

        public sealed record LinetypeElementState(
            double DashLength, int ShapeNumber, bool HasShapeStyle, string? Text,
            double OffsetX, double OffsetY, double Scale, double Rotation,
            bool IsUcsOriented, bool IsUpright);

        public sealed record LinetypeState(
            bool Exists, bool Dependent, string? Description, bool IsScaledToFit,
            string? Annotative, double PatternLength,
            IReadOnlyList<LinetypeElementState> Elements);

        // Protected names avoid trusting an arbitrary office/user definition that only
        // happens to be called DASHED2 or CENTER.  These simple patterns contain no
        // shapes/text and are identical on every Civil installation and locale.
        public static readonly LinetypeSpec DashedLinetype = new(
            DashedLinetypeName,
            "Mahod Civil Delivery deterministic dashed annotation linetype v1",
            0.75,
            new[] { 0.50, -0.25 });

        public static readonly LinetypeSpec CenterLinetype = new(
            CenterLinetypeName,
            "Mahod Civil Delivery deterministic center annotation linetype v1",
            2.00,
            new[] { 1.25, -0.25, 0.25, -0.25 });

        public static readonly IReadOnlyList<LinetypeSpec> RequiredLinetypeSpecs =
            new[] { DashedLinetype, CenterLinetype };

        public static readonly IReadOnlyList<string> RequiredLinetypes =
            RequiredLinetypeSpecs.Select(spec => spec.Name).ToArray();

        public sealed record LayerState(
            bool Exists, bool Dependent, bool IsOff, bool IsFrozen, bool IsPlottable, bool IsLocked,
            bool IsHidden, bool ViewportVisibilityDefault, bool HasViewportOverrides,
            bool ViewportScanComplete, int FrozenPaperViewportCount,
            short ColorIndex, bool TransparencyIsByAlpha, byte TransparencyAlpha,
            string? LinetypeName, string? LineWeight, string? Annotative);

        public sealed record TextStyleState(
            bool Exists, string? Typeface, bool IsShapeFile, string? BigFontFile,
            double XScale, double ObliquingAngleRad, bool IsVertical, byte FlagBits,
            double TextSize, bool Bold, bool Italic, int CharacterSet, int PitchAndFamily,
            string? Annotative, string? PaperOrientation);

        /// <summary>Empty list = compliant; otherwise every violated clause, in order.</summary>
        public static IReadOnlyList<string> ValidateLayer(LayerState s)
        {
            var errors = new List<string>();
            if (!s.Exists) { errors.Add("missing"); return errors; }
            if (s.Dependent) errors.Add("xref-dependent");
            if (s.IsOff) errors.Add("off");
            if (s.IsFrozen) errors.Add("frozen");
            if (!s.IsPlottable) errors.Add("not-plottable");
            if (s.IsLocked) errors.Add("locked");
            if (s.IsHidden) errors.Add("hidden");
            if (s.ViewportVisibilityDefault) errors.Add("new-viewport-frozen");
            if (s.HasViewportOverrides) errors.Add("viewport-overrides");
            if (!s.ViewportScanComplete) errors.Add("viewport-scan-incomplete");
            if (s.FrozenPaperViewportCount != 0)
                errors.Add($"frozen-paper-viewports={s.FrozenPaperViewportCount}");
            if (s.ColorIndex != AnnotationLayerColorIndex)
                errors.Add($"color={s.ColorIndex} (expected {AnnotationLayerColorIndex})");
            if (!s.TransparencyIsByAlpha)
                errors.Add("transparency-mode-not-alpha");
            if (s.TransparencyAlpha != OpaqueAlpha)
                errors.Add($"transparency-alpha={s.TransparencyAlpha} (expected opaque {OpaqueAlpha})");
            if (!string.Equals(s.LinetypeName, "Continuous", StringComparison.OrdinalIgnoreCase))
                errors.Add($"linetype={s.LinetypeName ?? "(none)"} (expected Continuous)");
            if (!string.Equals(s.LineWeight, "ByLineWeightDefault", StringComparison.Ordinal))
                errors.Add($"lineweight={s.LineWeight ?? "(none)"} (expected ByLineWeightDefault)");
            if (string.Equals(s.Annotative, "True", StringComparison.OrdinalIgnoreCase))
                errors.Add("annotative");
            return errors;
        }

        public static IReadOnlyList<string> ValidateTextStyle(TextStyleState s)
        {
            var errors = new List<string>();
            if (!s.Exists) { errors.Add("missing"); return errors; }
            if (!string.Equals(s.Typeface, AnnotationTypeface, StringComparison.OrdinalIgnoreCase))
                errors.Add($"typeface={s.Typeface ?? "(none)"} (expected {AnnotationTypeface})");
            if (s.IsShapeFile) errors.Add("shape-file");
            if (!string.IsNullOrWhiteSpace(s.BigFontFile)) errors.Add($"bigfont={s.BigFontFile}");
            if (!double.IsFinite(s.XScale) || Math.Abs(s.XScale - 1.0) > 1e-9)
                errors.Add($"xscale={s.XScale} (expected 1)");
            if (!double.IsFinite(s.ObliquingAngleRad) || Math.Abs(s.ObliquingAngleRad) > 1e-9)
                errors.Add($"oblique={s.ObliquingAngleRad} (expected 0)");
            if (s.IsVertical) errors.Add("vertical");
            if (s.FlagBits != 0) errors.Add($"flags={s.FlagBits} (backwards/upside-down; expected 0)");
            if (!double.IsFinite(s.TextSize) || Math.Abs(s.TextSize) > 1e-9)
                errors.Add($"fixed-height={s.TextSize} (expected 0)");
            if (s.Bold) errors.Add("bold");
            if (s.Italic) errors.Add("italic");
            if (s.CharacterSet != 0) errors.Add($"charset={s.CharacterSet} (expected 0)");
            if (s.PitchAndFamily != 0)
                errors.Add($"pitch-family={s.PitchAndFamily} (expected 0)");
            if (string.Equals(s.Annotative, "True", StringComparison.OrdinalIgnoreCase))
                errors.Add("annotative");
            if (string.Equals(s.PaperOrientation, "True", StringComparison.OrdinalIgnoreCase))
                errors.Add("paper-orientation");
            return errors;
        }

        public static IReadOnlyList<string> ValidateLinetype(LinetypeSpec spec, LinetypeState state)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(state);
            var errors = new List<string>();
            if (!state.Exists) { errors.Add("missing"); return errors; }
            if (state.Dependent) errors.Add("xref-dependent");
            if (!string.Equals(state.Description, spec.Description, StringComparison.Ordinal))
                errors.Add("description/provenance");
            if (state.IsScaledToFit) errors.Add("scaled-to-fit");
            if (string.Equals(state.Annotative, "True", StringComparison.OrdinalIgnoreCase))
                errors.Add("annotative");
            if (!double.IsFinite(state.PatternLength) ||
                Math.Abs(state.PatternLength - spec.PatternLength) > 1e-9)
                errors.Add($"pattern-length={state.PatternLength:R} (expected {spec.PatternLength:R})");
            if (state.Elements.Count != spec.DashLengths.Count)
            {
                errors.Add($"dash-count={state.Elements.Count} (expected {spec.DashLengths.Count})");
                return errors;
            }

            for (var i = 0; i < state.Elements.Count; i++)
            {
                var element = state.Elements[i];
                var expected = spec.DashLengths[i];
                if (!double.IsFinite(element.DashLength) ||
                    Math.Abs(element.DashLength - expected) > 1e-9)
                    errors.Add($"dash[{i}]={element.DashLength:R} (expected {expected:R})");
                // Only an embedded shape number or a text string changes what a
                // dash draws. The style pointer, offset, scale, rotation and
                // orientation flags exist for that embedded element alone and AutoCAD
                // rewrites them on save/reload: the MHD-DASHED2 record created live on
                // 07/09 11:21 came back after the drawing was reopened with a style
                // pointer on its gap element, and every selected APPLY on the reopened
                // drawing was refused as a shared-resource drift (07/09 15:41). A plain
                // dash is proven by its length alone; a shape or text still fails.
                if (IsEmbeddedShapeOrText(element))
                    errors.Add($"dash[{i}]-contains-shape-or-text (shape={element.ShapeNumber}, " +
                               $"text={DescribeText(element.Text)}, style={element.HasShapeStyle})");
            }
            return errors;
        }

        /// <summary>
        /// A dash element draws something other than a plain dash/gap only when it
        /// carries a shape number or a text string; every other element field is
        /// decoration for that embedded element and is not stable across a DWG
        /// save/reload round trip.
        /// </summary>
        public static bool IsEmbeddedShapeOrText(LinetypeElementState element)
        {
            ArgumentNullException.ThrowIfNull(element);
            // A shape or a text string is drawn only through a shape/text style. A
            // record persisted with the TEXT element flag stores text-buffer offsets
            // in the shape-number slot: live 07/09 16:55 the gap element of
            // MHD-DASHED2 read back as shape=1, text='', style=null — it draws a plain
            // gap and nothing else. Without a style nothing embedded can render.
            return element.HasShapeStyle &&
                   (element.ShapeNumber != 0 || !string.IsNullOrEmpty(element.Text));
        }

        /// <summary>
        /// The exact Mahod description marker on a non-XREF record is this tool's own
        /// provenance: such a record may be normalized in any scope (like the annotation
        /// registry), an office record that merely shares the name may not.
        /// </summary>
        public static bool IsToolOwnedLinetype(LinetypeSpec spec, LinetypeState state)
        {
            ArgumentNullException.ThrowIfNull(spec);
            ArgumentNullException.ThrowIfNull(state);
            return state.Exists && !state.Dependent &&
                   string.Equals(state.Description, spec.Description, StringComparison.Ordinal);
        }

        /// <summary>Exact, printable rendering of a dash text for evidence messages.</summary>
        public static string DescribeText(string? text)
        {
            if (text == null) return "null";
            var builder = new System.Text.StringBuilder("'");
            var shown = 0;
            foreach (var ch in text)
            {
                if (shown++ == 24) { builder.Append('…'); break; }
                builder.Append(ch < ' ' || ch > '~' ? $"\\u{(int)ch:X4}" : ch.ToString());
            }
            return builder.Append('\'').Append(" len=").Append(text.Length).ToString();
        }

        /// <summary>Required linetypes that the drawing's linetype table does not define.</summary>
        public static IReadOnlyList<string> MissingLinetypes(IEnumerable<string> definedNames)
        {
            var defined = new HashSet<string>(
                definedNames.Where(n => !string.IsNullOrWhiteSpace(n)), StringComparer.OrdinalIgnoreCase);
            return RequiredLinetypes.Where(n => !defined.Contains(n)).ToList();
        }

        /// <summary>
        /// The linetype an entity ended up with must be the one requested; a silent
        /// fallback to CONTINUOUS/ByLayer is a failure, never a cosmetic difference.
        /// </summary>
        public static bool LinetypeApplied(string requested, string? actual) =>
            !string.IsNullOrWhiteSpace(requested) &&
            string.Equals(requested.Trim(), (actual ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Fingerprint of an office block DEFINITION: the block header (origin/base
    /// point, units, scaling, explodable) plus every entity signature. A definition
    /// whose base point was moved shifts every reference on screen while the
    /// entity list stays identical — the header must be part of the hash.
    /// </summary>
    public static class BlockDefinitionFingerprintLogic
    {
        public const string Version = "blockdef-v3";
        public const string EntityVersion = "blockentities-v1";

        public sealed record Header(
            double OriginX, double OriginY, double OriginZ,
            string Units, string BlockScaling, bool Explodable,
            string Annotative, string PaperOrientation);

        public static string Compose(Header header, IEnumerable<string> entitySignatures)
        {
            ArgumentNullException.ThrowIfNull(header);
            foreach (var v in new[] { header.OriginX, header.OriginY, header.OriginZ })
            {
                if (!double.IsFinite(v))
                    throw new ArgumentOutOfRangeException(nameof(header), "Block origin must be finite.");
            }
            var signatures = entitySignatures.Where(s => !string.IsNullOrEmpty(s)).ToList();
            signatures.Sort(StringComparer.Ordinal);
            var canonical = string.Join("\n", new[]
            {
                Version,
                "origin=" + R(header.OriginX) + "," + R(header.OriginY) + "," + R(header.OriginZ),
                "units=" + header.Units,
                "scaling=" + header.BlockScaling,
                "explodable=" + header.Explodable,
                "annotative=" + header.Annotative,
                "paper-orientation=" + header.PaperOrientation,
            }.Concat(signatures));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        }

        public static string ComposeEntities(IEnumerable<string> entitySignatures)
        {
            ArgumentNullException.ThrowIfNull(entitySignatures);
            var signatures = entitySignatures.Where(s => !string.IsNullOrEmpty(s)).ToList();
            signatures.Sort(StringComparer.Ordinal);
            var canonical = string.Join("\n", new[] { EntityVersion }.Concat(signatures));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }

        /// <summary>
        /// Stable proof that a live office definition is still the audited source geometry. Entity extents are
        /// derived by AutoCAD's graphics cache and drift in their last bits after a regen (live 29.09.2026: the
        /// front-car definition read 8.370361393584146 at APPLY and 8.37036139358417 at VERIFY, which flipped the
        /// exact fingerprint of an unchanged block). The stable signature keeps every stored value exact and rounds
        /// only extents to a micron. Used as a fallback proof; the exact fingerprints and stamped comments are unchanged.
        /// </summary>
        public const string StableEntityVersion = "blockentities-stable-v1";

        public static string ComposeStableEntities(IEnumerable<string> stableEntitySignatures)
        {
            ArgumentNullException.ThrowIfNull(stableEntitySignatures);
            var signatures = stableEntitySignatures.Where(s => !string.IsNullOrEmpty(s)).ToList();
            signatures.Sort(StringComparer.Ordinal);
            var canonical = string.Join("\n", new[] { StableEntityVersion }.Concat(signatures));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }

        /// <summary>A derived extent value rounded to a micron (−0 written as 0).</summary>
        public static string StableExtentValue(double value)
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Extents must be finite.");
            var rounded = Math.Round(value, 6, MidpointRounding.AwayFromZero);
            if (rounded == 0) rounded = 0; // normalizes −0
            return rounded.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// A block definition is tool-owned when its comment is the pinned source
        /// comment, with or without a geometry fingerprint of ANY fingerprint version.
        /// Geometry itself is always re-proven against the embedded source, so an older
        /// fingerprint version never weakens the check; it only decides whether the
        /// comment may be refreshed (batch) or must block (selected scope).
        /// </summary>
        public static bool IsToolProvenance(string? comments, string sourceComment)
        {
            if (string.IsNullOrEmpty(sourceComment) || comments == null) return false;
            if (string.Equals(comments, sourceComment, StringComparison.Ordinal)) return true;
            var prefix = sourceComment + "; geometry_sha256=";
            return comments.StartsWith(prefix, StringComparison.Ordinal) &&
                   comments.Length == prefix.Length + 64 &&
                   comments.Substring(prefix.Length).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
        }
    }
}
