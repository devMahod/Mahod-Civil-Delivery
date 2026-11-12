using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;
using Feature = MahodAI.CivilDelivery.Shared.TypicalPlanVision.Feature;
using Sample = MahodAI.CivilDelivery.Shared.TypicalPlanVision.Sample;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;

internal sealed partial class TypicalSectionPicker
{
    /// <summary>Read the entire drawing first. A single write transaction owns all new markers.</summary>
    internal void Scan(CivilDocument civil, double width, TypicalSectionPickResult result, string? preferredCorridor,
        Func<string, IReadOnlyList<string>, string?> chooseCorridor)
    {
        if (AlignmentNames.Count == 0)
        {
            result.Error = "לא נמצא ציר קריא התואם לפרופיל. יש לבדוק את שכבות הצירים ואת קובצי ה־XREF.";
            return;
        }
        var image = DraftedTypicalSectionService.ReadVisionPlan(_read, _db, ScanBounds(width));
        foreach (ObjectId id in civil.GetAlignmentIds())
            if (_read.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment { AlignmentType: CivilDb.AlignmentType.Rail } rail && rail.Length > 0)
                AddTrack(image, rail);
        var taken = ExistingMarkerMidpoints();
        var proposals = new List<(PickResolution Position, bool FromPlan, TypicalPlanVision.Stretch Span)>();
        int duplicates = 0;

        void Evaluate(string name, IEnumerable<(double Station, IReadOnlyList<Feature> Model, string Assembly)> observations,
            Func<double, (P2 Axis, double Tangent)> locate, bool fromPlan)
        {
            var samples = new List<Sample>();
            foreach (var observation in observations)
            {
                if (HostApplicationServices.Current.UserBreak()) throw new OperationCanceledException();
                try
                {
                    var (point, angle) = locate(observation.Station);
                    var visual = image.Read(point, angle, width);
                    var features = TypicalPlanVision.CombineWithModel(visual, observation.Model);
                    samples.Add(new Sample(observation.Station, features, observation.Assembly));
                }
                catch (Autodesk.Civil.PointNotOnEntityException)
                {
                    samples.Add(new Sample(observation.Station, Array.Empty<Feature>()));
                }
            }
            var stretches = TypicalPlanVision.Select(samples, modelRegion: !fromPlan);
            if (stretches.Count == 0)
                result.Notes.Add($"{name}: לא נמצא מקטע עם נתוני חתך יציבים.");
            else if (samples.Any(s => !s.HasEvidence))
                result.Notes.Add($"{name}: בחלק מהתוואי חסרים קווי כביש או מסילה קריאים.");
            foreach (var span in stretches)
            {
                var station = span.Representative.Station;
                var (point, angle) = locate(station);
                if (TypicalSectionPickLogic.TooClose(taken, point)) { duplicates++; continue; }
                var (a, b) = TypicalSectionPickLogic.Endpoints(point, angle, width);
                proposals.Add((new PickResolution(TypicalSectionPickLogic.PickState.Ready, null, name, station,
                    0, point, angle, a, b), fromPlan, span));
                taken.Add(point);
            }
        }

        foreach (var axis in _planAxes)
            Evaluate(axis.Name, PlanStations(axis.StartStation, axis.Length),
                s => (axis.PointAt(s, 0), axis.TangentDegAt(s)), true);

        foreach (var alignment in _alignments)
        {
            (P2, double) Locate(double station)
            {
                double x = 0, y = 0;
                alignment.PointLocation(station, 0, ref x, ref y);
                return (new P2(x, y), AlignmentCandidateResolver.TangentAt(alignment, station));
            }
            if (!_corridorStations.ContainsKey(alignment.ObjectId))
            {
                Evaluate(alignment.Name, PlanStations(alignment.StartingStation, alignment.EndingStation - alignment.StartingStation), Locate, true);
                continue;
            }
            var corridors = TypicalSectionsService.CorridorsOn(_read, civil, alignment.Name);
            var candidates = corridors.Select(c => c.Candidate).Where(c => c.Regions.Count > 0).ToList();
            if (candidates.Count == 0)
            {
                result.Notes.Add($"{alignment.Name}: לקורידור אין אזורים מחושבים.");
                continue;
            }
            var choice = TypicalCorridorChoiceLogic.Choose(candidates, Array.Empty<double>(), preferredCorridor, null);
            var name = choice.Chosen ?? chooseCorridor(alignment.Name, choice.AskAmong);
            if (name == null) throw new OperationCanceledException();
            result.Corridors[alignment.Name] = name;
            var selected = corridors.First(c => c.Name == name);
            var corridor = (CivilDb.Corridor)_read.GetObject(selected.Id, OpenMode.ForRead);
            foreach (CivilDb.Baseline baseline in corridor.Baselines)
                if (baseline.AlignmentId == alignment.ObjectId)
                    foreach (var run in ModelRuns(baseline, alignment.Name, width, result.Notes))
                        Evaluate(alignment.Name, run, Locate, false);
        }
        if (duplicates > 0) result.Notes.Add($"{duplicates} תחנות מייצגות כבר מסומנות בשרטוט.");
        if (proposals.Count == 0)
        {
            result.Error = duplicates > 0 ? "כל התחנות שזוהו כבר מסומנות." : "אין די נתונים לבחירת חתכים טיפוסיים.";
            return;
        }

        var marked = new List<TypicalSectionPick>();
        using (var tr = _db.TransactionManager.StartTransaction())
        {
            foreach (var proposal in proposals)
            {
                if (HostApplicationServices.Current.UserBreak()) throw new OperationCanceledException();
                var marker = AddMarker(tr, proposal.Position, width, proposal.FromPlan);
                var supplemental = proposal.Span.Representative.Features
                    .Where(f => f.Kind is TypicalPlanVision.CivilTrack or TypicalPlanVision.InferredLaneMark)
                    .Select(f => new DraftedTypicalSectionLogic.Crossing(f.Offset,
                        f.Kind == TypicalPlanVision.CivilTrack ? DraftedTypicalSectionLogic.Kind.Track : DraftedTypicalSectionLogic.Kind.Marking,
                        f.Kind, f.Stage == 0 ? "EXIST" : f.Stage > 10 ? $"TT-S{f.Stage - 10}" : "host")).ToList();
                marked.Add(marker.Pick with { ScanFrom = proposal.Span.From, ScanTo = proposal.Span.To, VisualCrossings = supplemental });
            }
            tr.Commit();
        }
        result.Picks.AddRange(marked);
    }

