using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Contracts;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

/// <summary>Read-only native proof for the shared automatic-arrow inventory.</summary>
internal static class SectionTrafficStraightScopeService
{
    internal static SectionTrafficStraightScopeLogic.Resolution Filter(
        SectionPlanRecord record, CivilDocument civilDoc, Transaction tr,
        IReadOnlyList<TrafficDirectionEvidenceLogic.ArrowEvidence> arrows)
    {
        string? alignmentHandle = null;
        SectionTrafficStraightScopeLogic.Resolution Finish(SectionTrafficStraightScopeLogic.Resolution scope) =>
            scope with { CanonicalEvidence = SectionTrafficStraightScopeLogic.CanonicalEvidenceFor(
                record.SelectedAlignment, alignmentHandle, scope) };
        try
        {
            CivilDb.Alignment? selected = null;
            foreach (ObjectId id in civilDoc.GetAlignmentIds())
            {
                if (tr.GetObject(id, OpenMode.ForRead) is not CivilDb.Alignment alignment)
                    return Finish(SectionTrafficStraightScopeLogic.Refuse("unreadable-native-alignment-inventory"));
                if (!string.Equals(alignment.Name, record.SelectedAlignment, StringComparison.OrdinalIgnoreCase)) continue;
                if (selected != null)
                    return Finish(SectionTrafficStraightScopeLogic.Refuse("ambiguous-native-alignment-identity"));
                selected = alignment;
            }
            if (selected == null || record.SelectedCrossing == null || !SectionCutGeometry.TryFrame(record, out var frame))
                return Finish(SectionTrafficStraightScopeLogic.Refuse("missing-native-alignment-scope"));

            alignmentHandle = selected.Handle.ToString();
            var segments = new List<SectionTrafficStraightScopeLogic.Segment>();
            var entities = selected.Entities;
            for (var i = 0; i < entities.Count; i++)
            {
                // Path order excludes disconnected construction entities. A failed
                // read refuses the entire scope; partial geometry is not proof.
                var entity = entities.GetEntityByOrder(i);
                if (entity is not CivilDb.AlignmentLine line) continue;
                var start = line.StartPoint;
                var end = line.EndPoint;
                segments.Add(new(new(start.X, start.Y), new(end.X, end.Y),
                    selected.Handle + "/" + line.EntityId.ToString(CultureInfo.InvariantCulture)));
            }
            // Endpoints are exact native geometry. Do not use rounded PLAN station,
            // station equations, or StationHelper's catch-and-return-input fallback.
            return Finish(SectionTrafficStraightScopeLogic.Resolve(frame!,
                record.SelectedCrossing.TangentDeg * Math.PI / 180, segments, arrows));
        }
        catch (Exception ex)
        {
            return Finish(SectionTrafficStraightScopeLogic.Refuse("native-straight-scope-unreadable:" + ex.GetType().Name));
        }
    }
}
