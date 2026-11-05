using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// Road volumes the Civil 3D model already knows: corridor shape areas per code (Pave1, Base, SubBase…) integrated
/// along the stations by average end area, and cut/fill from sample-line sections — the same engine the guided
/// estimate uses (<see cref="CorridorQuantityService"/>). Read only; the rows join the BOQ as m³ groups.
/// </summary>
internal static class RoadCorridorVolumes
{
    internal const string Part = "קורידור";

    internal static int CorridorCount(Document doc)
    {
        try { return CivilApplication.ActiveDocument?.CorridorCollection?.Count ?? 0; }
        catch (Exception) { return 0; }
    }

    internal static (IReadOnlyList<InfrastructureGroup> Groups, IReadOnlyList<string> Findings) Read(Document doc, string drawingHash)
    {
        var findings = new List<string>();
        var civil = CivilApplication.ActiveDocument ?? throw new InvalidOperationException("אין מסמך Civil 3D פעיל");
        CorridorQuantityService.Outcome outcome;
        using (doc.LockDocument())
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            outcome = CorridorQuantityService.Collect(tr, doc.Database, civil, new ProjectProfile(), "boq-roads-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                doc.Name, drawingHash, null);
            tr.Commit();
        }
        findings.AddRange(outcome.Findings.Select(f => f.Title + (string.IsNullOrWhiteSpace(f.Message) ? "" : " — " + f.Message)));
        var source = "קורידורים — " + Path.GetFileName(doc.Name);
        var groups = outcome.Records
            .Where(r => r.Measurement.Kind == "volume" && r.Measurement.Unit == "מ\"ק" && double.IsFinite(r.Measurement.RawValue) && r.Measurement.RawValue > 0)
            .GroupBy(r => r.Source.Layer)
            .Select(g =>
            {
                var earth = g.Key.StartsWith("earthworks:", StringComparison.OrdinalIgnoreCase);
                var code = g.Key[(g.Key.IndexOf(':') + 1)..];
                var corridors = string.Join(", ", g.Select(r => r.Source.CivilIdentity).Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(4));
                return new InfrastructureGroup
                {
                    Source = source, Layer = g.Key, Count = g.Count(), LengthMetres = 0,
                    Topic = earth ? "עבודות עפר" : "כבישים ואספלט",
                    VolumeCubicMetres = g.Sum(r => r.Measurement.RawValue),
                    GroupPart = Part, MeasurementUnit = "מ״ק",
                    Evidence = earth ? $"חפירה/מילוי מחתכי Sample Lines (ממוצע שטחי קצה) · {code}"
                        : $"קוד מבנה {code} מקורידור{(corridors.Length > 0 ? " " + corridors : "")} · ממוצע שטחי קצה",
                    Element = earth ? (code.Contains("cut", StringComparison.OrdinalIgnoreCase) ? "חפירה" : "מילוי") : code,
                };
            })
            .OrderBy(g => g.Topic).ThenByDescending(g => g.VolumeCubicMetres)
            .ToArray();
        return (groups, findings);
    }
}
