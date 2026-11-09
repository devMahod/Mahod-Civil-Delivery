using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Puts the report's structures on the plan, on the engineer's request: the surface parts of every segment as a
/// translucent solid hatch on one layer per structure (<c>MHD-BOQ-PAVE-&lt;structure&gt;</c>), and one label per segment
/// (segment, road, structure and its layers). Everything it draws carries the <see cref="App"/> tag, so placing again
/// replaces the previous placement and nothing else in the drawing is touched. One undo step.
/// </summary>
internal static class PavementPlacement
{
    internal const string App = "MAHOD_BOQ_PAVE";
    internal const string LayerPrefix = "MHD-BOQ-PAVE-";
    internal const string TextLayer = LayerPrefix + "TEXT";
    private const string TextStyle = "MAHOD_BOQ_HE";

    internal sealed record Piece(NtsGeometry Shape, string Structure, short Colour);
    internal sealed record Label(string Text, double X, double Y);

    /// <summary>Draws the pieces and labels in model space (host units). Returns how many hatches were drawn.</summary>
    internal static int Place(Document doc, IReadOnlyList<Piece> pieces, IReadOnlyList<Label> labels, double textHeight)
    {
        using var _ = doc.LockDocument();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();
        EnsureApp(tr, db);
        var model = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
        foreach (ObjectId id in model)
            if (tr.GetObject(id, OpenMode.ForRead) is Entity e && e.GetXDataForApplication(App) != null) { e.UpgradeOpen(); e.Erase(); }
        var drawn = 0;
        foreach (var piece in pieces)
        {
            var layer = EnsureLayer(tr, db, LayerPrefix + Safe(piece.Structure), piece.Colour);
            foreach (var (outer, holes) in HatchBoundaryArea.Polygons(piece.Shape))
            {
                var hatch = new Hatch { Layer = layer, ColorIndex = 256, Transparency = new Transparency((byte)(255 * 0.45)) };
                model.AppendEntity(hatch); tr.AddNewlyCreatedDBObject(hatch, true);
                hatch.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                try
                {
                    hatch.AppendLoop(HatchLoopTypes.Outermost, Ring(outer), Flat(outer.Count));
                    foreach (var hole in holes) hatch.AppendLoop(HatchLoopTypes.Default, Ring(hole), Flat(hole.Count));
                    hatch.EvaluateHatch(true);
                    Tag(hatch);
                    drawn++;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception) { hatch.Erase(); }
            }
        }
        if (labels.Count > 0)
        {
            var layer = EnsureLayer(tr, db, TextLayer, 7);
            var style = EnsureStyle(tr, db);
            foreach (var label in labels)
            {
                // Logical Hebrew: MText applies bidi itself (hazard: visual order shows reversed on screen).
                var text = new MText { Contents = label.Text.Replace("\n", "\\P"), Location = new Point3d(label.X, label.Y, 0), TextHeight = textHeight,
                    Attachment = AttachmentPoint.MiddleCenter, Layer = layer, TextStyleId = style, ColorIndex = 256 };
                model.AppendEntity(text); tr.AddNewlyCreatedDBObject(text, true);
                Tag(text);
            }
        }
        tr.Commit();
        return drawn;
    }

    private static Point2dCollection Ring(IReadOnlyList<(double X, double Y)> ring)
    {
        var points = new Point2dCollection();
        foreach (var p in ring) points.Add(new Point2d(p.X, p.Y));
        if (points.Count > 1 && !points[0].IsEqualTo(points[points.Count - 1])) points.Add(points[0]);
        return points;
    }
    private static DoubleCollection Flat(int count) => new(Enumerable.Repeat(0d, count + 1).ToArray());

    private static void Tag(Entity e) => e.XData = new ResultBuffer(new TypedValue((int)DxfCode.ExtendedDataRegAppName, App),
        new TypedValue((int)DxfCode.ExtendedDataAsciiString, "pavement report structure"));

    private static void EnsureApp(Transaction tr, Database db)
    {
        var apps = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (apps.Has(App)) return;
        apps.UpgradeOpen();
        var record = new RegAppTableRecord { Name = App };
        apps.Add(record); tr.AddNewlyCreatedDBObject(record, true);
    }

    private static string EnsureLayer(Transaction tr, Database db, string name, short colour)
    {
        var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!layers.Has(name))
        {
            layers.UpgradeOpen();
            var record = new LayerTableRecord { Name = name, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(ColorMethod.ByAci, colour), IsPlottable = true };
            layers.Add(record); tr.AddNewlyCreatedDBObject(record, true);
        }
        return name;
    }

    private static ObjectId EnsureStyle(Transaction tr, Database db)
    {
        var styles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (styles.Has(TextStyle)) return styles[TextStyle];
        styles.UpgradeOpen();
        // TrueType Arial with the Hebrew charset: an empty-typeface style plots Hebrew as mojibake.
        var style = new TextStyleTableRecord { Name = TextStyle, Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Arial", false, false, 177, 0) };
        var id = styles.Add(style); tr.AddNewlyCreatedDBObject(style, true);
        return id;
    }

    /// <summary>A layer-name-safe structure key (layer names refuse &lt;&gt;/\":;?*|,=`).</summary>
    private static string Safe(string text)
    {
        var s = new string(text.Select(c => "<>/\\\":;?*|,=`'״׳".Contains(c) ? '_' : c).ToArray()).Trim();
        return s.Length > 60 ? s[..60] : s;
    }

    /// <summary>A distinct AutoCAD colour per structure, stable for a given order.</summary>
    internal static short ColourFor(int index) => new short[] { 1, 3, 5, 6, 4, 2, 30, 140, 200, 50, 90, 240 }[index % 12];
}
