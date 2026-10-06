using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Tools
{
    /// <summary>
    /// Central registry for all drawing tools. Handles tool registration and lookup.
    /// </summary>
    public class ToolRegistry
    {
        private readonly Dictionary<string, IDrawingTool> _tools = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<IDrawingTool>> _toolsByCategory = new(StringComparer.OrdinalIgnoreCase);

        private static ToolRegistry? _instance;
        private static readonly object _lock = new();

        /// <summary>
        /// Gets the singleton instance of the tool registry.
        /// </summary>
        public static ToolRegistry Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new ToolRegistry();
                    }
                }
                return _instance;
            }
        }

        private ToolRegistry()
        {
            RegisterAllTools();
        }

        /// <summary>
        /// Registers all available tools.
        /// </summary>
        private void RegisterAllTools()
        {
            // Discovery tools
            Register(new Discovery.ListObjectsTool());
            Register(new Discovery.ListLayersTool());
            Register(new Discovery.GetDrawingSummaryTool());
            // Sheet enumeration for the visual scan: the layout tab, not model space,
            // is the unit of work for sheet QA.
            Register(new Discovery.ListLayoutsTool());

            // Alignment tools
            Register(new Alignment.GetAlignmentGeometryTool());
            Register(new Alignment.GetPointAtStationTool());
            Register(new Alignment.GetStationOffsetTool());
            Register(new Alignment.SetStationLabelsTool());
            Register(new Alignment.ListAlignmentsTool());
            Register(new Alignment.FindIntersectionsTool());

            // Profile tools
            Register(new Profile.GetProfileGeometryTool());
            Register(new Profile.GetProfileElevationAtStationTool());
            Register(new Profile.SampleProfileElevationsTool());
            Register(new Profile.ListProfilesTool());

            // Surface tools
            Register(new Surface.GetSurfaceInfoTool());
            Register(new Surface.GetSurfaceElevationTool());
            Register(new Surface.SampleSurfaceProfileTool());
            Register(new Surface.ListSurfacesTool());

            // Corridor tools
            Register(new Corridor.GetCorridorInfoTool());
            Register(new Corridor.GetCorridorCrossSectionTool());
            Register(new Corridor.SampleCrossSectionsTool());
            Register(new Corridor.ListCorridorsTool());

            // Pipe Network tools
            Register(new PipeNetwork.GetPipeNetworkInfoTool());
            Register(new PipeNetwork.GetPipeDetailsTool());
            Register(new PipeNetwork.GetStructureDetailsTool());

            // Signs & Markings tools
            Register(new Validation.ValidateSignsTool());
            Register(new Validation.ValidateMarkingsTool());
            Register(new Validation.CheckSignMarkingConsistencyTool());
            Register(new SignsMarkings.ListSignsTool());
            Register(new SignsMarkings.ListMarkingsTool());
            Register(new SignsMarkings.GetSignRequirementsTool());
            Register(new PipeNetwork.ListPipeNetworksTool());

            // Validation tools
            Register(new Validation.ValidateAlignmentTool());
            Register(new Validation.ValidateProfileTool());
            Register(new Validation.CheckDesignClearancesTool());

            // Visual sheet QA (Ari Bali, 2026-08-11): per-layout text/legend/clutter checks
            // plus the removable review markup they draw.
            Register(new Validation.RunVisualScanTool());
            Register(new Validation.ClearVisualMarkupTool());

            // Cross-section sheet repair (Olga, 2026-08-13). The audit is read-only;
            // the fix is preview-then-accept on the MAHOD_FIX working layer so native
            // annotation is never touched before the engineer has compared the two.
            // Driven by CrossSectionPipeline, never declared as chat tools.
            Register(new CrossSection.AuditCrossSectionsTool());
            Register(new CrossSection.PreviewCrossSectionFixTool());
            Register(new CrossSection.AcceptCrossSectionFixTool());
            Register(new CrossSection.DiscardCrossSectionFixTool());

            // Modification tools (Stage 3 - Fix workflow)
            Register(new Modification.ModifyAlignmentCurveRadiusTool());
            Register(new Modification.InsertAlignmentCurveTool());
            // Inverse of InsertAlignmentCurveTool: removes a Table 5.5
            // "curve unnecessary" Arc/SCS and re-joins the tangents at the PI.
            Register(new Modification.RemoveAlignmentCurveTool());
            Register(new Modification.ModifyAlignmentDesignSpeedTool());
            Register(new Modification.SetAlignmentCriteriaTool());
            Register(new Modification.ModifyProfilePVIElevationTool());
            Register(new Modification.ModifyProfileVerticalCurveTool());
            Register(new Modification.ModifyProfileGradeTool());

            // Alignment modification tools
            Register(new Modification.ModifyAlignmentSpiralLengthTool());
            Register(new Modification.ModifyAlignmentTangentLengthTool());
            Register(new Alignment.FixAlignmentGeometryTool());
            // Re-introduced 2026-06-08: AddAlignmentSpiralTool now supports
            // Arc→SCS conversion via Entities.Remove + AddFreeSCS.
            Register(new Modification.AddAlignmentSpiralTool());
            // Removed: ModifySuperelevationRateTool (superelevation rate is read-only)

            // Profile modification tools
            Register(new Modification.ModifyProfileSightDistanceTool());
            Register(new Modification.AddProfilePVITool());
            Register(new Modification.DeleteProfilePVITool());
            Register(new Modification.AddVerticalCurveAtPviTool());
            // Removed: ModifyProfileVerticalCurveTypeTool (CurveType is read-only)

            // Corridor modification tools
            Register(new Corridor.ListSubassemblyParametersTool());
            Register(new Corridor.ModifyCorridorWidthTool());
            Register(new Corridor.ModifyCorridorSlopeTool());
            Register(new Corridor.ModifySubassemblyParameterTool());
            // Registered 2026-06-11: the agent's fix config maps corridor
            // total_width violations to this tool — leaving it unregistered
            // guaranteed TOOL_NOT_FOUND at runtime (findings T-1).
            Register(new Corridor.NormalizeCorridorWidthTool());
            Register(new Corridor.RebuildCorridorTool());
            // Registered 2026-07-21: the agent's "expand road" flow re-points a
            // corridor region onto a cloned 2-lane / divided assembly. Swapping
            // the whole assembly is the only reliable way to change a section —
            // the .NET API cannot author subassemblies from scratch.
            Register(new Corridor.SetRegionAssemblyTool());

            // Pipe network modification tools
            Register(new PipeNetwork.ModifyPipeSlopeTool());
            // Removed: ModifyPipeSizeTool (InnerDiameter is read-only)
            Register(new PipeNetwork.ModifyStructureRimElevationTool());
            Register(new PipeNetwork.ModifyStructureSumpDepthTool());

            // Signs & markings modification tools
            Register(new SignsMarkings.AddSignTool());
            Register(new SignsMarkings.RemoveSignTool());
            Register(new SignsMarkings.MoveSignTool());
            Register(new SignsMarkings.AddMarkingTool());
            Register(new SignsMarkings.RemoveMarkingTool());
            Register(new SignsMarkings.ModifyMarkingPatternTool());

            // Surface modification tools
            Register(new Surface.ModifySurfacePointElevationTool());

            // Intersection modification tools
            // Removed: ModifyIntersectionRadiusTool (CurbReturns is read-only)

            // Creation tools
            Register(new Creation.CreateAssemblyTool());
            Register(new Creation.CloneAssemblyFromLibraryTool());
            Register(new Creation.CreateAlignmentTool());
            Register(new Creation.DrawCenterlineTool());
            Register(new Creation.DrawCenterlineRoutedTool());
            Register(new Creation.CreateProfileTool());
            Register(new Creation.CreateProfileViewTool());
            Register(new Creation.VerifyAssemblyTool());
            Register(new Creation.CreateCorridorTool());
            Register(new Creation.ConfigureProfileBandsTool());
            Register(new Creation.CreateSampleLinesTool());
            Register(new Creation.CreateSectionViewsTool());
            Register(new Creation.CreateDrawingTableTool());

            // Corridor analysis tools
            Register(new Corridor.CreateSlopesTool());
            Register(new Corridor.CalcVolumesTool());

            // Layer & entity modification tools
            Register(new Modification.ModifyLayerPropertiesTool());
            Register(new Modification.MoveEntityTool());
            Register(new Modification.ModifyEntityPropertyTool());
            Register(new Modification.RebuildAlignmentTool());
            Register(new Modification.BatchFreezeLayersTool());
            Register(new Modification.BatchThawLayersTool());

            // Polyline / hatch vertex editing (2026-08-12): the native port of the PLTOOLS LISP
            // set the drafting team lives on. Called by name from PolylineEditPipeline and
            // deliberately NOT declared in the agent's civil3d_tools.py — they write to the
            // drawing, so declaring them would leak mutating tools into free chat's read-only
            // tool set (same arrangement as the visual scan).
            Register(new PolylineEdit.EditPolylineVerticesTool());
            Register(new PolylineEdit.CleanPolylineVerticesTool());
            Register(new PolylineEdit.DensifyPolylineTool());
            Register(new PolylineEdit.ModifyPolylineSegmentsTool());
            Register(new PolylineEdit.SetPolylineDirectionTool());
            Register(new PolylineEdit.ConvertPolylineTypeTool());
            Register(new PolylineEdit.JoinPolylinesTool());
            Register(new PolylineEdit.CreatePolylineGeometryTool());

            // View tools
            Register(new View.ZoomToObjectTool());
            Register(new View.ZoomToStationRangeTool());

            // Analysis & Validation
            Register(new Validation.CheckSightDistanceTool());
            Register(new Alignment.GetSuperelevationDataTool());
            Register(new Alignment.ComputeSuperelevationRateTool());
            Register(new Profile.OptimizeProfilePvisTool());
            Register(new Profile.InterpolateProfileWithSplineTool());
            Register(new Alignment.GetStationEquationInfoTool());

            // Query tools
            Register(new Corridor.SampleCrossSectionAtStationTool());
            Register(new PipeNetwork.CalculatePipeCoverDepthTool());
            Register(new PipeNetwork.GetPipeCurveProfileTool());

            // Creation tools (from polyline, offset, surfaces)
            Register(new Creation.CreateAlignmentFromPolylineTool());
            Register(new Creation.CreateOffsetAlignmentTool());
            Register(new Creation.CreateCorridorSurfaceTool());
            Register(new Creation.CreateSurfaceTool());
            Register(new Creation.CreateSampleLineGroupTool());

            // Mahod Civil Delivery (MHD_SECTIONS / MHD_ESTIMATE) — AI route over the
            // SAME deterministic services as the direct commands (plan §12).
            Register(new CivilDelivery.ScanProjectSetupTool());
            Register(new CivilDelivery.SaveProjectSetupTool());
            Register(new CivilDelivery.PlanSectionsTool());
            Register(new CivilDelivery.ApproveSectionTrafficDirectionTool());
            Register(new CivilDelivery.PreviewSectionsTool());
            Register(new CivilDelivery.ApplySectionsTool());
            Register(new CivilDelivery.VerifySectionsTool());
            Register(new CivilDelivery.ScanEstimateQuantitiesTool());
            Register(new CivilDelivery.ProposeEstimateMappingsTool());
            Register(new CivilDelivery.SaveEstimateMappingsTool());
            Register(new CivilDelivery.BuildEstimateTool());
            Register(new CivilDelivery.GetEstimateTraceTool());

            System.Diagnostics.Debug.WriteLine($"[ToolRegistry] Registered {_tools.Count} tools");
        }

        /// <summary>
        /// Registers a tool in the registry.
        /// </summary>
        public void Register(IDrawingTool tool)
        {
            _tools[tool.Name] = tool;

            if (!_toolsByCategory.TryGetValue(tool.Category, out var categoryTools))
            {
                categoryTools = new List<IDrawingTool>();
                _toolsByCategory[tool.Category] = categoryTools;
            }

            // Remove existing tool with same name in category
            categoryTools.RemoveAll(t => t.Name.Equals(tool.Name, StringComparison.OrdinalIgnoreCase));
            categoryTools.Add(tool);
        }

        /// <summary>
        /// Gets a tool by name.
        /// </summary>
        public IDrawingTool? GetTool(string name)
        {
            _tools.TryGetValue(name, out var tool);
            return tool;
        }

        /// <summary>
        /// Gets all registered tools.
        /// </summary>
        public IEnumerable<IDrawingTool> GetAllTools()
        {
            return _tools.Values;
        }

        /// <summary>
        /// Gets tools by category.
        /// </summary>
        public IEnumerable<IDrawingTool> GetToolsByCategory(string category)
        {
            if (_toolsByCategory.TryGetValue(category, out var tools))
            {
                return tools;
            }
            return Enumerable.Empty<IDrawingTool>();
        }

        /// <summary>
        /// Gets all categories.
        /// </summary>
        public IEnumerable<string> GetCategories()
        {
            return _toolsByCategory.Keys;
        }

        /// <summary>
        /// Checks if a tool exists.
        /// </summary>
        public bool HasTool(string name)
        {
            return _tools.ContainsKey(name);
        }

        /// <summary>
        /// Gets tool definitions for WebSocket connection acknowledgment.
        /// </summary>
        public List<ToolDefinition> GetToolDefinitions()
        {
            return _tools.Values.Select(t => new ToolDefinition
            {
                Name = t.Name,
                Description = t.Description,
                Category = t.Category,
                Parameters = t.ParameterSchema,
                TimeoutSeconds = (int)t.Timeout.TotalSeconds
            }).ToList();
        }

        /// <summary>
        /// Gets tool count.
        /// </summary>
        public int Count => _tools.Count;
    }
}
