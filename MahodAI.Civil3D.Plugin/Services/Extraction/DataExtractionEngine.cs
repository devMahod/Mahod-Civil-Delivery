using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers;
using MahodAI.Civil3D.Plugin.Services.Extraction.Extractors;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;
using LayerInfo = MahodAI.Civil3D.Plugin.Services.Extraction.Models.LayerInfo;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MahodAI.Civil3D.Plugin.Services.Extraction
{
    /// <summary>
    /// Central engine for extracting and analyzing Civil 3D data.
    /// Orchestrates extractors (data collection) and analyzers (data processing).
    /// Produces a single unified JSON output.
    /// </summary>
    public class DataExtractionEngine
    {
        private readonly List<IDataExtractor> _extractors;
        private readonly List<IAnalyzer> _analyzers;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public string? LastError { get; private set; }

        public DataExtractionEngine()
        {
            // Register extractors (data collection)
            _extractors = new List<IDataExtractor>
            {
                new AlignmentExtractor(),
                new GeometryExtractor(),
                new SignMarkingExtractor()
            };
            _extractors = _extractors.OrderBy(e => e.Priority).ToList();

            // Register analyzers (statistics only - compliance analysis done by AI)
            _analyzers = new List<IAnalyzer>
            {
                new SummaryAnalyzer()
            };
            _analyzers = _analyzers.OrderBy(a => a.Priority).ToList();
        }

        public void RegisterExtractor(IDataExtractor extractor)
        {
            _extractors.Add(extractor);
            _extractors.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public void RegisterAnalyzer(IAnalyzer analyzer)
        {
            _analyzers.Add(analyzer);
            _analyzers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        /// <summary>
        /// Extract all data from active drawing and run all analyzers
        /// </summary>
        public DrawingDataModel? ExtractFull(CancellationToken ct = default)
        {
            LastError = null;

            try
            {
                System.Diagnostics.Debug.WriteLine("DataExtractionEngine.ExtractFull: Starting...");

                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                {
                    LastError = "No active document";
                    return null;
                }

                System.Diagnostics.Debug.WriteLine($"DataExtractionEngine: Active document: {doc.Name}");

                var db = doc.Database;
                if (db == null)
                {
                    LastError = "Database is null";
                    return null;
                }

                var civilDoc = CivilApplication.ActiveDocument;

                // Create model with metadata
                var model = new DrawingDataModel
                {
                    FileName = Path.GetFileName(doc.Name),
                    FilePath = doc.Name,
                    ExtractedAt = DateTime.UtcNow
                };

                // Get file size
                try
                {
                    if (File.Exists(doc.Name))
                    {
                        var fi = new FileInfo(doc.Name);
                        model.FileSize = $"{fi.Length / 1024.0 / 1024.0:F2} MB";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] DataExtractionEngine get file size: {ex.Message}");
                }

                // Phase 1: Run extractors to collect data
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var extractor in _extractors)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (!extractor.IsAvailable(civilDoc))
                                continue;

                            System.Diagnostics.Debug.WriteLine($"Running extractor: {extractor.ObjectType}");
                            var data = extractor.ExtractAll(tr, civilDoc, db);
                            MergeExtractorData(model, extractor.ObjectType, data);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Extractor {extractor.ObjectType} error: {ex.Message}");
                        }
                    }

                    tr.Commit();
                }

                // Phase 2: Run analyzers to process data
                foreach (var analyzer in _analyzers)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"Running analyzer: {analyzer.Name}");
                        analyzer.Analyze(model);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Analyzer {analyzer.Name} error: {ex.Message}");
                    }
                }

                // Phase 3: Sanitize all values
                SanitizeModel(model);

                return model;
            }
            catch (Exception ex)
            {
                LastError = $"Exception: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"DataExtractionEngine.ExtractFull error: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Serialize model to JSON
        /// </summary>
        public string ToJson(DrawingDataModel model)
        {
            try
            {
                return JsonSerializer.Serialize(model, JsonOptions);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ToJson error: {ex.Message}");
                return "{}";
            }
        }

        private void MergeExtractorData(DrawingDataModel model, string objectType, object? data)
        {
            if (data == null) return;

            switch (objectType)
            {
                case "Alignment":
                    if (data is AlignmentExtractorResult alignResult)
                    {
                        model.Alignments = alignResult.Alignments;
                        model.AlignmentDetails = alignResult.AlignmentDetails;
                        model.Profiles = alignResult.Profiles;
                        model.SuperElevations = alignResult.SuperElevations;
                        model.AlignmentCount = alignResult.Alignments?.Count ?? 0;
                        model.ProfileCount = alignResult.Profiles?.Count ?? 0;
                    }
                    break;

                case "Geometry":
                    if (data is GeometryExtractorResult geomResult)
                    {
                        model.Units = geomResult.Units;
                        model.CoordinateSystem = geomResult.CoordinateSystem;
                        model.DwgVersion = geomResult.DwgVersion;
                        model.Layers = geomResult.Layers;
                        model.Blocks = geomResult.Blocks;
                        model.XRefs = geomResult.XRefs;
                        model.Extents = geomResult.Extents;
                        model.Geometry = geomResult.Geometry;
                        model.TotalEntityCount = geomResult.TotalEntityCount;
                        model.EntityTypeCounts = geomResult.EntityTypeCounts;
                        model.LayerCount = geomResult.LayerCount;
                        model.BlockCount = geomResult.BlockCount;
                        model.SurfaceCount = geomResult.SurfaceCount;
                        model.CorridorCount = geomResult.CorridorCount;
                        model.PipeNetworkCount = geomResult.PipeNetworkCount;
                        model.ParcelCount = geomResult.ParcelCount;
                    }
                    break;
            }
        }

        private static double Sanitize(double value)
        {
            if (double.IsInfinity(value) || double.IsNaN(value))
                return 0;
            return value;
        }

        private void SanitizeModel(DrawingDataModel model)
        {
            // Sanitize alignment data
            foreach (var a in model.Alignments ?? new())
            {
                a.Length = Sanitize(a.Length);
                a.MinRadius = Sanitize(a.MinRadius);
            }

            foreach (var ad in model.AlignmentDetails ?? new())
            {
                ad.Length = Sanitize(ad.Length);
                ad.MinRadius = Sanitize(ad.MinRadius);
                ad.StartStation = Sanitize(ad.StartStation);
                ad.EndStation = Sanitize(ad.EndStation);

                foreach (var curve in ad.HorizontalCurves ?? new())
                {
                    curve.Radius = Sanitize(curve.Radius);
                    curve.RadiusIn = Sanitize(curve.RadiusIn);
                    curve.RadiusOut = Sanitize(curve.RadiusOut);
                    curve.Length = Sanitize(curve.Length);
                    curve.StartStation = Sanitize(curve.StartStation);
                    curve.EndStation = Sanitize(curve.EndStation);
                }
            }

            // Sanitize profile data
            foreach (var p in model.Profiles ?? new())
            {
                p.MinGradePercent = Sanitize(p.MinGradePercent);
                p.MaxGradePercent = Sanitize(p.MaxGradePercent);

                foreach (var s in p.Segments ?? new())
                {
                    s.GradePercent = Sanitize(s.GradePercent);
                    s.Length = Sanitize(s.Length);
                    s.StartStation = Sanitize(s.StartStation);
                    s.EndStation = Sanitize(s.EndStation);
                }
            }

            // Sanitize summary statistics
            model.TotalAlignmentLength = Sanitize(model.TotalAlignmentLength);
            model.MinimumRadius = Sanitize(model.MinimumRadius);
            model.MaxGradePercent = Sanitize(model.MaxGradePercent);
            model.TotalCurveLength = Sanitize(model.TotalCurveLength);
            model.TotalSpiralLength = Sanitize(model.TotalSpiralLength);
            model.MinCurveRadius = Sanitize(model.MinCurveRadius);
            model.MaxCurveRadius = Sanitize(model.MaxCurveRadius);
            model.MinSpiralLength = Sanitize(model.MinSpiralLength);
            model.MaxSpiralLength = Sanitize(model.MaxSpiralLength);
        }
    }
}
