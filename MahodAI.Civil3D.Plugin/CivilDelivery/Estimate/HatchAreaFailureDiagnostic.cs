using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Bounded, read-only raw boundary evidence AFTER Area has failed. This never
/// evaluates, repairs, explodes or measures the hatch, and never supplies a fallback
/// quantity. In particular, readable loops do not prove a valid net filled area.
/// </summary>
internal static class HatchAreaFailureDiagnostic
{
    internal const int MaximumLoops = 32;
    internal const int MaximumItems = 512;
    internal const int MaximumHatches = 128;
    internal sealed record Loop(string Kind, string Flags, IReadOnlyList<string> Items, bool Truncated);
    internal sealed record LoopResult(int Index, Loop? Evidence, string? Error);
    internal sealed record Snapshot(string Purpose, string CoordinateSystem, int? DeclaredLoops,
        string? Header, IReadOnlyList<LoopResult> Loops, bool Truncated, string? Error);
    internal sealed record SourceSnapshot(ProvenanceRef Source, Snapshot Boundary);
    internal sealed class Batch
    {
        public string Purpose => "original Area API failures; strict recovery outcome is recorded in quantity evidence; unrecovered failed quantities remain failed";
        public List<SourceSnapshot> Sources { get; } = new();
        public int OmittedByDiagnosticLimit { get; private set; }
        public void Add(ProvenanceRef source, Func<Snapshot> read)
        {
            if (Sources.Count >= MaximumHatches) { OmittedByDiagnosticLimit++; return; }
            Sources.Add(new(source, read()));
        }
    }

    internal static Snapshot CaptureNative(Hatch hatch)
    {
        // The native handle/path/hash are attached separately by the traversal's
        // existing MeasurementFailureProvenance. Never resolve this leaf in host DB.
        return Capture(
            () => $"style={hatch.HatchStyle}; normal={Point(hatch.Normal.X, hatch.Normal.Y, hatch.Normal.Z)}; " +
                  $"elevation={Number(hatch.Elevation)}",
            () => hatch.NumberOfLoops,
            (index, budget) => ReadLoop(hatch, index, budget));
    }

    // Delegate boundary permits tests of failure retention and bounds without
    // constructing a native Hatch or pretending those tests exercise Autodesk.
    internal static Snapshot Capture(Func<string> header, Func<int> loopCount,
        Func<int, int, Loop> readLoop)
    {
        const string purpose = "original Area API failure evidence; boundary capture alone grants no quantity; strict recovery outcome is recorded in quantity evidence";
        const string coordinates = "raw hatch OCS / source drawing units; not host WCS or square metres";
        var loops = new List<LoopResult>();
        string? metadata = null;
        string? error = null;
        int? declared = null;
        var truncated = false;
        try { metadata = header(); }
        catch (System.Exception ex) { error = "header: " + ex.GetType().Name + ": " + ex.Message; }
        try { declared = loopCount(); }
        catch (System.Exception ex)
        {
            return new(purpose, coordinates, null, metadata, loops, false,
                Join(error, "loop-count: " + ex.GetType().Name + ": " + ex.Message));
        }
        if (declared < 0)
            return new(purpose, coordinates, declared, metadata, loops, false,
                Join(error, "negative loop count; no boundary classification made"));

        var remaining = MaximumItems;
        for (var i = 0; i < Math.Min(declared.Value, MaximumLoops); i++)
        {
            if (remaining == 0) { truncated = true; break; }
            try
            {
                var loop = readLoop(i, remaining);
                // Enforce the total output bound even if a future adapter ignores it.
                var count = Math.Min(remaining, loop.Items.Count);
                var items = new string[count];
                for (var j = 0; j < count; j++) items[j] = loop.Items[j];
                var partial = loop.Truncated || loop.Items.Count > count;
                loops.Add(new(i, new Loop(loop.Kind, loop.Flags, items, partial), null));
                remaining -= count;
                truncated |= partial;
            }
            catch (System.Exception ex)
            {
                loops.Add(new(i, null, ex.GetType().Name + ": " + ex.Message));
            }
        }
        truncated |= declared.Value > loops.Count;
        return new(purpose, coordinates, declared, metadata, loops, truncated, error);
    }

    private static Loop ReadLoop(Hatch hatch, int index, int budget)
    {
        var loop = hatch.GetLoopAt(index);
        var flags = loop.LoopType.ToString();
        var items = new List<string>();
        var truncated = false;
        if (loop.IsPolyline)
        {
            foreach (BulgeVertex vertex in loop.Polyline)
            {
                if (items.Count == budget) { truncated = true; break; }
                items.Add($"vertex={Point(vertex.Vertex.X, vertex.Vertex.Y)}; bulge={Number(vertex.Bulge)}");
            }
            return new("polyline", flags, items, truncated);
        }
        foreach (Curve2d edge in loop.Curves)
        {
            if (items.Count == budget) { truncated = true; break; }
            items.Add(edge switch
            {
                LineSegment2d line => $"line={Point(line.StartPoint.X, line.StartPoint.Y)} -> " +
                                      Point(line.EndPoint.X, line.EndPoint.Y),
                CircularArc2d arc => $"arc: start={Point(arc.StartPoint.X, arc.StartPoint.Y)}; " +
                                     $"end={Point(arc.EndPoint.X, arc.EndPoint.Y)}; " +
                                     $"centre={Point(arc.Center.X, arc.Center.Y)}; radius={Number(arc.Radius)}; " +
                                     $"angles={Point(arc.StartAngle, arc.EndAngle)}; clockwise={arc.IsClockWise}" +
                                     CaptureReferenceVector(() =>
                                     {
                                         var vector = arc.ReferenceVector;
                                         return (vector.X, vector.Y);
                                     }),
                _ => "unexpanded-edge=" + edge.GetType().FullName,
            });
        }
        return new("edge-list", flags, items, truncated);
    }

    // A failed getter is not a missing historical field and must not fall back to
    // an assumed OCS axis. Retain the other raw arc fields for diagnosis instead.
    internal static string CaptureReferenceVector(Func<(double X, double Y)> read)
    {
        try
        {
            var vector = read();
            return "; reference_vector=" + Point(vector.X, vector.Y);
        }
        catch (System.Exception ex)
        {
            return "; reference_vector_error=" + ex.GetType().Name;
        }
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Point(params double[] values) => string.Join(",", Array.ConvertAll(values, Number));
    private static string Join(string? first, string second) => first == null ? second : first + "; " + second;
}
