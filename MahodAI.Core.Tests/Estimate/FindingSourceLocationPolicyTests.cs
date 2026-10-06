using System;
using System.Linq;
using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

public sealed class FindingSourceLocationPolicyTests
{
    private const string Hash = "e8c396b44342fd594124880c8e6528e7e954f94c168f1f698cb8dd0433236b9a";
    private const string Path = @"C:\Users\arthurf\MahodCivilDelivery_Work\6422-local-mirror\Civil3d\PD\6422-SP-MEDVA-ALL-2026-MHD.dwg";
    private static ProvenanceRef Source(string? path = Path, string? hash = Hash,
        string? handle = "336457/505C8FE", string? xref = "6422-SP-MEDVA-ALL-2026-MHD") => new()
    {
        SourceKind = "xref", SourcePathOrUri = path, DrawingChecksum = hash,
        SourceHandle = handle, XrefPath = xref, EntityType = "Hatch", MeasurementMethod = "hatch-area",
    };

    [Fact]
    public void Recorded59FailedHatchIsLocatableWithoutAnyQuantityRecordOrBounds()
    {
        var source = Source();
        var issue = new EstimateReviewPolicy.Issue("סריקה", EstimateFindingCodes.MeasurementFailed,
            "81 supported construction objects could not be measured", "eNotApplicable", "Repair and rescan",
            true, EstimateReviewPolicy.Recovery.Inspect, Array.Empty<string>(), Array.Empty<string>())
            { Sources = new[] { source } };
        var targets = FindingSourceLocationPolicy.Collect(new[] { issue });
        targets.Should().ContainSingle();
        targets[0].Source.Should().BeSameAs(source);
        targets[0].UnavailableReason.Should().BeNull();
        issue.Blocking.Should().BeTrue();
        issue.RecordIds.Should().BeEmpty();
    }

    [Fact]
    public void EverySourceOfAnAggregatedFailureRemainsSelectableWithoutRecordIds()
    {
        var sources = Enumerable.Range(1, 81).Select(i => Source(handle: $"336457/{i:X}")).ToArray();
        var issue = new EstimateReviewPolicy.Issue("סריקה", EstimateFindingCodes.MeasurementFailed,
            "81 failures", "", "Repair and rescan", true, EstimateReviewPolicy.Recovery.Inspect,
            Array.Empty<string>(), Array.Empty<string>()) { Sources = sources };
        FindingSourceLocationPolicy.Collect(new[] { issue }).Select(target => target.Source).Should().Equal(sources);
        issue.Sources.Should().HaveCount(81);
        issue.Blocking.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("505C8FE")]
    [InlineData("336457//505C8FE")]
    [InlineData("336457/not-a-handle")]
    [InlineData("336457/0")]
    public void MissingOrMalformedInsertionPathNeverFallsBackToHostLeafHandle(string? handle) =>
        FindingSourceLocationPolicy.Validate(Source(handle: handle)).Should().NotBeNull();

    [Theory]
    [InlineData(@"\\server\project\source.dwg")]
    [InlineData("https://server/source.dwg")]
    [InlineData("source.dwg")]
    [InlineData(@"C:\source.txt")]
    public void NonLocalOrNonDrawingPathsAreUnavailableButNotHidden(string path)
    {
        var target = new FindingSourceLocationPolicy.Target("failure", "source", Source(path: path));
        target.Display.Should().Contain(path);
        target.UnavailableReason.Should().NotBeNull();
    }

    [Fact]
    public void IdentityRequiresExactPathHashAndXrefChain()
    {
        var source = Source();
        FindingSourceLocationPolicy.IdentityFailure(source, Path, Hash, source.XrefPath).Should().BeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, @"C:\other.dwg", Hash, source.XrefPath).Should().NotBeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, Path, new string('a', 64), source.XrefPath).Should().NotBeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, Path, Hash, "other > source").Should().NotBeNull();
        FindingSourceLocationPolicy.Validate(Source(hash: null)).Should().NotBeNull();
        FindingSourceLocationPolicy.Validate(Source(xref: null)).Should().NotBeNull();
    }

    [Fact]
    public void ARealHostReferenceMayUseOnlyItsOwnBareHandle()
    {
        var source = new ProvenanceRef { SourceKind = "drawing", SourcePathOrUri = Path,
            DrawingChecksum = Hash, SourceHandle = "ABC" };
        FindingSourceLocationPolicy.Validate(source).Should().BeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, Path, Hash, null).Should().BeNull();
        FindingSourceLocationPolicy.IdentityFailure(source, Path, Hash, "xref").Should().NotBeNull();
    }

    [Theory]
    [InlineData(DriveType.Network)]
    [InlineData(DriveType.Unknown)]
    [InlineData(DriveType.NoRootDirectory)]
    public void MappedNetworkAndUnknownDrivesAreRefusedWithoutProbingSource(DriveType type)
    {
        var calls = 0;
        FindingSourceLocationPolicy.LocalDriveFailure(@"P:\project\source.dwg", root =>
        {
            calls++; root.Should().Be(@"P:\"); return type;
        }).Should().NotBeNull();
        calls.Should().Be(1);
        FindingSourceLocationPolicy.LocalDriveFailure(Path, _ => DriveType.Fixed).Should().BeNull();
    }

    [Theory]
    [InlineData(@"\\server\project\source.dwg")]
    [InlineData(@"\\?\C:\source.dwg")]
    [InlineData(@"\\.\C:\source.dwg")]
    public void UncAndDevicePathsAreRejectedBeforeEvenQueryingDrive(string path)
    {
        var calls = 0;
        FindingSourceLocationPolicy.LocalDriveFailure(path, _ => { calls++; return DriveType.Network; }).Should().NotBeNull();
        calls.Should().Be(0);
    }
}
