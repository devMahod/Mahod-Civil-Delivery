using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.Civil3D.Plugin.Services.SheetQA.Pure;
using AcEntity = Autodesk.AutoCAD.DatabaseServices.Entity;

namespace MahodAI.Civil3D.Plugin.Services.SheetQA
{
    /// <summary>
    /// Draws the review markup: a red circle around each finding with its id beside it, all
    /// on one dedicated layer.
    ///
    /// This is Ari's Mode A — the tool points, the drafter fixes. Everything it writes lives
    /// on <see cref="MarkupLayerName"/>, so a rescan or a single layer delete removes every
    /// trace of it and the drawing is exactly as it was. Nothing else in the drawing is
    /// touched: no entity is moved, recoloured or erased by a scan.
    ///
    /// Findings seen through the viewport are marked in model space next to the geometry
    /// they describe; findings about the sheet itself (an orphan legend row) are marked in
    /// that layout's paper space. Findings with no place on the sheet — a whole layer being
    /// a declutter candidate — are reported but not circled.
    /// </summary>
    public sealed class VisualMarkupWriter
    {
        /// <summary>The one layer every piece of scan markup is written to.</summary>
        public const string MarkupLayerName = "MAHOD_VISUAL_SCAN";

        /// <summary>ACI 1 — red, the review colour on every one of these sheets.</summary>
        private const short MarkupColorIndex = 1;

        /// <summary>The scan's own TrueType text style, so labels never inherit a Hebrew SHX.</summary>
        internal const string MarkupTextStyleName = "MAHOD_VISUAL_SCAN";

        private readonly Transaction _tr;
        private readonly Database _db;

        public VisualMarkupWriter(Transaction tr, Database db)
        {
            _tr = tr ?? throw new ArgumentNullException(nameof(tr));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        /// <summary>
        /// Draws a circle and an id label for every finding that has a location. Returns how
        /// many were drawn; each finding gets its marker handle recorded so a later fix pass
        /// can find its own markup again.
        /// </summary>
        public int DrawFindings(Layout layout, IEnumerable<VisualFinding> findings)
        {
            if (layout == null || findings == null) return 0;

            var layerId = EnsureMarkupLayer();
            if (layerId.IsNull) return 0;

            BlockTableRecord? modelSpace = null;
            BlockTableRecord? paperSpace = null;
            var drawn = 0;

            var located = findings.Where(f => f.HasLocation && f.Radius > 0).ToList();
            if (located.Count == 0) return 0;

            // One label size per space. Sizing each label off its own circle made the few
            // huge findings shout over the sheet while the ordinary ones vanished; the
            // number is a reference to the report, so it wants to read the same everywhere.
            var styleId = EnsureMarkupTextStyle();
            var labelHeight = new Dictionary<SheetSpace, double>
            {
                [SheetSpace.Model] = LabelHeightFor(located, SheetSpace.Model),
                [SheetSpace.Paper] = LabelHeightFor(located, SheetSpace.Paper)
            };

            foreach (var finding in located)
            {

                try
                {
                    BlockTableRecord? owner;
                    if (finding.Space == SheetSpace.Paper)
                    {
                        paperSpace ??= _tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite) as BlockTableRecord;
                        owner = paperSpace;
                    }
                    else
                    {
                        modelSpace ??= OpenModelSpaceForWrite();
                        owner = modelSpace;
                    }

                    if (owner == null) continue;

                    var circle = DrawCircle(owner, finding);
                    DrawLabel(owner, finding, labelHeight[finding.Space], styleId);

                    finding.MarkerHandle = circle;
                    drawn++;
                }
                catch (Exception ex)
                {
                    // One un-drawable marker must not cost the rest of the report.
                    Debug.WriteLine($"[SheetQA] Could not mark {finding.Id}: {ex.Message}");
                }
            }

            return drawn;
        }

        /// <summary>
        /// Erases every entity on the markup layer, in model space and in all layouts.
        /// This is both the "clear markup" command and the reset a rescan performs, so a
        /// second scan never stacks circles on top of the first one's.
        /// </summary>
        public int EraseAllMarkup()
        {
            var erased = 0;

            if (_tr.GetObject(_db.BlockTableId, OpenMode.ForRead) is not BlockTable bt) return 0;

            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord? btr;
                try { btr = _tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord; }
                catch { continue; }

                if (btr == null || !btr.IsLayout) continue;

                var doomed = new List<ObjectId>();
                foreach (ObjectId id in btr)
                {
                    try
                    {
                        if (_tr.GetObject(id, OpenMode.ForRead) is AcEntity ent &&
                            string.Equals(ent.Layer, MarkupLayerName, StringComparison.OrdinalIgnoreCase))
                        {
                            doomed.Add(id);
                        }
                    }
                    catch { /* an unreadable entity is not ours to erase */ }
                }

                foreach (var id in doomed)
                {
                    try
                    {
                        if (_tr.GetObject(id, OpenMode.ForWrite) is AcEntity ent)
                        {
                            ent.Erase();
                            erased++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SheetQA] Could not erase markup entity: {ex.Message}");
                    }
                }
            }

