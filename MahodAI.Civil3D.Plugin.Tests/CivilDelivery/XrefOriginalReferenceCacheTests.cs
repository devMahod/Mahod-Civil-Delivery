using System;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Xunit;
using C = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.XrefOriginalReferenceCache;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class XrefOriginalReferenceCacheTests
{
    // Detached values from V3 native66's GM/HA/SM comparisons on 2026-09-11.
    // These policy tests do not open DWGs or claim native cache/transaction coverage.
    private static C.ReferenceIdentity Reference(string handle = "102BCB", string layer = "0-XREF") =>
        new(handle, "1F", layer, new(0, 0, 0), new(0, 0, 1), new(1, 1, 1), 0);
    private static C.DefinitionIdentity Overlay() => new("6422-SR-SURVEY-MD17615-MHD",
        @"..\..\..\Background\PD\6422-SR-SURVEY-MD17615-MHD.dwg", true, true, false, false);
    private static C.Result Evaluate(C.ReferenceIdentity? loaded = null, C.ReferenceIdentity? original = null,
        C.DefinitionIdentity? definition = null, string prefix = "6422-GM-MODEL-NATAZ",
        bool inside = true, bool ancestorOverlay = false, string expected = "102BCB") =>
        C.EvaluateOriginal(loaded ?? Reference(layer: prefix + "|0-XREF"), original ?? Reference(),
            definition ?? Overlay(), expected, prefix, inside, ancestorOverlay);

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void IneligibleDefinitionIsNotEnumerated(bool anonymous, bool external, bool overlay, bool layout)
    {
        var calls = 0;
        var result = C.CheckCandidate(anonymous, external, overlay, layout, () => { calls++; return false; });
        calls.Should().Be(0);
        result!.Decision.Should().Be(C.Decision.NotApplicable);
    }

    [Fact]
    public void OnlyProvenEmptyAnonymousDefinitionIsCandidateAndEnumerationErrorIsExplicit()
    {
        C.CheckCandidate(true, false, false, false, () => false).Should().BeNull();
        C.CheckCandidate(true, false, false, false, () => true)!.Decision.Should().Be(C.Decision.NotApplicable);
        var failure = C.CheckCandidate(true, false, false, false,
            () => throw new InvalidOperationException("eInvalidInput"));
        failure!.Decision.Should().Be(C.Decision.Refused);
        failure.Detail.Should().Contain("enumeration failed").And.Contain("eInvalidInput");
    }

    [Theory]
    [InlineData("6422-GM-MODEL-NATAZ", "102BCB", "0-XREF", "6422-GM-MODEL-NATAZ|0-XREF")]
    [InlineData("6422-HA-MODEL-NATAZ", "1116A4", "HW_HA_SIDEWALK", "6422-HA-MODEL-NATAZ|HW_HA_SIDEWALK")]
    [InlineData("6422-HA-MODEL-NATAZ", "7788", "0", "0")]
    [InlineData("6422-HA-MODEL-NATAZ", "7CE1", "0", "0")]
    [InlineData("6422-SM-MODEL-NATAZ", "170D5D", "TR-MARK-WHT-808-3-3", "6422-SM-MODEL-NATAZ|TR-MARK-WHT-808-3-3")]
    public void ProvenNativeAssociationShapesRetainLayerZeroAndUseExistingOverlayDisposition(
        string prefix, string handle, string originalLayer, string loadedLayer)
    {
        var result = Evaluate(Reference(handle, loadedLayer), Reference(handle, originalLayer),
            prefix: prefix, expected: handle);
        result.Decision.Should().Be(C.Decision.ExcludeByOverlay);
        result.Detail.Should().Contain("leaf=" + handle);
    }

    [Fact]
    public void OrdinaryEmptyOriginalIsNotSkippedEvenBelowOverlayAncestor()
    {
        var ordinary = Overlay() with { Name = "*U42", Path = "", IsExternal = false,
            IsOverlay = false, IsAnonymous = true };
        Evaluate(definition: ordinary, ancestorOverlay: true).Decision.Should().Be(C.Decision.NotApplicable);
    }

    [Fact]
    public void VisibleAttachedOriginalIsRefusedNotCountedOrExcluded()
    {
        var attached = Overlay() with { IsOverlay = false };
        Evaluate(definition: attached).Decision.Should().Be(C.Decision.Refused);
        Evaluate(definition: attached, ancestorOverlay: true).Decision.Should().Be(C.Decision.ExcludeByOverlay);
        Evaluate(inside: false).Decision.Should().Be(C.Decision.NotApplicable);
    }

    [Theory]
    [InlineData("other-source|0-XREF")]
    [InlineData("ancestor|6422-GM-MODEL-NATAZ|0-XREF")]
    [InlineData("6422-GM-MODEL-NATAZ|different-layer")]
    [InlineData("0")]
    [InlineData("")]
    public void WrongPrefixOrLayerCannotBeStrippedGenerically(string layer) =>
        Evaluate(loaded: Reference(layer: layer)).Decision.Should().Be(C.Decision.Refused);

    [Theory]
    [InlineData("leaf")]
    [InlineData("owner")]
    [InlineData("position")]
    [InlineData("normal")]
    [InlineData("scale")]
    [InlineData("rotation")]
    [InlineData("nan")]
    [InlineData("zero-normal")]
    [InlineData("zero-scale")]
    public void AnyUnprovenReferenceMetadataRefusesTheRecovery(string mismatch)
    {
        var original = Reference();
        original = mismatch switch
        {
            "leaf" => original with { Handle = "102BCD" },
            "owner" => original with { OwnerHandle = "8BB339" }, // host parent BTR is not source owner1F
            "position" => original with { Position = new(1e-15, 0, 0) },
            "normal" => original with { Normal = new(0, 0, -1) },
            "scale" => original with { Scale = new(1, 1, -1) },
            "rotation" => original with { Rotation = 1e-15 },
            "nan" => original with { Rotation = double.NaN },
            "zero-normal" => original with { Normal = new(0, 0, 0) },
            "zero-scale" => original with { Scale = new(1, 0, 1) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        Evaluate(original: original).Decision.Should().Be(C.Decision.Refused);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(2)]
    public void OnlyExactFullRevolutionsAreEquivalent(int turns) =>
        Evaluate(original: Reference() with { Rotation = turns * 2 * Math.PI })
            .Decision.Should().Be(C.Decision.ExcludeByOverlay);

    [Theory]
    [InlineData("layout")]
    [InlineData("anonymous-external")]
    [InlineData("missing-path")]
    [InlineData("missing-name")]
    [InlineData("inconsistent-external")]
    public void ExternalDefinitionMetadataMustBeComplete(string missing)
    {
        var definition = missing switch
        {
            "layout" => Overlay() with { IsLayout = true },
            "anonymous-external" => Overlay() with { IsAnonymous = true },
            "missing-path" => Overlay() with { Path = "" },
            "missing-name" => Overlay() with { Name = "" },
            "inconsistent-external" => Overlay() with { IsExternal = false },
            _ => throw new ArgumentOutOfRangeException(nameof(missing)),
        };
        Evaluate(definition: definition).Decision.Should().Be(C.Decision.Refused);
    }

    [Theory]
    [InlineData("8BB552/102BCB")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("unknown")]
    public void ExpectedHandleMustBeSinglePositiveLeaf(string expected) =>
        Evaluate(expected: expected).Decision.Should().Be(C.Decision.Refused);

    [Theory]
    [InlineData(@"\\server\share\source.dwg")]
    [InlineData(@"\\?\C:\source.dwg")]
    [InlineData(@"C:source.dwg")]
    [InlineData(@"C:\source.dwg:stream")]
    [InlineData("relative.dwg")]
    public void NonLocalOrAmbiguousPathIsRejectedBeforeAnyFileAccess(string path)
    {
        Action action = () => C.CanonicalLocalPath(path);
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void HashContractRequiresAllSixtyFourHexDigits()
    {
        C.IsSha256(new string('a', 64)).Should().BeTrue();
        C.IsSha256(new string('g', 64)).Should().BeFalse();
        C.IsSha256(new string('a', 63)).Should().BeFalse();
        C.IsSha256(null).Should().BeFalse();
    }

    [Fact]
    public void CacheHasNoNativeWriteOrResolveRouteAndHashesPerSourceWithMandatoryFinalGate()
    {
        var text = File.ReadAllText(Path.Combine(SourceFolder(), "XrefOriginalReferenceCache.cs"));
        text.Should().Contain("FileShare.Read, allowCPConversion: false, password: \"\"");
        text.Should().Contain("OpenMode.ForRead");
        text.Should().Contain("_refusedSources.TryGetValue");
        text.Should().Contain("ValidateSourcesUnchanged()");
        text.Should().NotContain("ResolveXrefs(");
        text.Should().NotContain("WorkingDatabase");
        text.Should().NotContain("OpenMode.ForWrite");
        text.Should().NotContain(".SaveAs(");
        text.Should().NotContain("verifiedLoadedParentDatabase.Dispose");
        text.IndexOf("drive.DriveType", StringComparison.Ordinal).Should().BeLessThan(
            text.IndexOf("File.GetAttributes(cursor)", StringComparison.Ordinal));
    }

    [Fact]
    public void RedirectedDefinitionIdentityRequiresValidActualIdsOnBothSidesNotHandleEquality()
    {
        // Source contract only: positive forwarding semantics require the native
        // V5 probe. This does not simulate or claim an Autodesk ID conversion.
        var text = File.ReadAllText(Path.Combine(SourceFolder(), "XrefOriginalReferenceCache.cs"));
        text.Should().Contain("var requestedDefinitionId = loadedReference.BlockTableRecord;")
            .And.Contain("var openedDefinitionId = loadedDefinition.ObjectId;")
            .And.Contain("if (!LoadedDefinitionIdsMatch(requestedDefinitionId, openedDefinitionId))");
        var start = text.IndexOf("internal static bool LoadedDefinitionIdsMatch", StringComparison.Ordinal);
        var end = text.IndexOf("internal static Result? CheckCandidate", start, StringComparison.Ordinal);
        var identityGuard = text[start..end];
        identityGuard.Should().Contain("if (!UsableId(requested) || !UsableId(opened)) return false;")
            .And.Contain("requested.ConvertToRedirectedId()")
            .And.Contain("opened.ConvertToRedirectedId()")
            .And.Contain("UsableId(redirectedRequested) && UsableId(redirectedOpened)")
            .And.Contain("redirectedRequested == redirectedOpened")
            .And.Contain("!id.IsNull && id.IsValid && !id.IsErased && !id.IsEffectivelyErased")
            .And.NotContain(".Handle").And.NotContain(".Name");
    }

    [Fact]
    public void TraversalRecoversBeforeOrdinaryMeasurementAndPublishesSeparateProvenanceAfterFinalGate()
    {
        var extraction = File.ReadAllText(Path.Combine(SourceFolder(), "CivilQuantityExtractionService.cs"));
        var query = extraction.IndexOf("originalReferences.Query(", StringComparison.Ordinal);
        query.Should().BeGreaterThan(0);
        var consider = extraction.IndexOf("Consider(reference,", query, StringComparison.Ordinal);
        consider.Should().BeGreaterThan(query);
        extraction[query..consider].Should().Contain("Decision.ExcludeByOverlay")
            .And.Contain("Decision.Refused").And.Contain("RecoveredOverlayReferences.Add");
        var final = extraction.IndexOf("originalReferences.ValidateSourcesUnchanged()", StringComparison.Ordinal);
        final.Should().BeGreaterThan(consider);
        extraction[final..].Should().Contain("if (recoveredSourcesFailure != null)")
            .And.Contain("EstimateFindingCodes.XrefTraversalUnresolved");
        var workflow = File.ReadAllText(Path.Combine(SourceFolder(), "EstimateWorkflowService.cs"));
        workflow.Should().Contain("result.RecoveredOverlayReferences = extraction.RecoveredOverlayReferences")
            .And.Contain("\"xref_overlay_recovery_evidence.json\", recoveredOverlays, pendingRoot)");
    }

    private static string SourceFolder() => Path.Combine(
        typeof(XrefOriginalReferenceCacheTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!, "CivilDelivery", "Estimate");
}
