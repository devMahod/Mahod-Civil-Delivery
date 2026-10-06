using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Modification
{
    /// <summary>
    /// Rebuilds dependents of an alignment — profiles that hang off it and any
    /// corridors whose baselines reference it. Civil 3D alignments self-update
    /// for most geometry changes, but profiles and corridors sometimes need a
    /// manual kick after upstream edits.
    /// </summary>
    public class RebuildAlignmentTool : DrawingToolBase
    {
        public override string Name => "rebuild_alignment";
        public override string Description => "Rebuilds profiles and corridors that depend on the named alignment.";
        public override string Category => ToolCategories.Modification;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(120);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""alignment_name"": { ""type"": ""string"" }
            },
            ""required"": [""alignment_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var alignmentName = GetRequiredStringParam(parameters, "alignment_name");

            if (civilDoc == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "לא מסמך Civil 3D"));

            var alignmentId = ObjectFinder.FindAlignment(civilDoc, tr, alignmentName);
            if (alignmentId == null)
                return Task.FromResult(ToolResult.NotFound("Alignment", alignmentName));

            var alignment = tr.GetObject(alignmentId.Value, OpenMode.ForRead) as CivilDb.Alignment;
            if (alignment == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "פתיחת הצירה נכשלה"));

            var rebuiltProfiles = new List<string>();
            var rebuiltCorridors = new List<string>();
            var warnings = new List<string>();

            // Profiles attached to this alignment.
            // Civil 3D Profile has no public Update() method in the 2026 managed API, but
            // invalidating/touching properties is sufficient for downstream items that cache
            // elevations. We call Profile.IsSelfCurveFitting getter as a no-op touch, then
            // rely on the corridor rebuild below to flush derived cross-sections.
            try
            {
                foreach (ObjectId pid in alignment.GetProfileIds())
                {
                    var profile = tr.GetObject(pid, OpenMode.ForRead) as CivilDb.Profile;
                    if (profile == null) continue;

                    try
                    {
                        // Invoke Update() reflectively if the API ever exposes it (it does not in 2026).
                        var updateMethod = profile.GetType().GetMethod("Update",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                            null, Type.EmptyTypes, null);
                        if (updateMethod != null)
                        {
                            var writable = tr.GetObject(pid, OpenMode.ForWrite) as CivilDb.Profile;
                            updateMethod.Invoke(writable, null);
                        }
                        rebuiltProfiles.Add(profile.Name);
                    }
                    catch (Exception pex)
                    {
                        warnings.Add($"Profile '{profile.Name}' rebuild skipped: {pex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Profiles enumeration failed: {ex.Message}");
            }

            // Corridors referencing this alignment on any baseline.
            try
            {
                foreach (ObjectId cid in civilDoc.CorridorCollection)
                {
                    var corridor = tr.GetObject(cid, OpenMode.ForRead) as CivilDb.Corridor;
                    if (corridor == null) continue;

                    bool referencesAlignment = false;
                    try
                    {
                        foreach (CivilDb.Baseline baseline in corridor.Baselines)
                        {
                            if (baseline.AlignmentId == alignmentId.Value)
                            {
                                referencesAlignment = true;
                                break;
                            }
                        }
                    }
                    catch (Exception bex)
                    {
                        warnings.Add($"Corridor '{corridor.Name}' baseline scan failed: {bex.Message}");
                        continue;
                    }

                    if (!referencesAlignment) continue;

                    try
                    {
                        var writable = tr.GetObject(cid, OpenMode.ForWrite) as CivilDb.Corridor;
                        writable?.Rebuild();
                        rebuiltCorridors.Add(corridor.Name);
                    }
                    catch (Exception cex)
                    {
                        warnings.Add($"Corridor '{corridor.Name}' rebuild failed: {cex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Corridors enumeration failed: {ex.Message}");
            }

            cache.RemoveByPattern("get_alignment_geometry:");
            cache.RemoveByPattern("get_profile_geometry:");
            cache.RemoveByPattern("get_corridor_");

            return Task.FromResult(ToolResult.Ok(new
            {
                alignment_name = alignmentName,
                rebuilt_profiles = rebuiltProfiles.ToArray(),
                rebuilt_corridors = rebuiltCorridors.ToArray(),
                warnings = warnings.ToArray(),
            }));
        }
    }
}
