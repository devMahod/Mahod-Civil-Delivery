using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;

namespace MahodAI.Civil3D.Plugin.Tools.SignsMarkings
{
    /// <summary>
    /// Returns the required sign set for a given sign code / road context per Israeli standards.
    /// P0-05: NOT YET WIRED to a sourced ruleset — it fails cleanly (STANDARD_RULESET_UNAVAILABLE)
    /// rather than returning an empty-but-Ok payload that reads as "no requirements".
    /// </summary>
    public class GetSignRequirementsTool : DrawingToolBase
    {
        public override string Name => "get_sign_requirements";
        public override string Description => "Returns the requirements specification for a given Israeli road sign code. NOT wired to a sourced ruleset yet — returns a STANDARD_RULESET_UNAVAILABLE failure (never an empty pass).";
        public override string Category => "SignsMarkings";
        public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public override Task<ToolResult> ExecuteAsync(
            Transaction tr, CivilDocument civilDoc,
            JsonElement parameters, ToolCache cache, CancellationToken ct)
        {
            var signCode = GetStringParam(parameters, "sign_code") ?? string.Empty;

            // P0-05: the sourced Israeli sign-rules provider is not implemented. Returning
            // requirements=null inside a ToolResult.Ok reads as "no requirements for this sign"
            // — a fail-open. Fail explicitly so no caller mistakes the absence of a wired
            // ruleset for a compliant/empty requirement set.
            return Task.FromResult(ToolResult.Fail(
                "STANDARD_RULESET_UNAVAILABLE",
                $"Sign-requirements lookup for '{signCode}' is not available: no sourced Israeli sign-rules provider is wired yet.",
                "Pending the versioned, engineer-approved rules provider (P0-05)."));
        }
    }
}
