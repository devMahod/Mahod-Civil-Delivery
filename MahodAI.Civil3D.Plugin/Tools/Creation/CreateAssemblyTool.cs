using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.Tools.Creation
{
    /// <summary>
    /// Creates an EMPTY named Assembly (cross-section template) for an Israeli road and returns
    /// a Hebrew, ordered guide telling the engineer which Tool Palette subassemblies + widths to
    /// add for the chosen road type.
    ///
    /// Owner decision (binding): Civil 3D's .NET API cannot build subassemblies (lanes/shoulders/
    /// curbs) from scratch — only cloning a complete pre-built assembly is reliable, and no
    /// pre-built assembly library exists yet. So this ships EMPTY-SHELL + GUIDED MANUAL ADD:
    /// the plugin creates the empty named assembly via the proven LISP path
    /// (SendStringToExecute "_-CREATEASSEMBLY", queued and run after the transaction commits)
    /// and returns the guide; the agent maps the user's prompt to a road type and, if unclear,
    /// asks the engineer to specify the type or build the assembly manually and press continue.
    /// A future clone-from-library upgrade is queued separately.
    ///
    /// The road-type catalog + guide generation live in the AutoCAD-free
    /// <see cref="AssemblyTemplateCatalog"/> so they are unit-testable without a live document.
    /// </summary>
    public class CreateAssemblyTool : DrawingToolBase
    {
        public override string Name => "create_assembly";
        public override string Description =>
            "Creates an EMPTY named assembly (cross-section template) for a road, chosen by " +
            "road_type (urban_2lane | rural_2lane | divided_highway | collector_local). " +
            "Returns a Hebrew ordered guide of the Tool Palette subassemblies + widths the " +
            "engineer must add (lanes, shoulders/curbs, median, daylight). The engineer adds " +
            "them then presses continue; subassemblies cannot be built reliably via the API.";
        public override string Category => ToolCategories.Creation;
        public override TimeSpan Timeout => TimeSpan.FromSeconds(60);

        private static readonly JsonElement _schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""assembly_name"": {
                    ""type"": ""string"",
                    ""description"": ""Name for the new assembly (e.g. 'Highway 2+2')""
                },
                ""road_type"": {
                    ""type"": ""string"",
                    ""enum"": [""urban_2lane"", ""rural_2lane"", ""divided_highway"", ""collector_local""],
                    ""description"": ""Israeli road type. Picks MOT-2011 default widths and which elements (curbs/median/daylight) the section uses. Defaults to rural_2lane if missing/invalid.""
                },
                ""lane_width"": {
                    ""type"": ""number"",
                    ""description"": ""Optional override of the lane width in meters (else road-type default)""
                },
                ""shoulder_width"": {
                    ""type"": ""number"",
                    ""description"": ""Optional override of the shoulder width in meters (else road-type default)""
                }
            },
            ""required"": [""assembly_name""]
        }").RootElement;

        public override JsonElement? ParameterSchema => _schema;

        public override async Task<ToolResult> ExecuteAsync(
            Transaction tr,
            CivilDocument civilDoc,
            JsonElement parameters,
            ToolCache cache,
            CancellationToken ct)
        {
            string assemblyName;
            try
            {
                assemblyName = GetRequiredStringParam(parameters, "assembly_name");
            }
            catch (ArgumentException ex)
            {
                return ToolResult.Fail(ToolErrorCodes.InvalidParameters, ex.Message);
            }

            var roadTypeRaw = GetStringParam(parameters, "road_type");
            var laneOverride = GetDoubleParam(parameters, "lane_width");
            var shoulderOverride = GetDoubleParam(parameters, "shoulder_width");

            // Resolve the road-type template (widths + element inclusion + Hebrew text) in the
            // pure catalog — invalid/missing road_type falls back to rural_2lane.
            var template = AssemblyTemplateCatalog.Resolve(roadTypeRaw, laneOverride, shoulderOverride);
            var subassemblyGuide = AssemblyTemplateCatalog.BuildSubassemblyGuide(template);

            if (civilDoc == null)
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed, "Not a Civil 3D document");

            try
            {
                // ── Reuse: if an assembly with this name already exists, don't recreate it ──
                foreach (ObjectId existingId in civilDoc.AssemblyCollection)
                {
                    var existing = tr.GetObject(existingId, OpenMode.ForRead) as CivilDb.Assembly;
                    if (existing != null &&
                        existing.Name.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        var reuse = BuildResult(assemblyName, template, subassemblyGuide,
                            commandQueued: false);
                        reuse["already_existed"] = true;
                        reuse["message"] =
                            $"אסמבלי בשם '{assemblyName}' כבר קיים בשרטוט. {template.CrossSectionHe}. " +
                            "יש להשלים את ה-Subassemblies לפי המדריך ולאחר מכן ללחוץ \"המשך\".";
                        cache.RemoveByPattern("get_drawing_summary:");
                        return ToolResult.Ok(reuse);
                    }
                }

                // ── Create the empty named assembly via the proven LISP path. ──
                // _-CREATEASSEMBLY (hyphenated = no dialog); prompts answered: Name, Description,
                // Style, Code Set Style, Insertion Point. (list 0 0 0) avoids an interactive pick.
                // The command is queued and executes after the transaction commits.
                bool commandQueued = false;
                try
                {
                    var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                        .DocumentManager.MdiActiveDocument;
                    if (doc != null)
                    {
                        string lispCmd = "(progn " +
                            "(setvar \"CMDECHO\" 0) " +
                            $"(command \"_-CREATEASSEMBLY\" \"{assemblyName}\" \"\" \"\" \"\" (list 0.0 0.0 0.0)) " +
                            "(setvar \"CMDECHO\" 1) " +
                            "(princ))";
                        doc.SendStringToExecute(lispCmd + "\n", true, false, false);
                        commandQueued = true;
                    }
                }
                catch
                {
                    commandQueued = false;
                }

                var result = BuildResult(assemblyName, template, subassemblyGuide, commandQueued);
                cache.RemoveByPattern("get_drawing_summary:");
                return await Task.FromResult(ToolResult.Ok(result));
            }
            catch (Exception ex)
            {
                return ToolResult.Fail(ToolErrorCodes.ExecutionFailed,
                    $"Failed to process assembly request: {ex.Message}", ex.ToString());
            }
        }

        /// <summary>
        /// Assembles the SHARED CONTRACT result dictionary from a resolved template.
        /// </summary>
        private static Dictionary<string, object> BuildResult(
            string assemblyName,
            AssemblyTemplateCatalog.Template template,
            IReadOnlyList<string> subassemblyGuide,
            bool commandQueued)
        {
            return new Dictionary<string, object>
            {
                ["success"] = true,
                ["assembly_name"] = assemblyName,
                ["road_type"] = template.RoadType,
                ["command_queued"] = commandQueued,
                ["cross_section_he"] = template.CrossSectionHe,
                ["subassembly_guide"] = subassemblyGuide,
                ["message"] = AssemblyTemplateCatalog.BuildMessage(assemblyName, template, commandQueued),
                // Extra, non-contract diagnostics — useful to the agent, ignored by the shared shape.
                ["lane_width_m"] = template.LaneWidth,
                ["shoulder_width_m"] = template.ShoulderWidth,
                ["has_median"] = template.HasMedian,
                ["has_curbs"] = template.HasCurbs,
                ["has_daylight"] = template.HasDaylight,
                ["road_type_defaulted"] = template.FellBackToDefault,
            };
        }
    }
}