    private Extents3d? ScanBounds(double width)
    {
        var bounds = new Extents3d();
        try
        {
            foreach (var alignment in _alignments) bounds.AddExtents(alignment.GeometricExtents);
            foreach (var axis in _planAxes)
                foreach (var v in axis.Vertices) bounds.AddPoint(new Autodesk.AutoCAD.Geometry.Point3d(v.X, v.Y, 0));
        }
        catch (Autodesk.AutoCAD.Runtime.Exception) { return null; }
        // Includes the longitudinal neighbourhood used to read dashed lines, at any axis bearing.
        var margin = new Autodesk.AutoCAD.Geometry.Vector3d(width + DraftedTypicalSectionService.DashReachM, width + DraftedTypicalSectionService.DashReachM, 0);
        return new Extents3d(bounds.MinPoint - margin, bounds.MaxPoint + margin);
    }

    private static void AddTrack(TypicalPlanVision image, CivilDb.Alignment rail)
    {
        int intervals = Math.Max(1, (int)Math.Ceiling(rail.Length / 2));
        P2? previous = null;
        for (var i = 0; i <= intervals; i++)
        {
            if (i % 256 == 0 && HostApplicationServices.Current.UserBreak()) throw new OperationCanceledException();
            var station = rail.StartingStation + (rail.EndingStation - rail.StartingStation) * i / intervals;
            double x = 0, y = 0;
            try { rail.PointLocation(station, 0, ref x, ref y); }
            catch (Autodesk.Civil.PointNotOnEntityException) { previous = null; continue; }
            var point = new P2(x, y);
            if (previous.HasValue) image.Add(previous.Value, point, TypicalPlanVision.CivilTrack, 1);
            previous = point;
        }
    }

