using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;
using D = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.XrefBlockExtentsFailureDiagnostic;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class XrefBlockExtentsFailureDiagnosticTests
{
    // Exact native66 provenance shape; the delegate Block values are synthetic,
    // not a claim that host/source BTR properties were read in Civil by this test.
    private static ProvenanceRef Source(string handle = "8BB299/7788", string? missing = null,
        string? checksum = null) => new()
    {
        SourceKind = missing == "kind" ? "drawing" : "xref",
        SourcePathOrUri = missing == "path" ? null : "C:/TEST/6422-HA-MODEL-NATAZ.dwg",
        DrawingChecksum = missing == "hash" ? null : checksum ?? "8F2469EDD9F9D25FCC09C5563FAC1C2BD9B3CE17A9B06D1E9DAE032CF8175315",
        SourceHandle = missing == "handle" ? "8BB299//7788" : handle,
        XrefPath = missing == "chain" ? null : "6422-HA-MODEL-NATAZ",
        EntityType = missing == "type" ? "Line" : "BlockReference",
        Layer = "0", MeasurementMethod = "geometric-extents", RunId = "test-only",
    };
    private static D.Block Block(string name) => new(
        new[] { new D.Property("name", name, null) },
        new[] { new D.Property("is_from_overlay_reference", true, null) },
        new(Array.Empty<D.Child>(), false, null));

    [Fact]
    public void ExactVerifiedParentComparisonReadsOnlyRequestedLeafAndRetainsBothViews()
    {
        var source = Source();
        string? requested = null;
        var result = D.Capture(source, () => Block("*U2733"), () => source.SourcePathOrUri,
            leaf => { requested = leaf; return Block("6422-GM-MODEL-NATAZ"); });
        requested.Should().Be("7788");
        result.AssociationError.Should().BeNull();
        result.Loaded.Block!.Reference[0].Value.Should().Be("*U2733");
        result.Original.Block!.Reference[0].Value.Should().Be("6422-GM-MODEL-NATAZ");
        result.Loaded.Error.Should().BeNull();
        result.Original.Error.Should().BeNull();
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("path")]
    [InlineData("hash")]
    [InlineData("chain")]
    [InlineData("type")]
    [InlineData("handle")]
    public void UnknownSourceIdentityNeverLooksUpAnOriginalObject(string missing)
    {
        var source = Source(missing: missing);
        var called = false;
        var result = D.Capture(source, () => Block("loaded"),
            () => throw new Exception("filename should not be read"),
            _ => { called = true; return Block("must not read"); });
        called.Should().BeFalse();
        result.Loaded.Block.Should().NotBeNull();
        result.AssociationError.Should().NotBeNullOrWhiteSpace();
        result.Original.Block.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("C:/TEST/different-source.dwg")]
    public void NullOrDifferentBorrowedFilenameCannotBecomeTheOriginal(string? filename)
    {
        var calls = 0;
        var result = D.Capture(Source(), () => Block("loaded"), () => filename,
            _ => { calls++; return Block("wrong"); });
        calls.Should().Be(0);
        result.AssociationError.Should().Contain("filename");
        result.Original.Block.Should().BeNull();
    }

    [Fact]
    public void MissingBorrowedDatabaseIsExplicitAndDoesNotHideTheLoadedView()
    {
        var result = D.Capture(Source(), () => Block("loaded"), null, null);
        result.Loaded.Block.Should().NotBeNull();
        result.AssociationError.Should().Contain("verified loaded parent database unavailable");
    }

    [Fact]
    public void IndependentReadFailuresAndNullReaderStayUnknownNotEmptyOrFalse()
    {
        var result = D.Capture(Source(), () => throw new InvalidOperationException("loaded-open"),
            () => Source().SourcePathOrUri, _ => throw new InvalidOperationException("eKeyNotFound"));
        result.Loaded.Error.Should().Contain("loaded-open");
        result.Original.Error.Should().Contain("eKeyNotFound");
        result.Loaded.Block.Should().BeNull();
        result.Original.Block.Should().BeNull();
        D.Capture(Source(), () => null!, () => throw new Exception("file-getter"), _ => Block("x"))
            .Loaded.Error.Should().Contain("returned null");
    }

    [Fact]
    public void EachPropertyFailureIsRecordedWithoutGuessingFlagsOrDroppingOtherProperties()
    {
        var properties = D.CaptureProperties(
            ("is_from_overlay_reference", () => throw new InvalidOperationException("flag-error")),
            ("is_resolved", () => false), ("path_name", () => null));
        properties[0].Value.Should().BeNull();
        properties[0].Error.Should().Contain("flag-error");
        properties[1].Value.Should().Be(false);
        properties[1].Error.Should().BeNull();
        properties[2].Value.Should().BeNull();
        properties[2].Error.Should().BeNull();
    }

    [Fact]
    public void ChildReadsAreBoundedAndIndividualFailureDoesNotClaimAnEmptyDefinition()
    {
        var calls = 0;
        var children = D.CaptureChildren(() => Enumerable.Range(0, 10000)
            .Select<int, Func<D.Child>>(i => () =>
            {
                calls++;
                if (i == 1) throw new InvalidOperationException("unreadable-child");
                return new(i.ToString("X"), "Line", null);
            }));
        calls.Should().Be(D.MaximumChildren);
        children.Items.Should().HaveCount(D.MaximumChildren);
        children.Truncated.Should().BeTrue();
        children.Items[1].Error.Should().Contain("unreadable-child");
        children.Items[2].Type.Should().Be("Line");
    }

    [Fact]
    public void EnumerationFailureRetainsThePrefixAndItsError()
    {
        IEnumerable<Func<D.Child>> Readers()
        {
            yield return () => new("A1", "Line", null);
            throw new InvalidOperationException("enumeration-failed");
        }
        var result = D.CaptureChildren(Readers);
        result.Items.Should().ContainSingle();
        result.Error.Should().Contain("enumeration-failed");
        result.Truncated.Should().BeFalse(); // Error, not a diagnostic limit.
    }

    [Fact]
    public void BatchDeduplicatesFullIdentityCapsReadsAndPreservesOriginalFailure()
    {
        var batch = new D.Batch();
        var calls = 0;
        D.Snapshot Read() { calls++; return D.Capture(Source(), () => Block("loaded"), null, null); }
        batch.Add(Source(), "eNullExtents", Read);
        batch.Add(Source(), "repeat", Read);
        for (var i = 1; i < D.MaximumSources + 5; i++)
            batch.Add(Source("8BB299/" + (0x8000 + i).ToString("X")), "eNullExtents", Read);
        calls.Should().Be(D.MaximumSources);
        batch.Sources.Should().HaveCount(D.MaximumSources);
        batch.DuplicateAttempts.Should().Be(1);
        batch.OmittedByDiagnosticLimit.Should().Be(5);
        batch.Sources[0].Source.SourceHandle.Should().Be("8BB299/7788");
        batch.Sources[0].OriginalExtentsError.Should().Be("eNullExtents");
        batch.Purpose.Should().Contain("no quantity or classification decision");
    }

    [Fact]
    public void SameLeafInDifferentXrefInstanceOrRevisionIsNotDeduplicated()
    {
        var batch = new D.Batch();
        var revised = Source(checksum: new string('b', 64));
        foreach (var source in new[] { Source(), Source("8BB300/7788"), revised })
            batch.Add(source, "eNullExtents", () => D.Capture(source, () => Block("loaded"), null, null));
        batch.Sources.Should().HaveCount(3);
        batch.DuplicateAttempts.Should().Be(0);
    }

    [Fact]
    public void UnexpectedDiagnosticFailureCannotChangeTheQuantityOutcome()
    {
        var batch = new D.Batch();
        batch.Add(Source(), "original-eNullExtents", () => throw new InvalidOperationException("diagnostic-error"));
        batch.Sources.Single().OriginalExtentsError.Should().Be("original-eNullExtents");
        batch.Sources.Single().Evidence.Loaded.Error.Should().Contain("diagnostic-error");
    }

    [Fact]
    public void DiagnosticIsSeparateFromScanRecordsAndFindings()
    {
        var scan = new EstimateWorkflowService.ScanResult
        {
            RunId = "test-only", ProjectProfileId = "test-only", ProfileSource = "test-only",
            SourceDrawing = "C:/TEST/host.dwg", XrefBlockDiagnostics = new(),
        };
        scan.XrefBlockDiagnostics.Add(Source(), "UNIQUE-DIAGNOSTIC", () =>
            D.Capture(Source(), () => Block("loaded"), null, null));
        JsonSerializer.Serialize(scan).Should().NotContain("UNIQUE-DIAGNOSTIC");
        JsonSerializer.Serialize(scan.XrefBlockDiagnostics).Should().Contain("UNIQUE-DIAGNOSTIC");
        scan.Findings.Should().BeEmpty();
        scan.Records.Should().BeEmpty();
    }

    [Fact]
    public void NativeWiringUsesOnlyBorrowedVerifiedDatabaseAndTheExistingEvidencePublication()
    {
        // Source wiring, not execution of Autodesk getters. All tests above execute
        // the real pure diagnostic policy; native comparison awaits the next scan.
        var root = typeof(XrefBlockExtentsFailureDiagnosticTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MahodPluginSourceDir").Value!;
        var folder = Path.Combine(root, "CivilDelivery", "Estimate");
        var helper = File.ReadAllText(Path.Combine(folder, "XrefBlockExtentsFailureDiagnostic.cs"));
        helper.Should().Contain("GetObjectId(false,").And.Contain("OpenMode.ForRead")
            .And.Contain("sourceTransaction.Abort()")
            .And.NotContain("ReadDwgFile(").And.NotContain("GetXrefDatabase(")
            .And.NotContain("ResolveXrefs(").And.NotContain("verifiedLoadedParent.Dispose(")
            .And.NotContain("GeometricExtents").And.NotContain("OpenMode.ForWrite");
        var extraction = File.ReadAllText(Path.Combine(folder, "CivilQuantityExtractionService.cs"));
        extraction.Should().Contain("if (source.IsExternal && ent is BlockReference failedReference)");
        var verified = extraction.IndexOf("var freshnessFailure = XrefQuantityPolicy.LoadedSnapshotFailure", StringComparison.Ordinal);
        var borrowed = extraction.IndexOf("chain, true, loadedDatabase,", StringComparison.Ordinal);
        borrowed.Should().BeGreaterThan(verified);
        extraction[borrowed..].Split(';')[0].Should().Contain("definition.Name, scopeKey)");
        extraction[verified..borrowed].Should().Contain("if (freshnessFailure != null)").And.Contain("return;");
        var workflow = File.ReadAllText(Path.Combine(folder, "EstimateWorkflowService.cs"));
        workflow.Should().Contain("result.XrefBlockDiagnostics = extraction.XrefBlockDiagnostics")
            .And.Contain("XrefBlockExtentsFailureDiagnostic.ArtifactName, blockDiagnostics, pendingRoot)");
    }
}
