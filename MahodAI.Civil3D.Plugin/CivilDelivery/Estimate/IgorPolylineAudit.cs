using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

internal static class IgorPolylineAudit
{
    internal sealed record Comparison(string Layer, int NativeCount, double NativeLength, int IgorCount, double IgorLength, bool Match);
    internal sealed record Result(string Status, string Assembly, string Scope, IReadOnlyList<Comparison> Layers, string? Error);
    /// <summary>Calls the installed DLL's read-only static LWPOLYLINE routine. Does not load a foreign DLL or create a form.</summary>
    internal static Result Capture(Database db)
    {
        const string scope = "Current drawing Model Space LWPOLYLINE only; excludes Xrefs and other curve types";
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name?.StartsWith("MahodNet", StringComparison.OrdinalIgnoreCase) == true
            && a.GetType("MahodNet.MHAutocad.CalcPolylines") != null);
        var identity = assembly?.FullName ?? "not loaded";
        try
        {
            var type = assembly?.GetType("MahodNet.MHAutocad.CalcPolylines") ?? throw new InvalidOperationException("MAHOD CalcPolylines assembly is not loaded");
            var method = type.GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Single(m => m.Name == "FillLayerInfoList" && m.GetParameters().Length == 4);
            var dictionary = (IDictionary)Activator.CreateInstance(method.GetParameters()[1].ParameterType)!;
            using var tr = db.TransactionManager.StartTransaction();
            var layers = new Dictionary<string, string>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var model = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            var native = new Dictionary<string, (int Count, double Length)>();
            foreach (ObjectId id in model)
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not Polyline pl) continue;
                layers[pl.Layer] = pl.Layer;
                var before = native.GetValueOrDefault(pl.Layer);
                native[pl.Layer] = (before.Count + 1, before.Length + pl.Length);
            }
            // The original SelectAll includes paperspace. Reject that case instead of comparing unequal scopes.
            foreach (ObjectId block in bt)
            {
                var record = (BlockTableRecord)tr.GetObject(block, OpenMode.ForRead);
                if (!record.IsLayout || block == model.ObjectId) continue;
                foreach (ObjectId id in record)
                    if (tr.GetObject(id, OpenMode.ForRead) is Polyline pl && layers.ContainsKey(pl.Layer))
                        throw new InvalidOperationException("Paperspace polylines share Model Space layers; original DLL SelectAll has a different scope");
            }
            method.Invoke(null, new object[] { tr, dictionary, "LWPOLYLINE", layers });
            var comparisons = new List<Comparison>();
            foreach (var layer in native)
            {
                var item = dictionary[layer.Key]; var info = item?.GetType();
                var count = item == null ? 0 : (int)info!.GetField("Count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!;
                var length = item == null ? 0 : (double)info!.GetField("Length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!;
                comparisons.Add(new(layer.Key, layer.Value.Count, layer.Value.Length, count, length,
                    count == layer.Value.Count && Math.Abs(length - layer.Value.Length) <= Math.Max(1e-8, layer.Value.Length * 1e-10)));
            }
            return new(comparisons.Count == 0 ? "no-comparable-lines" : comparisons.All(c => c.Match) ? "matched" : "mismatch", identity, scope, comparisons, null);
        }
        catch (System.Exception ex) { return new("unavailable", identity, scope, Array.Empty<Comparison>(), ex.GetBaseException().Message); }
    }
}
