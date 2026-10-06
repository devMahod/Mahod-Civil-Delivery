using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Discovery
{
    /// <summary>
    /// Lists Civil 3D objects by type with names and basic info.
    /// </summary>
    public class ListObjectsTool : DrawingToolBase
    {
        public override string Name => "list_objects";
        public override string Description => "Lists Civil 3D objects by type (alignments, profiles, surfaces, corridors, pipe networks). Returns names, IDs, and counts.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var objectTypes = GetStringArrayParam(parameters, "object_types");
            var limit = GetIntParam(parameters, "limit") ?? 100;

            if (civilDoc == null)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");
            }

            var result = new ListObjectsResult();

            // If no types specified, list all
            var types = objectTypes ?? new[] { "alignments", "profiles", "surfaces", "corridors", "pipe_networks", "assemblies", "points" };

            foreach (var objType in types)
            {
                ct.ThrowIfCancellationRequested();

                switch (objType.ToLowerInvariant())
                {
                    case "alignments":
                    case "alignment":
                        result.Alignments = ListAlignments(tr, civilDoc, limit);
                        break;

                    case "profiles":
                    case "profile":
                        result.Profiles = ListProfiles(tr, civilDoc, limit);
                        break;

                    case "surfaces":
                    case "surface":
                        result.Surfaces = ListSurfaces(tr, civilDoc, limit);
                        break;

                    case "corridors":
                    case "corridor":
                        result.Corridors = ListCorridors(tr, civilDoc, limit);
                        break;

                    case "pipe_networks":
                    case "pipenetworks":
                    case "pipe_network":
                        result.PipeNetworks = ListPipeNetworks(tr, civilDoc, limit);
                        break;

                    case "assemblies":
                    case "assembly":
                        result.Assemblies = ListAssemblies(tr, civilDoc, limit);
                        break;

                    case "points":
                    case "point":
                    case "cogo_points":
                        result.PointGroups = ListPointGroups(tr, civilDoc, limit);
                        break;
                }
            }

            return await Task.FromResult(ToolResult.Ok(result));
        }

        private List<ObjectInfo> ListAlignments(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (obj != null)
                    {
                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "Alignment",
                            Description = obj.Description,
                            Properties = new Dictionary<string, object?>
                            {
                                ["length"] = obj.Length,
                                ["start_station"] = obj.StartingStation,
                                ["end_station"] = obj.EndingStation
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListAlignments error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListProfiles(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    if (list.Count >= limit) break;
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment != null)
                    {
                        foreach (ObjectId profileId in alignment.GetProfileIds())
                        {
                            if (list.Count >= limit) break;
                            var profile = tr.GetObject(profileId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Profile;
                            if (profile != null)
                            {
                                list.Add(new ObjectInfo
                                {
                                    Name = profile.Name,
                                    ObjectId = profileId.ToString(),
                                    Type = "Profile",
                                    Description = profile.Description,
                                    Properties = new Dictionary<string, object?>
                                    {
                                        ["alignment"] = alignment.Name,
                                        ["profile_type"] = profile.ProfileType.ToString()
                                    }
                                });
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListProfiles error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListSurfaces(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                    if (obj != null)
                    {
                        var props = new Dictionary<string, object?>
                        {
                            ["surface_type"] = obj.GetType().Name
                        };

                        if (obj is TinSurface tinSurface)
                        {
                            props["point_count"] = tinSurface.Vertices.Count;
                            props["triangle_count"] = tinSurface.Triangles.Count;
                        }

                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "Surface",
                            Description = obj.Description,
                            Properties = props
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListSurfaces error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListCorridors(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.CorridorCollection)
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                    if (obj != null)
                    {
                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "Corridor",
                            Description = obj.Description,
                            Properties = new Dictionary<string, object?>
                            {
                                ["baseline_count"] = obj.Baselines.Count
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListCorridors error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListPipeNetworks(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (obj != null)
                    {
                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "PipeNetwork",
                            Description = obj.Description,
                            Properties = new Dictionary<string, object?>
                            {
                                ["pipe_count"] = obj.GetPipeIds().Count,
                                ["structure_count"] = obj.GetStructureIds().Count
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListPipeNetworks error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListAssemblies(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.AssemblyCollection)
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as Assembly;
                    if (obj != null)
                    {
                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "Assembly",
                            Description = obj.Description,
                            Properties = new Dictionary<string, object?>
                            {
                                ["group_count"] = obj.Groups.Count
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListAssemblies error: {ex.Message}");
            }
            return list;
        }

        private List<ObjectInfo> ListPointGroups(Transaction tr, CivilDocument civilDoc, int limit)
        {
            var list = new List<ObjectInfo>();
            try
            {
                foreach (ObjectId id in civilDoc.PointGroups)
                {
                    if (list.Count >= limit) break;
                    var obj = tr.GetObject(id, OpenMode.ForRead) as PointGroup;
                    if (obj != null)
                    {
                        list.Add(new ObjectInfo
                        {
                            Name = obj.Name,
                            ObjectId = id.ToString(),
                            Type = "PointGroup",
                            Description = obj.Description,
                            Properties = new Dictionary<string, object?>
                            {
                                ["point_count"] = obj.PointsCount
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ListPointGroups error: {ex.Message}");
            }
            return list;
        }
    }

    #region Result Models

    public class ListObjectsResult
    {
        public List<ObjectInfo>? Alignments { get; set; }
        public List<ObjectInfo>? Profiles { get; set; }
        public List<ObjectInfo>? Surfaces { get; set; }
        public List<ObjectInfo>? Corridors { get; set; }
        public List<ObjectInfo>? PipeNetworks { get; set; }
        public List<ObjectInfo>? Assemblies { get; set; }
        public List<ObjectInfo>? PointGroups { get; set; }
    }

    public class ObjectInfo
    {
        public string Name { get; set; } = string.Empty;
        public string ObjectId { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string? Description { get; set; }
        public Dictionary<string, object?>? Properties { get; set; }
    }

    #endregion
}
