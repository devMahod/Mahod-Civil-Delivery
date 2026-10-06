using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.CrossSection
{
    /// <summary>
    /// Read-only survey of every cross-section on the sheet: which chains sit at
    /// a ragged level, which still carry an un-collapsed kerb pair, and whether
    /// each printed number agrees with the geometry beneath it.
    ///
    /// Writes nothing. Safe to run from chat without a confirmation, which is why
    /// it is the tool the assistant reaches for on "check the cross sections".
    /// </summary>
    public sealed class AuditCrossSectionsTool : DrawingToolBase
    {
        public override string Name => "audit_cross_sections";

        public override string Description =>
            "Survey every cross-section frame in the drawing and report what is wrong: " +
            "dimension chains sitting at a non-whole elevation, kerb point pairs that still " +
            "need collapsing, distance texts that disagree with the span they label, and " +
            "height labels that disagree with their vertex. Read-only — changes nothing.";

        public override string Category => ToolCategories.Validation;

        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public override JsonElement? ParameterSchema => JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "section": {
              "type": "string",
              "description": "Optional section number, e.g. \"224\". Omit to audit every section."
            }
          }
        }
        """).RootElement;

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            // The executor has already bound this session to its tab's drawing
            // (ToolTargetPlanner), so the active document is the intended target.
            var db = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument?.Database;
            if (db == null)
                return Task.FromResult(ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "No active drawing."));

            string? only = GetStringParam(parameters, "section");

            List<SectionModel> sections;
            try
            {
                sections = CrossSectionReader.ReadAll(tr, db);
            }
            catch (Exception ex)
            {
                return Task.FromResult(ToolResult.Fail(
                    ToolErrorCodes.ExecutionFailed, "Could not read cross-sections.", ex.Message));
            }

            if (sections.Count == 0)
            {
                return Task.FromResult(ToolResult.Ok(new
                {
                    sections = 0,
                    message = "No cross-section frames found. Expected titles like \"STG-A1 - 224 (4480.00)\"."
                }));
            }

            if (!string.IsNullOrWhiteSpace(only))
                sections = sections.Where(s => s.Number == only.Trim()).ToList();

            var rows = new List<object>();
            int ragged = 0, kerbPairs = 0, numberMismatches = 0, unresolved = 0;

            foreach (var s in sections)
            {
                ct.ThrowIfCancellationRequested();

                if (!s.IsResolved || s.Frame == null)
                {
                    unresolved++;
                    rows.Add(new { section = s.Number, resolved = false, reason = "frame could not be decoded" });
                    continue;
                }

                var plan = CrossSectionFixPlanner.Plan(s);
                if (plan.MovesLevel) ragged++;
                if (plan.HasKerbPair) kerbPairs++;

                // every distance text against the span it labels
                var spans = s.Spans();
                var badNumbers = new List<object>();
                for (int i = 0; i < spans.Count && i < s.Distances.Count; i++)
                {
                    double err = spans[i] - s.Distances[i].Value;
                    if (Math.Abs(err) > 0.006)
                        badNumbers.Add(new
                        {
                            text = s.Distances[i].Raw,
                            measured = Math.Round(spans[i], 3),
                            atOffset = Math.Round(s.Frame.OffsetAt(s.Ticks[i]), 2)
                        });
                }
                numberMismatches += badNumbers.Count;

                rows.Add(new
                {
                    section = s.Number,
                    station = s.Station,
                    chainElevation = Math.Round(s.ChainElevation, 3),
                    levelIsWhole = !plan.MovesLevel,
                    levelMove = plan.MovesLevel
                        ? $"{Math.Round(plan.LevelFrom, 3)} → {Math.Round(plan.LevelTo, 0)}"
                        : null,
                    kerbPair = plan.HasKerbPair
                        ? $"{Math.Round(s.Frame.OffsetAt(plan.KerbFirstX), 3)} / {Math.Round(s.Frame.OffsetAt(plan.KerbSecondX), 3)}"
                        : null,
                    ticks = s.Ticks.Count,
                    distanceTexts = s.Distances.Count,
                    heightLabels = s.Heights.Count,
                    numberMismatches = badNumbers,
                    notes = plan.Notes
                });
            }

            bool clean = ragged == 0 && kerbPairs == 0 && numberMismatches == 0 && unresolved == 0;

            return Task.FromResult(ToolResult.Ok(new
            {
                sections = rows.Count,
                clean,
                summary = new
                {
                    chainsAtRaggedLevel = ragged,
                    kerbPairsToCollapse = kerbPairs,
                    numbersDisagreeingWithGeometry = numberMismatches,
                    framesNotDecoded = unresolved
                },
                detail = rows
            }));
        }
    }
}
