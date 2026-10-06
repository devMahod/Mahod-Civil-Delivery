using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;

namespace MahodAI.Civil3D.Plugin.Services.Extraction.Extractors
{
    /// <summary>
    /// Extracts road signs (from block references) and road markings (from polylines/text on marking layers).
    /// Signs are identified by block name patterns and layer conventions.
    /// Markings are identified by layer names and entity properties.
    /// </summary>
    public class SignMarkingExtractor : IDataExtractor
    {
        public string ObjectType => "SignsMarkings";
        public int Priority => 50;

        // Layer name patterns for sign identification
        private static readonly string[] SignLayerPatterns = new[]
        {
            "SIGN", "תמרור", "TRAFFIC", "TAMRUR", "T_SIGN", "ROAD_SIGN"
        };

        // Layer name patterns for marking identification
        private static readonly string[] MarkingLayerPatterns = new[]
        {
            "MARK", "סימון", "STRIPE", "LINE_MARK", "ROAD_MARK",
            "CROSSWALK", "מעבר", "ARROW", "חץ", "PAVEMENT"
        };

        // Block name patterns for signs
        private static readonly Regex SignBlockPattern = new(
            @"^(SIGN|T_|תמרור|TAMRUR|TRAFFIC)[\-_]?\d{0,4}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );

        // Israeli sign code to type mapping
        private static readonly Dictionary<string, (string type, string description)> IsraeliSignCodes = new()
        {
            // Regulatory
            { "101", ("regulatory", "עצור") },
            { "102", ("regulatory", "תן זכות קדימה") },
            { "201", ("regulatory", "מהירות מרבית 20") },
            { "202", ("regulatory", "מהירות מרבית 30") },
            { "203", ("regulatory", "מהירות מרבית 40") },
            { "204", ("regulatory", "מהירות מרבית 50") },
            { "205", ("regulatory", "מהירות מרבית 60") },
            { "206", ("regulatory", "מהירות מרבית 70") },
            { "207", ("regulatory", "מהירות מרבית 80") },
            { "208", ("regulatory", "מהירות מרבית 90") },
            { "209", ("regulatory", "מהירות מרבית 100") },
            { "210", ("regulatory", "מהירות מרבית 110") },
            { "211", ("regulatory", "מהירות מרבית 120") },
            { "301", ("prohibition", "אין כניסה") },
            { "302", ("prohibition", "אסור לפנות שמאלה") },
            { "303", ("prohibition", "אסור לפנות ימינה") },
            { "304", ("prohibition", "אסור לפנות") },
            { "305", ("prohibition", "אסור לעקוף") },
            
            // Warning
            { "401", ("warning", "עקומה מסוכנת") },
            { "402", ("warning", "עקומות מסוכנות") },
            { "403", ("warning", "ירידה תלולה") },
            { "404", ("warning", "הכביש מצטמצם") },
            { "405", ("warning", "צומת לפניך") },
            { "406", ("warning", "מעגל תנועה") },
            { "407", ("warning", "מעבר חצייה") },
            { "408", ("warning", "עבודות בדרך") },
            { "409", ("warning", "חצייה מסילת ברזל") },
            { "410", ("warning", "רמזור") },
            
            // Guide / Information
            { "501", ("guide", "שלט הכוונה") },
            { "601", ("information", "שלט מידע") },
            { "701", ("information", "מספר כביש") },
        };

        public bool IsAvailable(CivilDocument? civilDoc) => true;

        public object? ExtractAll(Transaction tr, CivilDocument? civilDoc, Database db)
        {
            var result = new SignMarkingExtractionResult();

            try
            {
                // Get all alignments for station calculation
                var alignments = GetAlignments(tr, civilDoc);

                // Extract signs from block references
                result.Signs = ExtractSigns(tr, db, alignments);
                result.TotalSignCount = result.Signs.Count;

                // Count by type
                result.SignsByType = result.Signs
                    .GroupBy(s => s.SignType)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Extract markings from polylines on marking layers
                result.Markings = ExtractMarkings(tr, db, alignments);
                result.TotalMarkingCount = result.Markings.Count;

                result.MarkingsByType = result.Markings
                    .GroupBy(m => m.MarkingType)
                    .ToDictionary(g => g.Key, g => g.Count());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SignMarkingExtractor error: {ex.Message}");
            }

            return result;
        }

        public object? ExtractByIds(Transaction tr, IEnumerable<ObjectId> ids) => null;

        #region Sign Extraction

        private List<RoadSignData> ExtractSigns(
            Transaction tr, Database db,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            var signs = new List<RoadSignData>();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead) is BlockReference blkRef))
                        continue;

