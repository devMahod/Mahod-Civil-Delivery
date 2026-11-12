using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using MahodAI.CivilDelivery.Estimate.Infrastructure;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// The cut and fill on the plan: the work area in coloured bands — reds where the ground is dug down, blues where it is
/// built up, by depth — like a cut/fill map in Civil 3D, but drawn as transient graphics only: nothing is added to the
/// drawing, and it goes away with the next estimate or a second press.
/// </summary>
internal sealed class EarthworksMap : IDisposable
{
    /// <summary>Depth classes (m): cut deeper than 2, 1–2, 0.3–1; ±0.3 at grade; fill 0.3–1, 1–2, deeper than 2.</summary>
    internal static readonly (double From, double To, byte R, byte G, byte B, string Label)[] Classes =
    {
        (double.NegativeInfinity, -2, 0xB7, 0x1C, 0x1C, "חפירה מעל 2 מ׳"),
        (-2, -1, 0xE5, 0x39, 0x35, "חפירה 1–2 מ׳"),
        (-1, -0.3, 0xEF, 0x9A, 0x9A, "חפירה 0.3–1 מ׳"),
        (-0.3, 0.3, 0xBD, 0xBD, 0xBD, "כמעט במפלס"),
        (0.3, 1, 0x90, 0xCA, 0xF9, "מילוי 0.3–1 מ׳"),
        (1, 2, 0x1E, 0x88, 0xE5, "מילוי 1–2 מ׳"),
        (2, double.PositiveInfinity, 0x0D, 0x47, 0xA1, "מילוי מעל 2 מ׳"),
    };

    private readonly List<Entity> _shown = new();
    internal bool Visible => _shown.Count > 0;

    /// <summary>Draws the map of <paramref name="grid"/> (metres) in host units (<paramref name="metresPerUnit"/>).</summary>
    internal void Show(Earthworks.Grid grid, double? platform, double metresPerUnit)
    {
        Clear();
        // At most ~180 bands across: averaging cells keeps the map light on a large area.
        var step = Math.Max(1, (int)Math.Ceiling(Math.Max(grid.Nx, grid.Ny) / 180.0));
        var nx = (grid.Nx + step - 1) / step; var ny = (grid.Ny + step - 1) / step;
        var cls = new int[nx * ny];
        for (var J = 0; J < ny; J++)
            for (var I = 0; I < nx; I++)
            {
                double sum = 0; var n = 0;
                for (var j = J * step; j < Math.Min(grid.Ny, (J + 1) * step); j++)
                    for (var i = I * step; i < Math.Min(grid.Nx, (I + 1) * step); i++)
                    {
                        var k = j * grid.Nx + i;
                        var g = grid.Ground[k];
                        double target = platform ?? grid.Design[k];
                        if (float.IsNaN(g) || double.IsNaN(target)) continue;
                        sum += target - g; n++;
                    }
                cls[J * nx + I] = n == 0 ? -1 : Class(sum / n);
            }
        var size = grid.Cell * step / metresPerUnit;
        var x0 = grid.X0 / metresPerUnit; var y0 = grid.Y0 / metresPerUnit;
        for (var c = 0; c < Classes.Length; c++)
        {
            Hatch? hatch = null;
            for (var J = 0; J < ny; J++)
            {
                var I = 0;
                while (I < nx)
                {
                    if (cls[J * nx + I] != c) { I++; continue; }
                    var start = I;
                    while (I < nx && cls[J * nx + I] == c) I++;
                    hatch ??= NewHatch(c);
                    double ax = x0 + start * size, bx = x0 + I * size, ay = y0 + J * size, by = y0 + (J + 1) * size;
                    var ring = new Point2dCollection { new(ax, ay), new(bx, ay), new(bx, by), new(ax, by), new(ax, ay) };
                    hatch.AppendLoop(HatchLoopTypes.Polyline, ring, new DoubleCollection { 0, 0, 0, 0, 0 });
                }
            }
            if (hatch == null) continue;
            try { hatch.EvaluateHatch(true); Add(hatch); }
            catch (System.Exception) { hatch.Dispose(); }
        }
    }

    private static int Class(double dz)
    {
        for (var c = 0; c < Classes.Length; c++) if (dz >= Classes[c].From && dz < Classes[c].To) return c;
        return Classes.Length - 1;
    }

    private static Hatch NewHatch(int c)
    {
        var h = new Hatch();
        h.SetDatabaseDefaults();
        h.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
        h.Color = Autodesk.AutoCAD.Colors.Color.FromRgb(Classes[c].R, Classes[c].G, Classes[c].B);
        h.Transparency = new Transparency((byte)190);   // ~25 % see-through: the plan stays readable under it
        return h;
    }

    private void Add(Entity e)
    {
        try { TransientManager.CurrentTransientManager.AddTransient(e, TransientDrawingMode.Main, 127, new IntegerCollection()); _shown.Add(e); }
        catch (System.Exception) { e.Dispose(); }
    }

    internal void Clear()
    {
        foreach (var e in _shown)
        {
            try { TransientManager.CurrentTransientManager.EraseTransient(e, new IntegerCollection()); } catch (System.Exception) { }
            e.Dispose();
        }
        _shown.Clear();
    }

    public void Dispose() => Clear();
}
