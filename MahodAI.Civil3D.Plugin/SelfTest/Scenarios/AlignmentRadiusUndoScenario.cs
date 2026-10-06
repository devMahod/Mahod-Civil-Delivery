using System;
using System.Text.Json;
using System.Threading.Tasks;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// The ONE mutating scenario, and it is OPT-IN: it runs only with
    /// <c>MAHOD_SELFTEST_ALLOW_MUTATION=1</c>. Default OFF — the harness must never
    /// change an engineer's drawing just because someone ran the self test.
    ///
    /// It exercises the apply/undo half of the fix flow: change one horizontal curve
    /// radius through the real modification tool, verify the drawing changed, then put
    /// it back. Undo is attempted first through AutoCAD's <c>._UNDO</c> (what the
    /// product's <c>_UNDO n</c> button drives); if that does not restore the value, the
    /// scenario restores it by writing the original radius back and FAILS loudly rather
    /// than leaving the drawing modified.
    /// </summary>
    public sealed class AlignmentRadiusUndoScenario : ISelfTestScenario
    {
        public string Name => "mutation.alignment_radius_apply_undo";
        public string Description => "Applies one curve-radius change through the real tool and undoes it, leaving the drawing as found.";
        public bool Mutates => true;
        public TimeSpan Timeout => TimeSpan.FromMinutes(3);

        private const double Tolerance = 0.01;

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var alignment = await AlignmentGeometryScenario.FindFirstAlignmentAsync(ctx).ConfigureAwait(false);
            if (alignment == null)
                return ScenarioResult.Skip("the open drawing has no alignment");

            var curve = await FindFirstArcAsync(ctx, alignment).ConfigureAwait(false);
            if (curve == null)
                return ScenarioResult.Skip($"alignment '{alignment}' has no arc element to modify");

            var (index, originalRadius) = curve.Value;
            var targetRadius = Math.Round(originalRadius * 1.05, 3);

            // ── apply ───────────────────────────────────────────────────────────
            var applied = await ctx.RunToolAsync("modify_alignment_curve_radius", new
            {
                alignment_name = alignment,
                element_index = index,
                new_radius = targetRadius,
                exact_only = true
            }).ConfigureAwait(false);

            if (!applied.Success)
            {
                // Nothing was committed (the v1.2 abort-on-fail invariant), so the
                // drawing is untouched — report rather than fail the whole harness.
                return ScenarioResult.Skip(
                    $"the tool could not apply R={targetRadius} on '{alignment}' element {index} " +
                    $"({applied.Error?.Code}: {applied.Error?.Message}) — drawing untouched");
            }

            var afterApply = await ReadRadiusAsync(ctx, alignment, index).ConfigureAwait(false);
            if (afterApply == null || Math.Abs(afterApply.Value - targetRadius) > Tolerance)
            {
                await RestoreAsync(ctx, alignment, index, originalRadius).ConfigureAwait(false);
                return ScenarioResult.Fail(
                    $"radius reads back as {targetRadius}",
                    $"radius reads back as {afterApply?.ToString("0.###") ?? "unreadable"}",
                    "the tool reported success but the drawing does not show the change");
            }

            // ── undo ────────────────────────────────────────────────────────────
            await ctx.OnMainThreadAsync<object?>(() =>
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                doc?.SendStringToExecute("._UNDO 1 ", true, false, true);
                return null;
            }).ConfigureAwait(false);

            var restored = await WaitForRadiusAsync(ctx, alignment, index, originalRadius, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);

            if (!restored)
            {
                var repaired = await RestoreAsync(ctx, alignment, index, originalRadius).ConfigureAwait(false);
                return ScenarioResult.Fail(
                    $"._UNDO restores radius {originalRadius:0.###}",
                    $"._UNDO did not restore it (drawing was {(repaired ? "repaired by writing the original radius back" : "LEFT MODIFIED — undo manually")})",
                    "undo of a tool-applied mutation did not roll back");
            }

            return ScenarioResult
                .Pass($"apply {originalRadius:0.###} → {targetRadius:0.###}, then ._UNDO restores {originalRadius:0.###}",
                      "applied and undone; drawing left as found")
                .With("alignment", alignment)
                .With("element_index", index)
                .With("original_radius", originalRadius)
                .With("applied_radius", targetRadius);
        }

        private static async Task<(int Index, double Radius)?> FindFirstArcAsync(SelfTestContext ctx, string alignment)
        {
            var geometry = await ctx.RunToolAsync("get_alignment_geometry", new
            {
                alignment_name = alignment,
                include_elements = true
            }).ConfigureAwait(false);

            if (!geometry.Success) return null;

            var data = SelfTestContext.AsJson(geometry.Data);
            if (!ScenarioJson.TryFind(data, "elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var element in elements.EnumerateArray())
            {
                var entityType = ScenarioJson.String(element, "entity_type") ?? "";
                if (!entityType.Equals("Arc", StringComparison.OrdinalIgnoreCase)) continue;

                var radius = ScenarioJson.Number(element, "radius");
                var index = ScenarioJson.Number(element, "index");
                if (radius is > 0 && index.HasValue) return ((int)index.Value, radius.Value);
            }
            return null;
        }

        private static async Task<double?> ReadRadiusAsync(SelfTestContext ctx, string alignment, int index)
        {
            var geometry = await ctx.RunToolAsync("get_alignment_geometry", new
            {
                alignment_name = alignment,
                include_elements = true
            }).ConfigureAwait(false);

            if (!geometry.Success) return null;

            var data = SelfTestContext.AsJson(geometry.Data);
            if (!ScenarioJson.TryFind(data, "elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var element in elements.EnumerateArray())
            {
                if ((int?)ScenarioJson.Number(element, "index") == index)
                    return ScenarioJson.Number(element, "radius");
            }
            return null;
        }

        private static async Task<bool> WaitForRadiusAsync(
            SelfTestContext ctx, string alignment, int index, double expected, TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (DateTime.UtcNow < deadline)
            {
                var radius = await ReadRadiusAsync(ctx, alignment, index).ConfigureAwait(false);
                if (radius.HasValue && Math.Abs(radius.Value - expected) <= Tolerance) return true;
                await Task.Delay(500).ConfigureAwait(false);
            }
            return false;
        }

        private static async Task<bool> RestoreAsync(SelfTestContext ctx, string alignment, int index, double radius)
        {
            var restore = await ctx.RunToolAsync("modify_alignment_curve_radius", new
            {
                alignment_name = alignment,
                element_index = index,
                new_radius = radius,
                exact_only = true
            }).ConfigureAwait(false);

            return restore.Success;
        }
    }
}
