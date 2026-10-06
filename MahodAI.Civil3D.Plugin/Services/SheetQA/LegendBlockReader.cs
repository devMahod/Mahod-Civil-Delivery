using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;
using AcLine = Autodesk.AutoCAD.DatabaseServices.Line;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA
{
    /// <summary>What the legend on one sheet contains.</summary>
    public sealed class LegendReadResult
    {
        /// <summary>Block the legend was read from; null when no legend was found.</summary>
        public string? BlockName { get; set; }

        /// <summary>True when the block was identified by name rather than by shape.</summary>
        public bool IdentifiedByName { get; set; }

        public List<LegendRow> Rows { get; set; } = new();

        public int SampleLineCount { get; set; }
        public int LabelCount { get; set; }

        public string? Note { get; set; }

        // Additive: legacy SheetQA consumers retain all readable rows and notes.
        // Counts failure events (a selection/scoring retry can observe one member twice), not missing rows.
        public int ReadFailureCount { get; private set; }
        public bool IsComplete => ReadFailureCount == 0;
        internal void NoteReadFailure() => ReadFailureCount++;
    }

    /// <summary>
    /// Finds and reads the legend printed on a sheet.
    ///
    /// Ari's instruction fixes the reference: each layout carries its own legend and the
    /// check compares against THAT, not a project-wide list. The legend is a block sitting
    /// in the layout's paper space — a local block in the RD383 package ("mikraut") and an
    /// xref in the metro package ("mikra M13 ADW") — so both cases are handled the same way.
    ///
    /// Identification is by name first (מקרא transliterates as mikra/mikraut, English sheets
    /// say legend/key). When no name matches, the fallback picks the paper-space block that
    /// most looks like a legend: many short sample lines, each with a caption beside it. The
    /// result always says which block was used and how it was chosen, so a wrong guess is
    /// visible rather than silent.
    /// </summary>
    public sealed class LegendBlockReader
    {
        private readonly Transaction _tr;
        private readonly SheetStyleResolver _styles;

        /// <summary>Name fragments that identify a legend block outright.</summary>
        private static readonly string[] LegendNameHints =
        {
            "mikra", "mekra", "legend", "key-plan", "keyplan", "מקרא"
        };

        public LegendBlockReader(Transaction tr, SheetStyleResolver styles)
        {
            _tr = tr ?? throw new ArgumentNullException(nameof(tr));
            _styles = styles ?? throw new ArgumentNullException(nameof(styles));
        }

        /// <summary>
        /// Civil Delivery's evidence read (additive; SheetQA leaves it off, so its output is unchanged). Every distinct legend
        /// found by name on the layout is read and the rows united; a legend whose definition is an unloaded or unresolved XREF
        /// is a failed read; members that are not visible or lie on a frozen/off layer are not rows; a key plan is not a legend.
        /// </summary>
        public bool ReadForEvidence { get; init; }

        /// <summary>Reads the legend belonging to the given layout.</summary>
        public LegendReadResult Read(Layout layout)
        {
            var result = new LegendReadResult();
            if (layout == null) return result;

            if (_tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead) is not BlockTableRecord paperBtr)
            {
                result.NoteReadFailure();
                result.Note = "Layout has no paper-space block record.";
                return result;
            }

            var candidates = CollectCandidateBlocks(paperBtr, result);
            if (candidates.Count == 0)
            {
                result.Note = "No block on this sheet looks like a legend.";
                return result;
            }

            var named = candidates.FirstOrDefault(c => MatchesLegendName(c.Name, ReadForEvidence));
            var chosen = named ?? candidates
                .OrderByDescending(c => ScoreAsLegend(c.Definition, result))
                .First();

            result.BlockName = chosen.Name;
            result.IdentifiedByName = named != null;

            var (lines, labels) = ReadBlockContents(chosen.Definition, result);
            result.SampleLineCount = lines.Count;
            result.LabelCount = labels.Count;
            result.Rows = LegendMatcher.PairRows(lines, labels);
            if (ReadForEvidence) ReadOtherNamedLegends(candidates, chosen, named != null, result);

            if (named == null)
            {
                result.Note =
                    $"No block named like a legend; '{chosen.Name}' was used because it has the most " +
                    "captioned sample lines. Confirm before acting on legend findings.";
            }
            else if (result.Rows.Count == 0)
            {
                result.Note = $"Legend block '{chosen.Name}' contains no sample lines to compare.";
            }

            return result;
        }

        /// <summary>A paper-space block that might be the legend.</summary>
        private sealed class LegendCandidate
        {
            public string Name { get; init; } = string.Empty;
            public BlockTableRecord Definition { get; init; } = null!;
        }

        private List<LegendCandidate> CollectCandidateBlocks(BlockTableRecord paperBtr, LegendReadResult result)
        {
            var candidates = new List<LegendCandidate>();

            foreach (ObjectId id in paperBtr)
            {
                try
                {
                    if (_tr.GetObject(id, OpenMode.ForRead) is not BlockReference blockRef) continue;
                    if (_styles.IsLayerHidden(blockRef.Layer)) continue;
                    if (_tr.GetObject(blockRef.BlockTableRecord, OpenMode.ForRead) is not BlockTableRecord def)
                    {
                        result.NoteReadFailure();
                        continue;
                    }

                    candidates.Add(new LegendCandidate { Name = def.Name, Definition = def });
                }
                catch (Exception ex)
                {
                    result.NoteReadFailure();
                    Debug.WriteLine($"[SheetQA] Legend candidate skipped: {ex.Message}");
                }
            }

            return candidates;
        }

        internal static bool MatchesLegendName(string? blockName) => MatchesLegendName(blockName, forEvidence: false);

        /// <param name="forEvidence">A key plan locates the sheet; it is not a legend of what the drawing's objects are.</param>
        internal static bool MatchesLegendName(string? blockName, bool forEvidence)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return false;
            var name = blockName.ToLowerInvariant();
            return LegendNameHints.Any(hint => name.Contains(hint, StringComparison.Ordinal) &&
                                               !(forEvidence && hint is "key-plan" or "keyplan"));
        }

        /// <summary>
        /// Evidence mode, after the chosen legend was read: a legend the reader cannot see into (an unloaded or unresolved XREF
        /// definition) is a failed read, and every other distinct legend found by name is read too, each paired on its own.
        /// </summary>
        private void ReadOtherNamedLegends(List<LegendCandidate> candidates, LegendCandidate chosen, bool byName, LegendReadResult result)
        {
            if (UnreadXref(chosen.Definition)) result.NoteReadFailure();
            if (!byName) return;
            var names = new List<string> { chosen.Name };
            var read = new HashSet<ObjectId> { chosen.Definition.ObjectId };
            foreach (var other in candidates.Where(c => MatchesLegendName(c.Name, forEvidence: true)))
            {
                if (!read.Add(other.Definition.ObjectId)) continue;
                if (UnreadXref(other.Definition)) result.NoteReadFailure();
                var (lines, labels) = ReadBlockContents(other.Definition, result);
                result.SampleLineCount += lines.Count;
                result.LabelCount += labels.Count;
                result.Rows.AddRange(LegendMatcher.PairRows(lines, labels));
                if (!names.Contains(other.Name, StringComparer.Ordinal)) names.Add(other.Name);
            }
            result.BlockName = string.Join(" + ", names);
        }

        private static bool UnreadXref(BlockTableRecord definition)
        {
            try
            {
                return (definition.IsFromExternalReference || definition.IsFromOverlayReference) &&
                       (definition.IsUnloaded || !definition.IsResolved);
            }
            catch { return true; }
        }

        /// <summary>
        /// How legend-like a block is: sample lines that each have a caption. A title block
        /// has plenty of text but few short lines paired with it, so it scores low.
        /// </summary>
        private int ScoreAsLegend(BlockTableRecord definition, LegendReadResult result)
        {
            try
            {
                var (lines, labels) = ReadBlockContents(definition, result);
                if (lines.Count == 0 || labels.Count == 0) return 0;
                return Math.Min(lines.Count, labels.Count);
            }
            catch { result.NoteReadFailure(); return 0; }
        }

        private (List<LegendSampleLine> Lines, List<LegendLabelText> Labels) ReadBlockContents(
            BlockTableRecord definition, LegendReadResult result)
        {
            var lines = new List<LegendSampleLine>();
            var labels = new List<LegendLabelText>();

            foreach (ObjectId id in definition)
            {
                AcEntity? ent;
                try { ent = _tr.GetObject(id, OpenMode.ForRead) as AcEntity; }
                catch { result.NoteReadFailure(); continue; }

                if (ent == null) continue;
                // Evidence mode: a member that is not printed is not a row of the legend the engineer reads (a member on
                // layer 0 takes the insert's layer, which was checked when the legend was chosen).
                if (ReadForEvidence && (!ent.Visible ||
                        (!string.Equals(ent.Layer, "0", StringComparison.Ordinal) && _styles.IsLayerHidden(ent.Layer))))
                    continue;

                // Only supported sample/caption entities need extents. An unsupported symbol is not a
                // failed read; this preserves the legacy scope without marking unrelated geometry unread.
                if (ent is not (AcLine or Polyline or Polyline2d or DBText or MText)) continue;

                try
                {
                    var extents = ent.GeometricExtents;
                    var midY = (extents.MinPoint.Y + extents.MaxPoint.Y) / 2;

                    switch (ent)
                    {
                        case AcLine or Polyline or Polyline2d:
                            lines.Add(new LegendSampleLine
                            {
                                ColorIndex = _styles.ResolveColorIndex(ent),
                                Linetype = _styles.ResolveLinetype(ent),
                                MidY = midY
                            });
                            break;

                        case DBText dbText:
                            labels.Add(MakeLabel(dbText.TextString, dbText.TextStyleId, midY));
                            break;

                        case MText mText:
                            labels.Add(MakeLabel(mText.Text, mText.TextStyleId, midY));
                            break;
                    }
                }
                catch
                {
                    // Retain readable legacy rows, but do not present a partial legend as complete
                    // evidence: an unread caption can also change which caption is paired with a line.
                    result.NoteReadFailure();
                }
            }

            return (lines, labels);
        }

        private LegendLabelText MakeLabel(string raw, ObjectId styleId, double midY) => new()
        {
            RawLabel = raw ?? string.Empty,
            Label = HebrewCadTextDecoder.Decode(raw, _styles.GetFontFile(styleId)),
            MidY = midY
        };
    }
}
