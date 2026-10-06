using System;
using System.Threading.Tasks;

namespace MahodAI.Civil3D.Plugin.SelfTest.Scenarios
{
    /// <summary>
    /// <c>list_layers</c> against the open drawing. The cheapest end-to-end proof that
    /// the whole stack works in-host: registry → serial queue → Idle hop → document
    /// lock → transaction → AutoCAD symbol table read.
    /// </summary>
    public sealed class ListLayersScenario : ISelfTestScenario
    {
        public string Name => "discovery.list_layers";
        public string Description => "list_layers returns the open drawing's layer table (never empty — layer 0 always exists).";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var result = await ctx.RunToolAsync("list_layers", new { include_entity_counts = false, limit = 200 });

            if (!result.Success)
                return ScenarioResult.Fail("success=true", "tool failed", result.Error?.Message);

            var data = SelfTestContext.AsJson(result.Data);
            var count = ScenarioJson.ArrayCount(data, "layers");

            if (count < 0)
                return ScenarioResult.Fail("result carries a 'layers' array", "no layers array in the result");

            if (count == 0)
                return ScenarioResult.Fail("at least one layer (layer 0 always exists)", "layers array is empty");

            var first = ScenarioJson.FirstOf(data, "layers");
            var firstName = first.HasValue ? ScenarioJson.String(first.Value, "name") : null;

            return ScenarioResult
                .Pass("non-empty layers array", $"{count} layers, first = '{firstName}'")
                .With("layer_count", count);
        }
    }

    /// <summary>
    /// <c>list_objects</c> — the Civil-side discovery read. Uses <c>object_types</c>
    /// (PROTOCOL v1.2: the old singular <c>object_type</c> was a silent mismatch).
    /// </summary>
    public sealed class ListObjectsScenario : ISelfTestScenario
    {
        public string Name => "discovery.list_objects";
        public string Description => "list_objects returns a well-formed per-type result for the open Civil 3D drawing.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var result = await ctx.RunToolAsync("list_objects", new
            {
                object_types = new[] { "alignments", "surfaces", "profiles", "corridors" },
                limit = 100
            });

            if (!result.Success)
            {
                var message = result.Error?.Message ?? "";
                if (message.Contains("Not a Civil 3D document", StringComparison.OrdinalIgnoreCase))
                    return ScenarioResult.Skip("the open drawing is not a Civil 3D document");

                return ScenarioResult.Fail("success=true", "tool failed", message);
            }

            var data = SelfTestContext.AsJson(result.Data);
            var alignments = Math.Max(0, ScenarioJson.ArrayCount(data, "alignments"));
            var surfaces = Math.Max(0, ScenarioJson.ArrayCount(data, "surfaces"));
            var profiles = Math.Max(0, ScenarioJson.ArrayCount(data, "profiles"));
            var corridors = Math.Max(0, ScenarioJson.ArrayCount(data, "corridors"));
            var total = alignments + surfaces + profiles + corridors;

            if (total == 0)
                return ScenarioResult.Skip("the open drawing contains no alignments/surfaces/profiles/corridors");

            // Shape check on the first entity we can find.
            var first = ScenarioJson.FirstOf(data, alignments > 0 ? "alignments" : surfaces > 0 ? "surfaces" : "profiles");
            var firstName = first.HasValue ? ScenarioJson.String(first.Value, "name") : null;

            if (string.IsNullOrWhiteSpace(firstName))
                return ScenarioResult.Fail("each listed object carries a name", "first listed object has no name");

            return ScenarioResult
                .Pass("non-empty, named Civil objects", $"{total} objects (first = '{firstName}')")
                .With("alignments", alignments)
                .With("surfaces", surfaces)
                .With("profiles", profiles)
                .With("corridors", corridors);
        }
    }

    /// <summary>
    /// <c>get_alignment_geometry</c> — the extraction path the analyze pipeline depends on.
    /// Element extraction touches the Civil managed API directly and cannot run outside
    /// Civil 3D, which is exactly why the unit suite marks those tests RequiresCivil3D.
    /// </summary>
    public sealed class AlignmentGeometryScenario : ISelfTestScenario
    {
        public string Name => "extraction.get_alignment_geometry";
        public string Description => "get_alignment_geometry returns ordered, typed elements with stations for a real alignment.";
        public bool Mutates => false;
        public TimeSpan Timeout => TimeSpan.FromSeconds(120);

        public async Task<ScenarioResult> RunAsync(SelfTestContext ctx)
        {
            var alignmentName = await FindFirstAlignmentAsync(ctx);
            if (alignmentName == null)
                return ScenarioResult.Skip("the open drawing has no alignment to extract");

            var result = await ctx.RunToolAsync("get_alignment_geometry", new
            {
                alignment_name = alignmentName,
                include_elements = true
            });

            if (!result.Success)
                return ScenarioResult.Fail("success=true", "tool failed", result.Error?.Message);

            var data = SelfTestContext.AsJson(result.Data);
            var elementCount = ScenarioJson.ArrayCount(data, "elements");

            if (elementCount <= 0)
                return ScenarioResult.Fail("at least one geometry element", $"elements = {elementCount}");

            var first = ScenarioJson.FirstOf(data, "elements");
            var entityType = first.HasValue ? ScenarioJson.String(first.Value, "entity_type") : null;
            var length = ScenarioJson.Number(data, "length") ?? 0;

            if (string.IsNullOrWhiteSpace(entityType))
                return ScenarioResult.Fail("elements carry entity_type", "first element has no entity_type");

            if (length <= 0)
                return ScenarioResult.Fail("alignment length > 0", $"length = {length}");

            return ScenarioResult
                .Pass("typed elements + positive length",
                      $"'{alignmentName}': {elementCount} elements, first = {entityType}, length = {length:0.##} m")
                .With("alignment", alignmentName)
                .With("element_count", elementCount)
                .With("length_m", length);
        }

        internal static async Task<string?> FindFirstAlignmentAsync(SelfTestContext ctx)
        {
            var listed = await ctx.RunToolAsync("list_objects", new { object_types = new[] { "alignments" }, limit = 10 });
            if (!listed.Success) return null;

            var first = ScenarioJson.FirstOf(SelfTestContext.AsJson(listed.Data), "alignments");
            if (!first.HasValue) return null;

            var name = ScenarioJson.String(first.Value, "name");
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }
}
