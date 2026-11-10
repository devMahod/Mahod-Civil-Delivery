using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// What belongs to the plan. The 130X road plan (live 2026-11-10) counted three things that are not the design:
/// the same XREF attached twice at the same place (once on a frozen layer — its 20 broken hatches filled the red
/// banner — and once visibly, so every object of it was counted twice), sheet-frame hatches (1.34 million m²
/// "recovered"), and key-map insets: the whole design XREF'd again at 1:23 or 1:40 beside the title block.
/// <para>
/// Objects on a frozen or off layer INSIDE an attached drawing are still measured: engineers freeze utility layers to
/// clear a road sheet (130X froze its 6.6 km of LV cable), and that work is still in the bill of quantities.
/// </para>
/// </summary>
internal sealed class DrawingVisibility
{
    private readonly HashSet<(ObjectId Block, string Placement)> _placements = new();
    private static readonly Regex SheetFrame = new(@"(?:^|[-_ ])(?:FRM|FRAME|BORDER|TITLE|TITLEBLOCK)(?:[-_ ]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>An object hidden on its own (Visible = false): nothing on screen, nothing to measure.</summary>
    internal int Hidden { get; private set; }

    internal bool Visible(Entity entity)
    {
        if (entity.Visible) return true;
        Hidden++;
        return false;
    }

    /// <summary>An XREF attached on a frozen layer: the whole attachment is switched off in this drawing.</summary>
    internal static bool SwitchedOff(Transaction tr, BlockReference reference) =>
        !reference.LayerId.IsNull && tr.GetObject(reference.LayerId, OpenMode.ForRead) is LayerTableRecord { IsFrozen: true };

    /// <summary>Sheet frame / title block linework and hatches (0-FRM-A0): drafting, never a quantity.</summary>
    internal static bool IsSheetFrame(string layer) => SheetFrame.IsMatch(layer.Split('|').Last());

    private static readonly Regex SectionSheets = new(@"^(?:HATAHIM|HATAKHIM|HATACHIM|CHATACHIM|SECTIONS?|X-?SECTIONS?|CROSS-?SECTIONS?|חתכים)(?:[-_ .\d]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// A drawing of cross-section sheets (HATAHIM = חתכים) attached to the plan: its hatches, trees and signs are section
    /// drafting laid out beside the plan, not plan quantities (130X: 60,162 m² of "landscaping" from one help hatch).
    /// </summary>
    internal static bool IsSectionSheets(string xrefName, string path) =>
        SectionSheets.IsMatch(xrefName) || SectionSheets.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path ?? ""));

    /// <summary>False when this XREF was already read at exactly this place: attaching it twice draws it once on screen.</summary>
    internal bool FirstPlacement(ObjectId block, Matrix3d placement) =>
        _placements.Add((block, string.Join(",", placement.ToArray().Select(v => Math.Round(v, 6).ToString("R", CultureInfo.InvariantCulture)))));

    /// <summary>
    /// A key map / inset: an XREF scaled by anything but a unit conversion (1, 10ⁿ, feet↔metres). The design itself is
    /// attached at a unit scale; a 1:23 copy beside the title block is a location map, not more road.
    /// </summary>
    internal static bool IsInset(Scale3d scale)
    {
        var x = Math.Abs(scale.X);
        if (!double.IsFinite(x) || x <= 0) return true;
        if (Math.Abs(Math.Abs(scale.Y) - x) > x * 1e-6) return true;
        var log = Math.Log10(x);
        if (Math.Abs(log - Math.Round(log)) < 1e-6) return false;
        foreach (var feet in new[] { 0.3048, 1 / 0.3048, 0.0003048, 304.8 })
            if (Math.Abs(x - feet) < feet * 1e-6) return false;
        return true;
    }
}
