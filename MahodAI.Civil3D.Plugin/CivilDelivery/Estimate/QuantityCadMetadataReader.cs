using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using AcadColor = Autodesk.AutoCAD.Colors.Color;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    /// <summary>
    /// Reads optional source properties in the same transaction as measurement.
    /// Raw properties are not resolved through insertion ancestors and never change
    /// grouping, quantities, mappings or source identity.
    /// </summary>
    internal static class QuantityCadMetadataReader
    {
        /// <summary>b24: the host database of the scan and the units resolved for it (unit decisions included).</summary>
        internal sealed record HostUnits(Database Database, PhysicalDrawingUnitPolicy.Resolution Units);

        internal static IReadOnlyDictionary<string, string> Read(Entity entity, Transaction tr, HostUnits? host = null)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["evidence_role"] = "raw-properties-only; not used for grouping or pricing",
                ["geometry_space"] = "untransformed entity geometry",
            };
            void Capture(string field, Func<string> read)
            {
                try { values[field] = read(); }
                catch (Exception ex)
                {
                    values[field.EndsWith("_status", StringComparison.Ordinal) ? field : field + "_status"] =
                        "unavailable:" + ex.GetType().Name;
                }
            }
            void ColorFields(string prefix, AcadColor color)
            {
                values[prefix + "color_method"] = color.ColorMethod.ToString();
                values[prefix + "color_index"] = color.ColorIndex.ToString(CultureInfo.InvariantCulture);
                // RGB is meaningful here only when directly stored as a true color.
                if (color.ColorMethod == ColorMethod.ByColor)
                    values[prefix + "color_rgb"] = $"{color.Red},{color.Green},{color.Blue}";
            }

            Capture("entity_color_status", () =>
            {
                var color = entity.Color;
                ColorFields("entity_", color);
                return color.ColorMethod is ColorMethod.ByBlock or ColorMethod.ByLayer
                    ? "raw; effective inheritance unresolved"
                    : "explicit entity property";
            });
            Capture("entity_linetype", () => entity.Linetype);
            Capture("entity_linetype_scale", () => F(entity.LinetypeScale));
            Capture("entity_lineweight", () => entity.LineWeight.ToString());
            Capture("entity_database_insunits", () => entity.Database.Insunits.ToString());
            // b24: only an entity of the host database carries the host's resolved physical unit; an XREF keeps its own.
            var hostEntity = host != null && entity.Database == host.Database;
            if (hostEntity) QuantityPhysicalUnits.Stamp(values, host!.Units);
            Capture("layer_status", () =>
            {
                var layer = (LayerTableRecord)tr.GetObject(entity.LayerId, OpenMode.ForRead);
                ColorFields("layer_", layer.Color);
                var linetype = (LinetypeTableRecord)tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
                values["layer_linetype"] = linetype.Name;
                return "raw layer properties; layer 0 and ByBlock inheritance unresolved";
            });

            if (entity is BlockReference block)
            {
                Capture("block_name_raw", () => block.Name);
                Capture("block_name_effective", () =>
                {
                    if (!block.IsDynamicBlock) return block.Name;
                    var definition = (BlockTableRecord)tr.GetObject(
                        block.DynamicBlockTableRecord, OpenMode.ForRead);
                    return definition.Name;
                });
            }
            if (entity is Polyline polyline)
            {
                values["width_units"] = "entity database drawing units; untransformed";
                Capture("polyline_constant_width_raw", () => F(polyline.ConstantWidth));
                Capture("polyline_segment_width_status", () =>
                {
                    var count = Math.Max(0, polyline.NumberOfVertices - (polyline.Closed ? 0 : 1));
                    if (count == 0) return "no segments";
                    var min = double.PositiveInfinity;
                    var max = double.NegativeInfinity;
                    for (var i = 0; i < count; i++)
                    {
                        var start = polyline.GetStartWidthAt(i);
                        var end = polyline.GetEndWidthAt(i);
                        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < 0)
                            return "invalid width observation; no width summary";
                        min = Math.Min(min, Math.Min(start, end));
                        max = Math.Max(max, Math.Max(start, end));
                    }
                    values["polyline_width_min_raw"] = F(min);
                    values["polyline_width_max_raw"] = F(max);
                    values["polyline_width_segment_count"] = count.ToString(CultureInfo.InvariantCulture);
                    return "raw widths captured; painted area and dash spacing not inferred";
                });
            }
            // Bounded classification geometry (cad_segments / cad_hatch_boundary_*): raw, untransformed, read-only.
            QuantitySegmentEvidenceReader.Read(entity, tr, values,
                hostEntity && host!.Units.IsSupported ? host.Units.LinearToMetres : null);
            return values;
        }

        private static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
