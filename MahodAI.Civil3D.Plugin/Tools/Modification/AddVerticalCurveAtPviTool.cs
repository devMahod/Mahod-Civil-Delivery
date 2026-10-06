using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Adds a vertical curve at an interior PVI in a layout profile.
    ///
    /// Used by the fix pipeline for ``grade_difference`` violations where two
    /// adjacent tangents meet with a Δgrade above the standard's break
    /// threshold without a vertical curve. Refuses gracefully (success=false
    /// with a Hebrew message) when the PVI already has a curve, sits at the
    /// profile boundary, or the profile is read-only — never throws into the
    /// fix runner.
    /// </summary>
    public class AddVerticalCurveAtPviTool : DrawingToolBase
    {
        public override string Name => "add_vertical_curve_at_pvi";
        public override string Description => "Adds a vertical curve at a PVI in a layout profile. Refuses if the PVI already has a curve, sits at a profile boundary, or the profile is read-only.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var profileName = GetRequiredStringParam(parameters, "profile_name");
            var alignmentName = GetStringParam(parameters, "alignment_name");
            var pviIndex = GetIntParam(parameters, "pvi_index");
            var newLength = GetDoubleParam(parameters, "new_length");

            if (pviIndex == null)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'pvi_index' is missing");
            if (newLength == null || newLength.Value <= 0)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, "Required parameter 'new_length' must be a positive number");

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            var profileId = ObjectFinder.FindProfile(civilDoc, tr, profileName, alignmentName);
            if (profileId == null)
                return ToolResult.NotFound("Profile", profileName);

            var profile = tr.GetObject(profileId.Value, OpenMode.ForWrite) as CivilDb.Profile;
            if (profile == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Failed to open profile for write");

            if (!ProfileTypeGuard.IsModifiable(profile.ProfileType))
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"פרופיל '{profileName}' מסוג {profile.ProfileType} אינו ניתן לעריכה");

            // Collect PVIs to validate the index and check boundary position.
            var pviList = new List<(double Station, double Elevation)>();
            foreach (CivilDb.ProfilePVI pvi in profile.PVIs)
                pviList.Add((pvi.RawStation, pvi.Elevation));

            int idx = pviIndex.Value;
            if (idx < 0 || idx >= pviList.Count)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"PVI index {idx} out of range (0-{pviList.Count - 1})");

            // Boundary PVIs (first / last) have no incoming or outgoing tangent
            // and cannot host a vertical curve.
            if (idx == 0 || idx == pviList.Count - 1)
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                    $"PVI ב-index {idx} נמצא בקצה הפרופיל — לא ניתן להוסיף עקומה אנכית");

            double pviStation = pviList[idx].Station;
            double pviElev = pviList[idx].Elevation;

            // Detect an existing vertical curve at this PVI by walking the
            // entity collection and checking whether any non-Tangent entity
            // straddles the PVI station.
            foreach (CivilDb.ProfileEntity ent in profile.Entities)
            {
                var etypeName = ent.EntityType.ToString();
                if (etypeName == "Tangent")
                    continue;
                if (ent.StartStation <= pviStation + 0.01 && ent.EndStation >= pviStation - 0.01)
                {
                    return ToolResult.Fail(ToolErrorCodes.InvalidParameters,
                        $"קיימת עקומה אנכית ({etypeName}) ב-PVI index {idx} — השתמש ב-modify_profile_vertical_curve כדי לשנות אורך/K");
                }
            }

            // Compute Δgrade across the PVI so we can derive K = L / |Δg|
            // for the result payload. The adjacent PVIs are guaranteed to
            // exist because we ruled out boundary PVIs above.
            var prev = pviList[idx - 1];
            var next = pviList[idx + 1];
            double gradeIn = (pviElev - prev.Elevation) / (pviStation - prev.Station) * 100.0;
            double gradeOut = (next.Elevation - pviElev) / (next.Station - pviStation) * 100.0;
            double deltaGrade = gradeOut - gradeIn;

            double appliedLength = newLength.Value;
            string? methodUsed = null;
            Exception? lastException = null;

            // Try the primary API: AddFreeCircularCurveByPVIAndLength.
            try
            {
                var entitiesObj = profile.Entities;
                var entitiesType = entitiesObj.GetType();
                var addCircular = entitiesType.GetMethod(
                    "AddFreeCircularCurveByPVIAndLength",
                    new[] { typeof(double), typeof(double) });
                if (addCircular != null)
                {
                    addCircular.Invoke(entitiesObj, new object[] { pviStation, appliedLength });
                    methodUsed = "AddFreeCircularCurveByPVIAndLength";
                }
            }
            catch (TargetInvocationException tie)
            {
                lastException = tie.InnerException ?? tie;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }

            // Fallback: parabolic curve (some profile types only accept that).
            if (methodUsed == null)
            {
                try
                {
                    var entitiesObj = profile.Entities;
                    var entitiesType = entitiesObj.GetType();
                    var addParabolic = entitiesType.GetMethod(
                        "AddFreeVerticalCurveByPVIAndLength",
                        new[] { typeof(double), typeof(double) });
                    if (addParabolic != null)
                    {
                        addParabolic.Invoke(entitiesObj, new object[] { pviStation, appliedLength });
                        methodUsed = "AddFreeVerticalCurveByPVIAndLength";
                    }
                }
                catch (TargetInvocationException tie)
                {
                    lastException = tie.InnerException ?? tie;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }
            }

            if (methodUsed == null)
            {
                var msg = lastException != null
                    ? $"לא ניתן להוסיף עקומה אנכית: {lastException.Message}"
                    : "Civil 3D API לא חושף שיטה להוספת עקומה אנכית ב-PVI";
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, msg);
            }

            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_profile_elevation_at_station:");
            cache.RemoveByPattern("validate_profile:");

            var resolvedAlignmentName = alignmentName ?? ObjectFinder.FindAlignmentNameForProfile(civilDoc, tr, profileName);
            double? kValue = Math.Abs(deltaGrade) > 0.0001
                ? appliedLength / Math.Abs(deltaGrade)
                : (double?)null;

            return await Task.FromResult(ToolResult.Ok(new
            {
                profile_name = profileName,
                alignment_name = resolvedAlignmentName,
                pvi_index = idx,
                station = Math.Round(pviStation, 3),
                applied_length = Math.Round(appliedLength, 3),
                grade_in = Math.Round(gradeIn, 3),
                grade_out = Math.Round(gradeOut, 3),
                delta_grade = Math.Round(deltaGrade, 3),
                k_value = kValue.HasValue ? Math.Round(kValue.Value, 3) : (double?)null,
                method_used = methodUsed
            }));
        }
    }
}
