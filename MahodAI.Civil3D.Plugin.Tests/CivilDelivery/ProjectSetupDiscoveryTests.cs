using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using Xunit;
using Node = MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services.ProjectSetupScanner.DiscoveryNode<string, System.Func<MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry.Pt2, MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Geometry.Pt2>>;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

// Behavioral fixtures run the production traversal, not a second scanner. Native
// object opening and loaded-DWG identity remain explicitly native acceptance work.
public sealed class ProjectSetupDiscoveryTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void SingleClEntityIsOfferedWithoutInventingAnEmptyCandidate(int count, bool expected)
    {
        new ProjectSetupScanner().MinTwoPointEntities.Should().Be(1);
        ProjectSetupScanner.OffersCandidate(count).Should().Be(expected);
        ProjectSetupScanner.OffersCandidate(0, 0).Should().BeFalse();
        ProjectSetupScanner.OffersCandidate(1, 2).Should().BeFalse();
    }

    [Fact]
    public void OrdinaryNestedBlocksComposeInnerThenOuterAndRemainHostGeometry()
    {
        var f = new Fixture();
        f.Nodes["outer"] = f.Reference("outer", "A", new[] { "inner" }, p => new(p.X + 100, p.Y + 200));
        f.Nodes["inner"] = f.Reference("inner", "B", new[] { "line" }, p => new(-p.Y, p.X));
        f.Nodes["line"] = f.Leaf("line", new(2, 3));
        f.Run("outer");
        f.Failures.Should().BeEmpty();
        f.Visited.Should().ContainSingle().Which.Should().Be(("line", new Pt2(97, 202), false));
    }

    [Fact]
    public void RepeatedDefinitionInstancesAreBothDiscovered_NotMistakenForACycle()
    {
        var f = new Fixture();
        f.Nodes["first"] = f.Reference("first", "shared", new[] { "line" }, p => new(p.X + 10, p.Y));
        f.Nodes["second"] = f.Reference("second", "shared", new[] { "line" }, p => new(p.X + 20, p.Y));
        f.Nodes["line"] = f.Leaf("line", new(1, 0));
        f.Run("first", "second");
        f.Failures.Should().BeEmpty();
        f.Visited.Select(v => v.Point.X).Should().Equal(11, 21);
    }

    [Fact]
    public void ExternalThenOrdinaryThenExternalUsesItsContainingSourceAndKeepsXrefProvenance()
    {
        var f = new Fixture();
        var parentPaths = new List<string?>();
        f.Nodes["xref"] = f.Reference("xref", "X", new[] { "ordinary" }, external: true,
            resolve: path => { parentPaths.Add(path); return "C:/local/first.dwg"; });
        f.Nodes["ordinary"] = f.Reference("ordinary", "O", new[] { "nested" });
        f.Nodes["nested"] = f.Reference("nested", "N", new[] { "line" }, external: true,
            resolve: path => { parentPaths.Add(path); return "C:/local/nested/second.dwg"; });
        f.Nodes["line"] = f.Leaf("line", new(1, 2));
        f.Run("xref");
        f.Failures.Should().BeEmpty();
        parentPaths.Should().Equal("C:/local/host.dwg", "C:/local/first.dwg");
        f.Visited.Should().ContainSingle().Which.InXref.Should().BeTrue();
    }

    [Fact]
    public void CycleReportsTheExactHandlePath_AndOtherRootInstancesRemainReadable()
    {
        var f = new Fixture();
        f.Nodes["root"] = f.Reference("root", "A", new[] { "child" });
        f.Nodes["child"] = f.Reference("child", "A", new[] { "hidden" });
        f.Nodes["hidden"] = f.Leaf("hidden", new(0, 0));
        f.Nodes["good"] = f.Leaf("good", new(1, 0));
        f.Run("root", "good");
        f.Failures.Should().ContainSingle().Which.Should().Contain("root/child").And.Contain("Cyclic");
        f.Visited.Select(v => v.Handle).Should().Equal("good");
    }

    [Theory]
    [InlineData(6, false)]
    [InlineData(7, true)]
    public void NestingLimitMatchesTheReader_NotAnUnboundedPartialSuccess(int deepestReference, bool blocked)
    {
        var f = new Fixture();
        for (var i = 0; i <= deepestReference; i++)
            f.Nodes[$"b{i}"] = f.Reference($"b{i}", $"d{i}", new[] { i == deepestReference ? "line" : $"b{i + 1}" });
        f.Nodes["line"] = f.Leaf("line", new(1, 1));
        f.Run("b0");
        f.Failures.Any().Should().Be(blocked);
        f.Visited.Count.Should().Be(blocked ? 0 : 1);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void UnresolvedAndStaleVisibleXrefsNeverBecomeEmptySuccessfulSources(bool unloaded, bool stale)
    {
        var f = new Fixture();
        f.Nodes["xref"] = f.Reference("xref", "X", new[] { "line" }, external: true,
            unavailable: unloaded, resolve: _ => stale ? throw new InvalidOperationException("loaded-xref-stale") : "C:/local/x.dwg");
        f.Nodes["line"] = f.Leaf("line", new(0, 1));
        f.Run("xref");
        f.Visited.Should().BeEmpty();
        f.Failures.Should().ContainSingle().Which.Should().Contain(stale ? "loaded-xref-stale" : "לא טעון או לא פתור");
    }

    [Fact]
    public void MissingExternalIdentityIsRefused()
    {
        var f = new Fixture();
        f.Nodes["xref"] = f.Reference("xref", "X", new[] { "line" }, external: true, resolve: _ => "");
        f.Nodes["line"] = f.Leaf("line", new(0, 1));
        f.Run("xref");
        f.Visited.Should().BeEmpty();
        f.Failures.Should().ContainSingle().Which.Should().Contain("identity is unavailable");
    }

    [Fact]
    public void NestedOverlayIsSkippedBeforeUnavailableCheck_ButTopLevelOverlayIsRead()
    {
        var f = new Fixture();
        f.Nodes["outer"] = f.Reference("outer", "X", new[] { "ordinary" }, external: true);
        f.Nodes["ordinary"] = f.Reference("ordinary", "O", new[] { "nested-overlay", "line" });
        f.Nodes["nested-overlay"] = f.Reference("nested-overlay", "N", new[] { "hidden" }, external: true, overlay: true, unavailable: true);
        f.Nodes["top-overlay"] = f.Reference("top-overlay", "T", new[] { "line" }, external: true, overlay: true);
        f.Nodes["line"] = f.Leaf("line", new(1, 0));
        f.Run("outer", "top-overlay");
        f.Failures.Should().BeEmpty();
        f.Visited.Should().HaveCount(2).And.OnlyContain(v => v.InXref);
    }

    [Fact]
    public void OverlayAncestorHidesItsExternalChildrenButNotItsOwnOrdinaryBlocks()
    {
        var f = new Fixture();
        f.Nodes["top-overlay"] = f.Reference("top-overlay", "T", new[] { "ordinary", "attached" }, external: true, overlay: true);
        f.Nodes["ordinary"] = f.Reference("ordinary", "O", new[] { "line" });
        f.Nodes["attached"] = f.Reference("attached", "A", new[] { "hidden" }, external: true, unavailable: true);
        f.Nodes["line"] = f.Leaf("line", new(1, 0));
        f.Run("top-overlay");
        f.Failures.Should().BeEmpty();
        f.Visited.Should().ContainSingle();
    }

    [Fact]
    public void NonFiniteComposedTransformRefusesGeometryAndRecordsItsHandle()
    {
        var f = new Fixture();
        f.Nodes["bad"] = f.Reference("bad", "B", new[] { "line" }, _ => new(double.NaN, 0));
        f.Nodes["line"] = f.Leaf("line", new(1, 2));
        f.Run("bad");
        f.Visited.Should().BeEmpty();
        f.Failures.Should().ContainSingle().Which.Should().Contain("handle_path=bad").And.Contain("transform");
    }

    [Fact]
    public void ObjectReadAndEnumerationFailuresAreExplicit_NotSilentlyDropped()
    {
        var f = new Fixture();
        f.Nodes["root"] = new Node { Handle = "root", IsReference = true, Definition = "R",
            Transform = p => p, ReadChildren = ThrowingSequence };
        f.Run("root", "missing");
        f.Failures.Should().HaveCount(2);
        f.Failures.Should().Contain(x => x.Contains("enumeration") && x.Contains("handle_path=root"));
        f.Failures.Should().Contain(x => x.Contains("handle_path=missing"));
        static IEnumerable<string> ThrowingSequence() { throw new InvalidOperationException("enumerator failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }

    [Fact]
    public void NativeAdapterWiringBindsLoadedSnapshotAndDependencyHashesToActualWalker()
    {
        var sourceDir = typeof(ProjectSetupDiscoveryTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        var text = File.ReadAllText(Path.Combine(sourceDir, "CivilDelivery", "Sections", "Services", "ProjectSetupScanner.cs"));
        text.Should().Contain("new SectionXrefSnapshotGuard.Cache()").And.Contain("snapshots.Validate(definition, parentPath)")
            .And.Contain("!snapshot.IsFresh").And.Contain("WalkDiscovery(ms.Cast<ObjectId>()")
            // 1.4.1 C2a: the walk composes a frame (transform + effective layer); the transform order stays outer * inner.
            .And.Contain("(outer, inner) => new DiscoveryFrame(outer.Transform * inner.Transform,")
            .And.Contain("scan.DiscoverySourceHashes[path] = hash")
            .And.Contain("RequireDiscoverySourcesUnchanged(sourceHashes)").And.Contain("sideTr.Abort()");
        ProjectSetupScanner.NoAlignmentGuidance.Should().Contain("Data Shortcut").And.Contain("מגאומטריה")
            .And.Contain("XREF אינו ציר זמין").And.Contain("אינה יוצרת ציר");
    }

    private sealed class Fixture
    {
        public Dictionary<string, Node> Nodes { get; } = new();
        public List<(string Handle, Pt2 Point, bool InXref)> Visited { get; } = new();
        public List<string> Failures { get; } = new();
        public Node Leaf(string handle, Pt2 point) => new() { Handle = handle,
            Visit = (transform, external) => Visited.Add((handle, transform(point), external)) };
        public Node Reference(string handle, string definition, string[] children, Func<Pt2, Pt2>? transform = null,
            bool external = false, bool overlay = false, bool unavailable = false, Func<string?, string>? resolve = null) => new()
        {
            Handle = handle, IsReference = true, Definition = definition, Transform = transform ?? (p => p),
            IsExternal = external, IsOverlay = overlay, IsUnavailable = unavailable, Name = definition,
            ReadChildren = () => children, ResolveSource = resolve ?? (_ => "C:/local/" + definition + ".dwg"),
        };
        public void Run(params string[] roots) => ProjectSetupScanner.WalkDiscovery(roots, id => Nodes[id],
            (Func<Pt2, Pt2>)(p => p), (outer, inner) => p => outer(inner(p)),
            transform => { var p = transform(new Pt2(0, 0)); return double.IsFinite(p.X) && double.IsFinite(p.Y); },
            "C:/local/host.dwg", (stage, error) => Failures.Add(stage + ": " + error.Message));
    }
}