            return erased;
        }

        /// <summary>
        /// Creates the markup layer if it is missing. Called before every write because the
        /// layer is disposable by design: the user may delete it, or reopen the drawing
        /// without ever saving it, and a missing layer makes the entity write throw.
        /// </summary>
        public ObjectId EnsureMarkupLayer()
        {
            try
            {
                if (_tr.GetObject(_db.LayerTableId, OpenMode.ForRead) is not LayerTable layers) return ObjectId.Null;

                if (layers.Has(MarkupLayerName)) return layers[MarkupLayerName];

                layers.UpgradeOpen();
                var record = new LayerTableRecord
                {
                    Name = MarkupLayerName,
                    Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, MarkupColorIndex),
                    Description = "MahodAI visual scan markup — safe to delete"
                };

                var id = layers.Add(record);
                _tr.AddNewlyCreatedDBObject(record, true);
                return id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SheetQA] Could not create markup layer: {ex.Message}");
                return ObjectId.Null;
            }
        }

        private BlockTableRecord? OpenModelSpaceForWrite()
        {
            if (_tr.GetObject(_db.BlockTableId, OpenMode.ForRead) is not BlockTable bt) return null;
            return _tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;
        }

        private string DrawCircle(BlockTableRecord owner, VisualFinding finding)
        {
            var circle = new Circle(
                new Point3d(finding.CenterX, finding.CenterY, 0),
                Vector3d.ZAxis,
                finding.Radius)
            {
                Layer = MarkupLayerName
            };

            owner.AppendEntity(circle);
            _tr.AddNewlyCreatedDBObject(circle, true);
            return circle.Handle.ToString();
        }

        /// <summary>
        /// Writes the finding's number just outside the circle — the same number the report
        /// row carries, so the reviewer can look from the list to the sheet and back.
        /// </summary>
        /// <remarks>
        /// Only the sequence number is drawn, not the full id. On a real coordination sheet
        /// the full "VS-LAYOUT1-125" came out as "521-1??????-??": these drawings make a
        /// Hebrew SHX font current, which has no Latin glyphs (hence the ?) and lays text out
        /// right-to-left (hence the reversal). The number alone is short enough to read at
        /// sheet scale, and <see cref="EnsureMarkupTextStyle"/> pins a TrueType style so the
        /// digits cannot inherit that problem either.
        /// </remarks>
        private void DrawLabel(
            BlockTableRecord owner, VisualFinding finding, double height, ObjectId styleId)
        {
            var text = new DBText
            {
                TextString = FindingIdGenerator.MarkerLabel(finding.Id),
                Position = new Point3d(
                    finding.CenterX + finding.Radius * 1.05,
                    finding.CenterY + finding.Radius * 1.05,
                    0),
                Height = height,
                Layer = MarkupLayerName
            };

            if (!styleId.IsNull) text.TextStyleId = styleId;

            owner.AppendEntity(text);
            _tr.AddNewlyCreatedDBObject(text, true);
        }

        /// <summary>
        /// One label height per space, from the median circle so a handful of very large
        /// findings cannot set the size for the whole sheet.
        /// </summary>
        private static double LabelHeightFor(List<VisualFinding> located, SheetSpace space)
        {
            var radii = located
                .Where(f => f.Space == space)
                .Select(f => f.Radius)
                .OrderBy(r => r)
                .ToList();

            if (radii.Count == 0) return 1.0;

            var median = radii[radii.Count / 2];
            return Math.Max(median * 0.8, 0.5);
        }

        /// <summary>
        /// A TrueType text style owned by the scan. Without it the labels inherit whatever
        /// style the drawing has current — on these sheets a Hebrew SHX that renders Latin
        /// characters as "?" and draws backwards.
        /// </summary>
        private ObjectId EnsureMarkupTextStyle()
        {
            try
            {
                if (_tr.GetObject(_db.TextStyleTableId, OpenMode.ForRead) is not TextStyleTable styles)
                    return ObjectId.Null;

                if (styles.Has(MarkupTextStyleName)) return styles[MarkupTextStyleName];

                styles.UpgradeOpen();
                var record = new TextStyleTableRecord
                {
                    Name = MarkupTextStyleName,
                    FileName = "Arial.ttf",
                    IsVertical = false
                };
                record.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                    "Arial", false, false, 0, 0);

                var id = styles.Add(record);
                _tr.AddNewlyCreatedDBObject(record, true);
                return id;
            }
            catch (Exception ex)
            {
                // Falling back to the current style still draws a (possibly ugly) number,
                // which beats losing the markup entirely.
                Debug.WriteLine($"[SheetQA] Could not create markup text style: {ex.Message}");
                return ObjectId.Null;
            }
        }

        /// <summary>True when the drawing currently carries scan markup.</summary>
        public bool HasMarkup()
        {
            try
            {
                if (_tr.GetObject(_db.LayerTableId, OpenMode.ForRead) is not LayerTable layers) return false;
                return layers.Has(MarkupLayerName);
            }
            catch { return false; }
        }
    }
}