    // Adjacent regions often split only the sampling frequency. Keep their samples together
    // while the assembly is the same and their station ranges meet.
    private static IEnumerable<List<(double Station, IReadOnlyList<Feature> Model, string Assembly)>> ModelRuns(
        CivilDb.Baseline baseline, string alignment, double width, List<string> notes)
    {
        var run = new List<(double Station, IReadOnlyList<Feature> Model, string Assembly)>();
        double previousEnd = double.NaN;
        foreach (var region in baseline.BaselineRegions.Cast<CivilDb.BaselineRegion>().OrderBy(r => r.StartStation))
        {
            var model = ReadAppliedSections(region, width);
            if (run.Count > 0 && (model.Count == 0 || run[^1].Assembly != model[0].Assembly ||
                Math.Abs(previousEnd - region.StartStation) > 0.01))
            {
                yield return run;
                run = new();
            }
            if (model.Count == 0)
                notes.Add(FormattableString.Invariant($"{alignment} {region.StartStation:0.##}–{region.EndStation:0.##}: אין חתכי קורידור מחושבים."));
            else
            {
                if (run.Count > 0 && Math.Abs(run[^1].Station - model[0].Station) < 0.001) run.RemoveAt(run.Count - 1);
                run.AddRange(model);
            }
            previousEnd = region.EndStation;
        }
        if (run.Count > 0) yield return run;
    }

    // Cell centres keep stations inside their extent, including on curves and nonzero stationing.
    private static IEnumerable<(double, IReadOnlyList<Feature>, string)> PlanStations(double start, double length)
    {
        if (!(length > 0) || !double.IsFinite(length)) yield break;
        var count = Math.Max(3, (int)Math.Ceiling(length / 5));
        for (var i = 0; i < count; i++)
            yield return (start + (i + 0.5) * length / count, Array.Empty<Feature>(), "");
    }

    private static List<(double Station, IReadOnlyList<Feature> Model, string Assembly)> ReadAppliedSections(CivilDb.BaselineRegion region, double width)
    {
        var result = new SortedDictionary<double, IReadOnlyList<Feature>>();
        foreach (CivilDb.AppliedAssembly applied in region.AppliedAssemblies)
        {
            if (HostApplicationServices.Current.UserBreak()) throw new OperationCanceledException();
            var points = applied.Points.Cast<CivilDb.CalculatedPoint>().ToList();
            if (points.Count == 0) continue;
            var station = points[0].StationOffsetElevationToBaseline.X;
            if (!double.IsFinite(station)) continue;
            // Coded points retain the order and offsets of lane, shoulder, track and platform
            // edges even where a region uses one assembly with changing width targets.
            var features = points.SelectMany(p =>
                {
                    var offset = p.StationOffsetElevationToBaseline.Y;
                    return p.CorridorCodes.Where(TypicalPlanVision.IsSectionPointCode)
                        .Select(code => new Feature(-1, code.ToUpperInvariant(), Math.Round(offset, 3)));
                })
                .Where(f => double.IsFinite(f.Offset) && (f.Kind == "TOP" || Math.Abs(f.Offset) <= width))
                .Distinct().OrderBy(f => f.Kind, StringComparer.Ordinal).ThenBy(f => f.Offset).ToList();
            result[station] = features;
        }
        var assembly = region.AssemblyId.IsNull ? "" : region.AssemblyId.Handle.ToString();
        return result.Select(p => (p.Key, p.Value, assembly)).ToList();
    }
}
