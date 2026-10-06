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
    /// Gets a compact drawing summary optimized for LLM context (~2K lines).
    /// </summary>
    public class GetDrawingSummaryTool : DrawingToolBase
    {
        public override string Name => "get_drawing_summary";
        public override string Description => "Gets a compact summary of the drawing including object counts, coordinate bounds, and key statistics. Optimized for quick context gathering.";
        public override string Category => ToolCategories.Discovery;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(45);

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            var includeStatistics = GetBoolParam(parameters, "include_statistics", true);
            var includeObjectNames = GetBoolParam(parameters, "include_object_names", true);

            var db = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument?.Database
                ?? throw new InvalidOperationException("No active document");
            var summary = new DrawingSummary();

            // Get file metadata
            summary.Metadata = GetMetadata(db);

            // Get object counts and names
            if (civilDoc != null)
            {
                summary.Objects = GetObjectCounts(tr, civilDoc, includeObjectNames, ct);
            }

            // Get coordinate bounds
            summary.Bounds = GetDrawingBounds(tr, db, ct);

            // Get statistics
            if (includeStatistics && civilDoc != null)
            {
                summary.Statistics = GetStatistics(tr, civilDoc, ct);
            }

            return await Task.FromResult(ToolResult.Ok(summary));
        }

        private DrawingMetadata GetMetadata(Database db)
        {
            var metadata = new DrawingMetadata
            {
                FileName = System.IO.Path.GetFileName(db.Filename) ?? "untitled.dwg",
                FilePath = db.Filename,
                DrawingUnits = GetLinearUnits(db),
                LastSavedBy = GetDatabaseSummaryInfo(db, "LastSavedBy"),
                Title = GetDatabaseSummaryInfo(db, "Title")
            };

            return metadata;
        }

        private string GetLinearUnits(Database db)
        {
            try
            {
                return db.Lunits switch
                {
                    1 => "Scientific",
                    2 => "Decimal",
                    3 => "Engineering",
                    4 => "Architectural",
                    5 => "Fractional",
                    _ => $"Type_{db.Lunits}"
                };
            }
            catch
            {
                return null;
            }
        }

        private string? GetDatabaseSummaryInfo(Database db, string property)
        {
            try
            {
                var summaryInfo = db.SummaryInfo;
                return property switch
                {
                    "LastSavedBy" => summaryInfo.LastSavedBy,
                    "Title" => summaryInfo.Title,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private ObjectCounts GetObjectCounts(Transaction tr, CivilDocument civilDoc, bool includeNames, CancellationToken ct)
        {
            var counts = new ObjectCounts();

            // Alignments
            try
            {
                var alignmentIds = civilDoc.GetAlignmentIds();
                counts.AlignmentCount = alignmentIds.Count;
                if (includeNames && alignmentIds.Count <= 20)
                {
                    counts.AlignmentNames = new List<string>();
                    foreach (ObjectId id in alignmentIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                        if (obj != null) counts.AlignmentNames.Add(obj.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count alignments: {ex.Message}");
            }

            // Surfaces
            try
            {
                var surfaceIds = civilDoc.GetSurfaceIds();
                counts.SurfaceCount = surfaceIds.Count;
                if (includeNames && surfaceIds.Count <= 20)
                {
                    counts.SurfaceNames = new List<string>();
                    foreach (ObjectId id in surfaceIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Surface;
                        if (obj != null) counts.SurfaceNames.Add(obj.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count surfaces: {ex.Message}");
            }

            // Corridors
            try
            {
                counts.CorridorCount = civilDoc.CorridorCollection.Count;
                if (includeNames && counts.CorridorCount <= 20)
                {
                    counts.CorridorNames = new List<string>();
                    foreach (ObjectId id in civilDoc.CorridorCollection)
                    {
                        ct.ThrowIfCancellationRequested();
                        var obj = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Corridor;
                        if (obj != null) counts.CorridorNames.Add(obj.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count corridors: {ex.Message}");
            }

            // Pipe Networks
            try
            {
                var networkIds = civilDoc.GetPipeNetworkIds();
                counts.PipeNetworkCount = networkIds.Count;
                if (includeNames && networkIds.Count <= 20)
                {
                    counts.PipeNetworkNames = new List<string>();
                    foreach (ObjectId id in networkIds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var obj = tr.GetObject(id, OpenMode.ForRead) as Network;
                        if (obj != null) counts.PipeNetworkNames.Add(obj.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count pipe networks: {ex.Message}");
            }

            // Profiles (count total across all alignments)
            try
            {
                int profileCount = 0;
                foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                {
                    ct.ThrowIfCancellationRequested();
                    var alignment = tr.GetObject(alignmentId, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment != null)
                    {
                        profileCount += alignment.GetProfileIds().Count;
                    }
                }
                counts.ProfileCount = profileCount;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count profiles: {ex.Message}");
            }

            // Point Groups
            try
            {
                counts.PointGroupCount = civilDoc.PointGroups.Count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count point groups: {ex.Message}");
            }

            // Assemblies
            try
            {
                counts.AssemblyCount = civilDoc.AssemblyCollection.Count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary count assemblies: {ex.Message}");
            }

            return counts;
        }

        private DrawingBounds GetDrawingBounds(Transaction tr, Database db, CancellationToken ct)
        {
            var bounds = new DrawingBounds();

            try
            {
                var extMin = db.Extmin;
                var extMax = db.Extmax;

                bounds.MinX = extMin.X;
                bounds.MinY = extMin.Y;
                bounds.MinZ = extMin.Z;
                bounds.MaxX = extMax.X;
                bounds.MaxY = extMax.Y;
                bounds.MaxZ = extMax.Z;

                bounds.Width = extMax.X - extMin.X;
                bounds.Height = extMax.Y - extMin.Y;
                bounds.Depth = extMax.Z - extMin.Z;
            }
            catch
            {
                // Use defaults if bounds can't be determined
            }

            return bounds;
        }

        private DrawingStatistics GetStatistics(Transaction tr, CivilDocument civilDoc, CancellationToken ct)
        {
            var stats = new DrawingStatistics();

            // Surface statistics
            try
            {
                foreach (ObjectId id in civilDoc.GetSurfaceIds())
                {
                    ct.ThrowIfCancellationRequested();
                    var surface = tr.GetObject(id, OpenMode.ForRead) as TinSurface;
                    if (surface != null)
                    {
                        stats.TotalSurfacePoints += surface.Vertices.Count;
                        stats.TotalSurfaceTriangles += surface.Triangles.Count;

                        var gpi = surface.GetGeneralProperties();
                        if (gpi.MinimumElevation < stats.MinElevation || stats.MinElevation == 0)
                            stats.MinElevation = gpi.MinimumElevation;
                        if (gpi.MaximumElevation > stats.MaxElevation)
                            stats.MaxElevation = gpi.MaximumElevation;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary surface statistics: {ex.Message}");
            }

            // Alignment statistics
            try
            {
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    ct.ThrowIfCancellationRequested();
                    var alignment = tr.GetObject(id, OpenMode.ForRead) as Autodesk.Civil.DatabaseServices.Alignment;
                    if (alignment != null)
                    {
                        stats.TotalAlignmentLength += alignment.Length;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary alignment statistics: {ex.Message}");
            }

            // Pipe network statistics
            try
            {
                foreach (ObjectId id in civilDoc.GetPipeNetworkIds())
                {
                    ct.ThrowIfCancellationRequested();
                    var network = tr.GetObject(id, OpenMode.ForRead) as Network;
                    if (network != null)
                    {
                        stats.TotalPipes += network.GetPipeIds().Count;
                        stats.TotalStructures += network.GetStructureIds().Count;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] GetDrawingSummary pipe network statistics: {ex.Message}");
            }

            return stats;
        }
    }

    #region Result Models

    public class DrawingSummary
    {
        public DrawingMetadata? Metadata { get; set; }
        public ObjectCounts? Objects { get; set; }
        public DrawingBounds? Bounds { get; set; }
        public DrawingStatistics? Statistics { get; set; }
    }

    public class DrawingMetadata
    {
        public string FileName { get; set; } = string.Empty;
        public string? FilePath { get; set; }
        public string DrawingUnits { get; set; } = string.Empty;
        public string? LastSavedBy { get; set; }
        public string? Title { get; set; }
    }

    public class ObjectCounts
    {
        public int AlignmentCount { get; set; }
        public List<string>? AlignmentNames { get; set; }
        public int SurfaceCount { get; set; }
        public List<string>? SurfaceNames { get; set; }
        public int CorridorCount { get; set; }
        public List<string>? CorridorNames { get; set; }
        public int PipeNetworkCount { get; set; }
        public List<string>? PipeNetworkNames { get; set; }
        public int ProfileCount { get; set; }
        public int PointGroupCount { get; set; }
        public int AssemblyCount { get; set; }
    }

    public class DrawingBounds
    {
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MinZ { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }
        public double MaxZ { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Depth { get; set; }
    }

    public class DrawingStatistics
    {
        public int TotalSurfacePoints { get; set; }
        public int TotalSurfaceTriangles { get; set; }
        public double MinElevation { get; set; }
        public double MaxElevation { get; set; }
        public double TotalAlignmentLength { get; set; }
        public int TotalPipes { get; set; }
        public int TotalStructures { get; set; }
    }

    #endregion
}