                    string blockName = blkRef.Name ?? "";
                    string layerName = blkRef.Layer ?? "";

                    // Check if this is a sign block (by name or layer)
                    if (!IsSignBlock(blockName, layerName))
                        continue;

                    var sign = new RoadSignData
                    {
                        BlockName = blockName,
                        LayerName = layerName,
                        X = blkRef.Position.X,
                        Y = blkRef.Position.Y,
                        Rotation = blkRef.Rotation * (180.0 / Math.PI), // rad to deg
                    };

                    // Extract block attributes
                    ExtractSignAttributes(tr, blkRef, sign);

                    // Try to identify sign code from block name or attributes
                    IdentifySignCode(sign);

                    // Find nearest alignment and station
                    FindNearestAlignment(sign, alignments);

                    // Determine side (left/right relative to alignment)
                    DetermineSide(sign, alignments);

                    signs.Add(sign);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSigns error: {ex.Message}");
            }

            return signs.OrderBy(s => s.AlignmentName).ThenBy(s => s.Station).ToList();
        }

        private bool IsSignBlock(string blockName, string layerName)
        {
            // Check block name
            if (SignBlockPattern.IsMatch(blockName))
                return true;

            // Check layer
            string upperLayer = layerName.ToUpperInvariant();
            foreach (var pattern in SignLayerPatterns)
            {
                if (upperLayer.Contains(pattern.ToUpperInvariant()))
                    return true;
            }

            return false;
        }

        private void ExtractSignAttributes(Transaction tr, BlockReference blkRef, RoadSignData sign)
        {
            try
            {
                if (blkRef.AttributeCollection != null)
                {
                    foreach (ObjectId attrId in blkRef.AttributeCollection)
                    {
                        if (tr.GetObject(attrId, OpenMode.ForRead) is AttributeReference attr)
                        {
                            string tag = attr.Tag?.ToUpperInvariant() ?? "";
                            string value = attr.TextString ?? "";

                            sign.Attributes[tag] = value;

                            // Try to extract specific properties from attributes
                            if (tag.Contains("CODE") || tag.Contains("קוד") || tag.Contains("NUM"))
                                sign.SignCode = value;
                            else if (tag.Contains("HEIGHT") || tag.Contains("גובה"))
                                if (double.TryParse(value, out double h)) sign.Height = h;
                            else if (tag.Contains("SIZE") || tag.Contains("גודל"))
                                if (double.TryParse(value, out double s)) sign.Size = s;
                            else if (tag.Contains("DESC") || tag.Contains("תיאור"))
                                sign.Description = value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractSignAttributes error: {ex.Message}");
            }
        }

        private void IdentifySignCode(RoadSignData sign)
        {
            // Try to extract code from block name if not from attributes
            if (string.IsNullOrEmpty(sign.SignCode))
            {
                var match = Regex.Match(sign.BlockName, @"(\d{3,4})");
                if (match.Success)
                    sign.SignCode = match.Value;
            }

            // Look up code in Israeli sign database
            if (!string.IsNullOrEmpty(sign.SignCode) && IsraeliSignCodes.TryGetValue(sign.SignCode, out var info))
            {
                sign.SignType = info.type;
                if (string.IsNullOrEmpty(sign.Description))
                    sign.Description = info.description;
            }
        }

        private void FindNearestAlignment(
            RoadSignData sign,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            double minDist = double.MaxValue;

            foreach (var alignment in alignments)
            {
                try
                {
                    double station = 0, offset = 0;
                    alignment.StationOffset(sign.X, sign.Y, ref station, ref offset);

                    double dist = Math.Abs(offset);
                    if (dist < minDist && dist < 50) // Within 50m of alignment
                    {
                        minDist = dist;
                        sign.AlignmentName = alignment.Name;
                        sign.Station = station;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor find nearest alignment: {ex.Message}");
                }
            }
        }

        private void DetermineSide(
            RoadSignData sign,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            if (string.IsNullOrEmpty(sign.AlignmentName))
                return;

            var alignment = alignments.FirstOrDefault(a => a.Name == sign.AlignmentName);
            if (alignment == null)
                return;

            try
            {
                double station = 0, offset = 0;
                alignment.StationOffset(sign.X, sign.Y, ref station, ref offset);

                if (Math.Abs(offset) < 1.0)
                    sign.Side = "overhead"; // On the alignment itself
                else if (offset > 0)
                    sign.Side = "right";
                else
                    sign.Side = "left";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor determine side: {ex.Message}");
            }
        }

        #endregion

        #region Marking Extraction

        private List<RoadMarkingData> ExtractMarkings(
            Transaction tr, Database db,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            var markings = new List<RoadMarkingData>();

            try
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Autodesk.AutoCAD.DatabaseServices.Entity;
                    if (ent == null) continue;

                    string layerName = ent.Layer ?? "";
                    if (!IsMarkingLayer(layerName))
                        continue;

                    if (ent is Polyline pl)
                    {
                        var marking = ExtractPolylineMarking(pl, layerName, alignments);
                        if (marking != null)
                            markings.Add(marking);
                    }
                    else if (ent is Polyline2d pl2d)
                    {
                        // Handle 2D polylines similarly
                    }
                    else if (ent is Line line)
                    {
                        var marking = ExtractLineMarking(line, layerName, alignments);
                        if (marking != null)
                            markings.Add(marking);
                    }
                    else if (ent is BlockReference blkRef)
                    {
                        // Arrow markings, crosswalk blocks, etc.
                        var marking = ExtractBlockMarking(tr, blkRef, layerName, alignments);
                        if (marking != null)
                            markings.Add(marking);
                    }
                    else if (ent is DBText text)
                    {
                        var marking = ExtractTextMarking(text, layerName, alignments);
                        if (marking != null)
                            markings.Add(marking);
                    }
                    else if (ent is MText mtext)
                    {
                        var marking = ExtractMTextMarking(mtext, layerName, alignments);
                        if (marking != null)
                            markings.Add(marking);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ExtractMarkings error: {ex.Message}");
            }

            return markings.OrderBy(m => m.AlignmentName).ThenBy(m => m.StartStation).ToList();
        }

        private bool IsMarkingLayer(string layerName)
        {
            string upper = layerName.ToUpperInvariant();
            foreach (var pattern in MarkingLayerPatterns)
            {
                if (upper.Contains(pattern.ToUpperInvariant()))
                    return true;
            }
            return false;
        }

        private RoadMarkingData? ExtractPolylineMarking(
            Polyline pl, string layerName,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            try
            {
                if (pl.NumberOfVertices < 2) return null;

                var marking = new RoadMarkingData
                {
                    LayerName = layerName,
                    Width = pl.ConstantWidth * 100, // Convert to cm
                };

                // Classify marking type from layer name
                ClassifyMarkingType(marking, layerName);

                // Determine pattern from linetype
                ClassifyPattern(marking, pl.Linetype);

                // Determine color from entity/layer color
                ClassifyColor(marking, pl.ColorIndex, layerName);

                // Get start and end points for station lookup
                var startPt = pl.GetPoint3dAt(0);
                var endPt = pl.GetPoint3dAt(pl.NumberOfVertices - 1);

                // Find nearest alignment and stations
                FindMarkingAlignment(marking, startPt, endPt, alignments);

                return marking;
            }
            catch
            {
                return null;
            }
        }

        private RoadMarkingData? ExtractLineMarking(
            Line line, string layerName,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            try
            {
                var marking = new RoadMarkingData
                {
                    LayerName = layerName,
                };

                ClassifyMarkingType(marking, layerName);
                ClassifyPattern(marking, line.Linetype);
                ClassifyColor(marking, line.ColorIndex, layerName);

                var startPt = line.StartPoint;
                var endPt = line.EndPoint;
                FindMarkingAlignment(marking, startPt, endPt, alignments);

                return marking;
            }
            catch
            {
                return null;
            }
        }

        private RoadMarkingData? ExtractBlockMarking(
            Transaction tr, BlockReference blkRef, string layerName,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            try
            {
                string blockName = blkRef.Name?.ToUpperInvariant() ?? "";

                var marking = new RoadMarkingData
                {
                    LayerName = layerName,
                    StartStation = 0,
                    EndStation = 0,
                };

                // Identify arrow type
                if (blockName.Contains("ARROW") || blockName.Contains("חץ"))
                {
                    marking.MarkingType = "arrow";
                    if (blockName.Contains("LEFT") || blockName.Contains("שמאל"))
                        marking.ArrowType = "left";
                    else if (blockName.Contains("RIGHT") || blockName.Contains("ימין"))
                        marking.ArrowType = "right";
                    else if (blockName.Contains("UTURN") || blockName.Contains("פרסה"))
                        marking.ArrowType = "uturn";
                    else
                        marking.ArrowType = "straight";
                }
                else if (blockName.Contains("CROSS") || blockName.Contains("מעבר"))
                {
                    marking.MarkingType = "crosswalk";
                }
                else if (blockName.Contains("STOP"))
                {
                    marking.MarkingType = "stop_line";
                }
                else
                {
                    marking.MarkingType = "block_marking";
                }

                // Find station
                var pos = blkRef.Position;
                double minDist = double.MaxValue;
                foreach (var alignment in alignments)
                {
                    try
                    {
                        double station = 0, offset = 0;
                        alignment.StationOffset(pos.X, pos.Y, ref station, ref offset);
                        if (Math.Abs(offset) < minDist && Math.Abs(offset) < 30)
                        {
                            minDist = Math.Abs(offset);
                            marking.AlignmentName = alignment.Name;
                            marking.StartStation = station;
                            marking.EndStation = station;
                            marking.Side = offset > 0 ? "right" : offset < 0 ? "left" : "center";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor locate marking on alignment: {ex.Message}");
                    }
                }

                return marking;
            }
            catch
            {
                return null;
            }
        }

        private RoadMarkingData? ExtractTextMarking(
            DBText text, string layerName,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            try
            {
                var marking = new RoadMarkingData
                {
                    LayerName = layerName,
                    MarkingType = "text",
                    TextContent = text.TextString,
                };

                // Check if it's a speed marking (number on road)
                if (int.TryParse(text.TextString?.Trim(), out int speed) && speed >= 20 && speed <= 120)
                {
                    marking.MarkingType = "speed_marking";
                }

                var pos = text.Position;
                FindPointStation(marking, pos, alignments);

                return marking;
            }
            catch
            {
                return null;
            }
        }

        private RoadMarkingData? ExtractMTextMarking(
            MText mtext, string layerName,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            try
            {
                var marking = new RoadMarkingData
                {
                    LayerName = layerName,
                    MarkingType = "text",
                    TextContent = mtext.Text,
                };

                var pos = mtext.Location;
                FindPointStation(marking, pos, alignments);

                return marking;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Classification Helpers

        private void ClassifyMarkingType(RoadMarkingData marking, string layerName)
        {
            string upper = layerName.ToUpperInvariant();

            if (upper.Contains("CENTER") || upper.Contains("CL") || upper.Contains("מרכז"))
                marking.MarkingType = "center_line";
            else if (upper.Contains("EDGE") || upper.Contains("שפה") || upper.Contains("SHOULDER"))
                marking.MarkingType = "edge_line";
            else if (upper.Contains("LANE") || upper.Contains("נתיב"))
                marking.MarkingType = "lane_line";
            else if (upper.Contains("STOP") || upper.Contains("עצור"))
                marking.MarkingType = "stop_line";
            else if (upper.Contains("CROSS") || upper.Contains("מעבר") || upper.Contains("WALK"))
                marking.MarkingType = "crosswalk";
            else if (upper.Contains("ARROW") || upper.Contains("חץ"))
                marking.MarkingType = "arrow";
            else
                marking.MarkingType = "road_marking";
        }

        private void ClassifyPattern(RoadMarkingData marking, string? linetype)
        {
            if (string.IsNullOrEmpty(linetype))
            {
                marking.Pattern = "solid";
                return;
            }

            string upper = linetype.ToUpperInvariant();

            if (upper.Contains("DASH") && upper.Contains("DOUBLE"))
                marking.Pattern = "double_dashed";
            else if (upper.Contains("DOUBLE") || upper.Contains("CONT2"))
                marking.Pattern = "double_solid";
            else if (upper.Contains("DASH") || upper.Contains("HIDDEN") || upper.Contains("DOT"))
                marking.Pattern = "dashed";
            else
                marking.Pattern = "solid";
        }

        private void ClassifyColor(RoadMarkingData marking, int colorIndex, string layerName)
        {
            // AutoCAD color index: 2 = yellow, 7 = white
            if (colorIndex == 2 || colorIndex == 40 || colorIndex == 50)
                marking.Color = "yellow";
            else if (layerName.ToUpperInvariant().Contains("YELLOW") || layerName.Contains("צהוב"))
                marking.Color = "yellow";
            else
                marking.Color = "white";
        }

        #endregion

        #region Station Helpers

        private void FindMarkingAlignment(
            RoadMarkingData marking, Point3d startPt, Point3d endPt,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            double minDist = double.MaxValue;

            foreach (var alignment in alignments)
            {
                try
                {
                    double startStation = 0, startOffset = 0;
                    double endStation = 0, endOffset = 0;

                    alignment.StationOffset(startPt.X, startPt.Y, ref startStation, ref startOffset);
                    alignment.StationOffset(endPt.X, endPt.Y, ref endStation, ref endOffset);

                    double avgOffset = (Math.Abs(startOffset) + Math.Abs(endOffset)) / 2;

                    if (avgOffset < minDist && avgOffset < 30) // Within 30m of alignment
                    {
                        minDist = avgOffset;
                        marking.AlignmentName = alignment.Name;
                        marking.StartStation = Math.Min(startStation, endStation);
                        marking.EndStation = Math.Max(startStation, endStation);

                        double avgOff = (startOffset + endOffset) / 2;
                        if (Math.Abs(avgOff) < 1.0)
                            marking.Side = "center";
                        else
                            marking.Side = avgOff > 0 ? "right" : "left";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor find line station: {ex.Message}");
                }
            }
        }

        private void FindPointStation(
            RoadMarkingData marking, Point3d pos,
            List<Autodesk.Civil.DatabaseServices.Alignment> alignments)
        {
            double minDist = double.MaxValue;

            foreach (var alignment in alignments)
            {
                try
                {
                    double station = 0, offset = 0;
                    alignment.StationOffset(pos.X, pos.Y, ref station, ref offset);

                    if (Math.Abs(offset) < minDist && Math.Abs(offset) < 30)
                    {
                        minDist = Math.Abs(offset);
                        marking.AlignmentName = alignment.Name;
                        marking.StartStation = station;
                        marking.EndStation = station;
                        marking.Side = offset > 0 ? "right" : offset < 0 ? "left" : "center";
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor find point station: {ex.Message}");
                }
            }
        }

        private List<Autodesk.Civil.DatabaseServices.Alignment> GetAlignments(
            Transaction tr, CivilDocument? civilDoc)
        {
            var list = new List<Autodesk.Civil.DatabaseServices.Alignment>();

            if (civilDoc == null) return list;

            try
            {
                foreach (ObjectId id in civilDoc.GetAlignmentIds())
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is Autodesk.Civil.DatabaseServices.Alignment alignment)
                        list.Add(alignment);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] SignMarkingExtractor get alignments: {ex.Message}");
            }

            return list;
        }

        #endregion
    }
}
