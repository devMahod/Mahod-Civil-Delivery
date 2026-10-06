using System;
using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class CorridorFindingSourceLocationTests
{
    private const string PathValue = @"C:\SYNTHETIC-ONLY\host.dwg";
    private static readonly string Hash = new('a', 64);
    private static ProvenanceRef Source(string? path = PathValue, string? hash = null,
        string? handle = "BD9393", string? type = "CORRIDOR", string? method = "civil-model-quantity",
        string? xref = null, string? subentity = null, double[]? transform = null) => new()
    {
        SourceKind = "civil-model", SourcePathOrUri = path, DrawingChecksum = hash ?? Hash,
        SourceHandle = handle, EntityType = type, MeasurementMethod = method,
        XrefPath = xref, SourceSubentityPath = subentity, XrefTransform = transform,
    };

    [Fact]
    public void WholeCorridorWithExactHostIdentityIsLocatableWithoutCreatingAnyQuantity()
    {
        var source = Source();
        FindingSourceLocationPolicy.Validate(source).Should().BeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, PathValue, Hash, null).Should().BeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, @"C:\SYNTHETIC-ONLY\other.dwg", Hash, null).Should().NotBeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, PathValue, new string('b', 64), null).Should().NotBeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, PathValue, Hash, "other").Should().NotBeNull();
        source.SourceSubentityPath.Should().BeNull();
    }

    [Theory]
    [InlineData("Alignment")]
    [InlineData("SampleLineGroup")]
    [InlineData("Section")]
    [InlineData("Polyline")]
    [InlineData(null)]
    public void OtherCivilOrUnknownTypesAreDetailOnly(string? type) =>
        FindingSourceLocationPolicy.Validate(Source(type: type)).Should().NotBeNull();

    [Theory]
    [InlineData("context")]
    [InlineData("B1/material=Base/station=100")]
    public void SubentityContextIsNeverUsedToLocateParentOrGuessChild(string context) =>
        FindingSourceLocationPolicy.Validate(Source(subentity: context)).Should().NotBeNull();

    [Fact]
    public void MissingIdentityNestedReferenceTransformOrDifferentMethodRemainsRefused()
    {
        foreach (var source in new[]
        {
            Source(path: null), Source(hash: ""), Source(handle: null), Source(handle: "0"),
            Source(handle: "AB/CD"), Source(xref: "GM"), Source(transform: new double[16]),
            Source(method: null), Source(method: "civil-model-quantity:open"), Source(method: "hatch-area"),
        }) FindingSourceLocationPolicy.Validate(source).Should().NotBeNull();
    }

    [Fact]
    public void SourceOnlyNativeAdapterChecksActualCorridorBeforeBoundsAndHasNoHandleFallback()
    {
        // Wiring evidence only: this does not load Civil or prove native Corridor ownership.
        var directory = RepositoryRoot();
        var source = File.ReadAllText(System.IO.Path.Combine(directory, "MahodAI.Civil3D.Plugin",
            "CivilDelivery", "Estimate", "FindingSourceLocatorService.cs"));
        var check = source.IndexOf("entity is not Autodesk.Civil.DatabaseServices.Corridor", StringComparison.Ordinal);
        check.Should().BeGreaterThan(source.IndexOf("if (identityFailure != null)", StringComparison.Ordinal));
        check.Should().BeLessThan(source.IndexOf("var extents = entity!.GeometricExtents;", StringComparison.Ordinal));
        source.Should().Contain("table[BlockTableRecord.ModelSpace]")
            .And.NotContain("GetObjectId(").And.NotContain("OpenMode.ForWrite");
    }

    private static string RepositoryRoot([CallerFilePath] string testFile = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(testFile)!, "..", ".."));
}
