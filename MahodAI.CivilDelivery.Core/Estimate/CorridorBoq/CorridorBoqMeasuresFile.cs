using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace MahodAI.CivilDelivery.Estimate.CorridorBoq
{
    /// <summary>
    /// The typed record of one corridor measurement run (schema mahod-corridor-measures/2, file corridor_boq_measures.json in
    /// the run folder): the per-corridor measures with their completeness flags, the skipped corridors, the drawing tables,
    /// the evidence lines, the drawing and raw receipt identities, and the identity of the corridor rules that produced it.
    /// The combined bill (4 plan files + corridors) rebuilds the corridor chapters from this file without Civil; it refuses a
    /// file written under other corridor rules (the SHA-256 of the embedded rules must match), a file without any of the
    /// fields of this schema (a missing field is never read as zero), and earthworks after stripping of another method or depth.
    /// </summary>
    public static class CorridorBoqMeasuresFile
    {
        public const string Schema = "mahod-corridor-measures/2";
        public const string FileName = "corridor_boq_measures.json";

        public sealed record Header(string Schema, string Project, string RulesetVersion, string RulesetSha256, string DrawingPath,
            string DrawingSha256, string RawReceiptFile, string RawReceiptSha256, DateTime MeasuredLocal, string? ProfileId);

        public sealed record Loaded(Header Header, CorridorBoqExport.Export Export, CorridorBoqExport.Context Context);

        private sealed record MeasureDto(string CorridorId, bool Complete, List<string> Issues, int Stations, double LengthM,
            double VolCut, double VolFill, double PlanCut, double PlanFill, double Plan3DCut, double Plan3DFill,
            Dictionary<string, double> CodeVolume, Dictionary<string, double> CodePlanArea, double AsphaltPlanArea,
            string InterfaceStatus, bool EarthworksComplete, bool MaterialsComplete,
            double? PostCut, double? PostFill, double? StripDebit, double StripDepthM, string? StripMethodId);

        private static readonly string[] HeaderFields = { "Schema", "Project", "RulesetVersion", "RulesetSha256", "DrawingPath", "DrawingSha256",
            "RawReceiptFile", "RawReceiptSha256", "MeasuredLocal", "ProfileId" };
        private static readonly string[] MeasureFields = { "CorridorId", "Complete", "Issues", "Stations", "LengthM", "VolCut", "VolFill", "PlanCut",
            "PlanFill", "Plan3DCut", "Plan3DFill", "CodeVolume", "CodePlanArea", "AsphaltPlanArea", "InterfaceStatus", "EarthworksComplete",
            "MaterialsComplete", "PostCut", "PostFill", "StripDebit", "StripDepthM", "StripMethodId" };
        private static readonly string[] FileFields = { "Header", "Measures", "Skipped", "DrawingTables", "Evidence", "Notes" };

        private sealed record NamedTextDto(string Corridor, string Text);

        private sealed record TableDto(string Handle, string Title, string Road, Dictionary<string, double> Values);

        private sealed record FileDto(Header Header, List<MeasureDto> Measures, List<NamedTextDto> Skipped, List<TableDto> DrawingTables,
            List<string> Evidence, List<string> Notes);

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        /// <summary>SHA-256 (lower hex) of the embedded corridor rules of project 6422, byte for byte.</summary>
        public static string EmbeddedRulesetSha256()
        {
            using var stream = typeof(CorridorBoqRuleset).Assembly.GetManifestResourceStream(CorridorBoqRuleset.EmbeddedResource6422)
                ?? throw new InvalidOperationException("The corridor BoQ rules of project 6422 are not embedded in this build.");
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public static string Serialize(CorridorBoqExport.Export export, CorridorBoqExport.Context context, string rulesetSha256, string? profileId)
        {
            ArgumentNullException.ThrowIfNull(export);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(rulesetSha256);
            foreach (var m in export.Measures)
            {
                var values = new[] { m.LengthM, m.VolCut, m.VolFill, m.PlanCut, m.PlanFill, m.Plan3DCut, m.Plan3DFill, m.AsphaltPlanArea, m.StripDepthM }
                    .Concat(new[] { m.PostCut, m.PostFill, m.StripDebit }.Where(v => v.HasValue).Select(v => v!.Value))
                    .Concat(m.CodeVolume.Values).Concat(m.CodePlanArea.Values);
                if (values.Any(v => !double.IsFinite(v)))
                    throw new InvalidDataException($"Corridor measures of {m.CorridorId} are not finite; the measurement is not written.");
            }
            var header = new Header(Schema, export.Rules.Project, export.Rules.Version, rulesetSha256, context.DrawingPath, context.DrawingSha256,
                Path.GetFileName(context.RawReceiptPath), context.RawReceiptSha256, context.Now, profileId);
            var dto = new FileDto(header,
                export.Measures.Select(m => new MeasureDto(m.CorridorId, m.Complete, m.Issues.ToList(), m.Stations, m.LengthM, m.VolCut, m.VolFill,
                    m.PlanCut, m.PlanFill, m.Plan3DCut, m.Plan3DFill,
                    m.CodeVolume.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                    m.CodePlanArea.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
                    m.AsphaltPlanArea, m.InterfaceStatus, m.EarthworksComplete, m.MaterialsComplete,
                    m.PostCut, m.PostFill, m.StripDebit, m.StripDepthM, m.StripMethodId)).ToList(),
                export.Skipped.Select(s => new NamedTextDto(s.Corridor, s.Reason)).ToList(),
                export.DrawingTables.Select(t => new TableDto(t.Handle, t.Title, t.Road, t.Values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))).ToList(),
                export.Evidence.ToList(), export.Notes.ToList());
            return JsonSerializer.Serialize(dto, Options);
        }

        /// <summary>Reads a measures file written under the given corridor rules; throws when the schema or the rules identity
        /// differ (a measurement made under other rules is never priced with these).</summary>
        public static Loaded Read(string json, CorridorBoqRuleset rules, string rulesetSha256)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(json);
            ArgumentNullException.ThrowIfNull(rules);
            RequireFields(json);
            var dto = JsonSerializer.Deserialize<FileDto>(json, Options) ?? throw new InvalidDataException("Empty corridor measures file.");
            var h = dto.Header ?? throw new InvalidDataException("Corridor measures file without a header.");
            if (!string.Equals(h.Schema, Schema, StringComparison.Ordinal))
                throw new InvalidDataException($"Corridor measures schema '{h.Schema}' is not {Schema}.");
            if (!string.Equals(h.RulesetSha256, rulesetSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(h.Project, rules.Project, StringComparison.Ordinal) || !string.Equals(h.RulesetVersion, rules.Version, StringComparison.Ordinal))
                throw new InvalidDataException($"The corridor measurement was made under other corridor rules ({h.Project} v{h.RulesetVersion}, {h.RulesetSha256}); measure again with this version.");
            foreach (var m in dto.Measures ?? new List<MeasureDto>())
            {
                RequireMeasureSemantics(m);
                if (m.StripDepthM != rules.HisufDepthM)
                    throw new InvalidDataException($"The earthworks after stripping of {m.CorridorId} were computed with depth {m.StripDepthM} m, not the rules' {rules.HisufDepthM} m.");
                if ((m.PostCut.HasValue || m.PostFill.HasValue || m.StripDebit.HasValue) &&
                    !string.Equals(m.StripMethodId, CorridorPostStripLogic.SlopeEquivalentMethodId, StringComparison.Ordinal))
                    throw new InvalidDataException($"The earthworks after stripping of {m.CorridorId} use an unknown method '{m.StripMethodId}'.");
            }
            var measures = (dto.Measures ?? new List<MeasureDto>()).Select(m => new CorridorBoqExport.CorridorMeasure(m.CorridorId, m.Complete,
                    m.Issues, m.Stations, m.LengthM, m.VolCut, m.VolFill, m.PlanCut, m.PlanFill, m.Plan3DCut, m.Plan3DFill,
                    new Dictionary<string, double>(m.CodeVolume, StringComparer.Ordinal),
                    new Dictionary<string, double>(m.CodePlanArea, StringComparer.Ordinal))
                {
                    AsphaltPlanArea = m.AsphaltPlanArea, InterfaceStatus = m.InterfaceStatus ?? "unknown",
                    EarthworksComplete = m.EarthworksComplete, MaterialsComplete = m.MaterialsComplete,
                    PostCut = m.PostCut, PostFill = m.PostFill, StripDebit = m.StripDebit, StripDepthM = m.StripDepthM, StripMethodId = m.StripMethodId,
                }).ToList();
            var export = new CorridorBoqExport.Export(rules, measures,
                (dto.Skipped ?? new List<NamedTextDto>()).Select(s => (s.Corridor, s.Text)).ToList(),
                (dto.DrawingTables ?? new List<TableDto>()).Select(t => new CorridorVolumeTable(t.Handle, t.Title, t.Road,
                    new Dictionary<string, double>(t.Values ?? new Dictionary<string, double>(), StringComparer.Ordinal))).ToList(),
                dto.Evidence ?? new List<string>(), dto.Notes ?? new List<string>());
            var context = new CorridorBoqExport.Context(h.MeasuredLocal, h.DrawingPath, h.DrawingSha256, h.RawReceiptFile, h.RawReceiptSha256);
            return new Loaded(h, export, context);
        }

        /// <summary>What a measure claims must be backed by its values: complete earthworks need finite non-negative
        /// post-stripping values and the known method; every number is finite and non-negative; Complete is exactly
        /// earthworks and materials complete. A partial corridor may carry null post values (it is shown, never totalled).</summary>
        private static void RequireMeasureSemantics(MeasureDto m)
        {
            var where = $"Corridor measures of {m.CorridorId}";
            var numbers = new[] { m.LengthM, m.VolCut, m.VolFill, m.PlanCut, m.PlanFill, m.Plan3DCut, m.Plan3DFill, m.AsphaltPlanArea, m.StripDepthM }
                .Concat(new[] { m.PostCut, m.PostFill, m.StripDebit }.Where(v => v.HasValue).Select(v => v!.Value))
                .Concat(m.CodeVolume.Values).Concat(m.CodePlanArea.Values);
            if (numbers.Any(v => !double.IsFinite(v) || v < 0))
                throw new InvalidDataException($"{where}: a quantity is negative or not finite.");
            if (m.Complete != (m.EarthworksComplete && m.MaterialsComplete))
                throw new InvalidDataException($"{where}: 'Complete' does not match the earthworks and materials completeness.");
            if (m.EarthworksComplete && (m.PostCut is null || m.PostFill is null || m.StripDebit is null ||
                !string.Equals(m.StripMethodId, CorridorPostStripLogic.SlopeEquivalentMethodId, StringComparison.Ordinal)))
                throw new InvalidDataException($"{where}: complete earthworks without the earthworks after stripping and their method.");
        }

        /// <summary>Every field of the schema must be present (null where the schema allows it): a missing field is refused,
        /// never read as zero or false. Maps and lists are never null.</summary>
        private static void RequireFields(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            static void Require(JsonElement element, string[] names, string where)
            {
                if (element.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException($"Corridor measures file: {where} is not an object.");
                foreach (var name in names)
                    if (!element.TryGetProperty(name, out _))
                        throw new InvalidDataException($"Corridor measures file: {where} has no '{name}'.");
            }
            Require(root, FileFields, "the file");
            Require(root.GetProperty("Header"), HeaderFields, "the header");
            if (root.GetProperty("Measures").ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Corridor measures file: Measures is not a list.");
            foreach (var list in new[] { "Skipped", "DrawingTables", "Evidence", "Notes" })
                if (root.GetProperty(list).ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Corridor measures file: {list} is not a list.");
            var index = 0;
            foreach (var measure in root.GetProperty("Measures").EnumerateArray())
            {
                var where = $"measure {index++}";
                Require(measure, MeasureFields, where);
                if (measure.GetProperty("Issues").ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Corridor measures file: {where} Issues is not a list.");
                foreach (var map in new[] { "CodeVolume", "CodePlanArea" })
                    if (measure.GetProperty(map).ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException($"Corridor measures file: {where} {map} is not an object.");
            }
        }
    }
}
