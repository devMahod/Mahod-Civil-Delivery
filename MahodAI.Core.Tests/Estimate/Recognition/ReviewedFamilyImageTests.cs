using System;
using System.Collections.Generic;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

// The three binding tests of the Codex proposal (27.09), adopted unchanged apart from the namespace. Pure binding
// behaviour: they do not imitate Clipboard/WPF/Civil. Records and images are SYNTHETIC.
public sealed class ReviewedFamilyImageTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly DateTimeOffset GrantTime = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitConsent_SendsExactReviewedImageOnce_AndDoesNotAssertItsOrigin()
    {
        var group = Group();
        var image = Image(VisionImagePolicy.LegendCrop, false);
        var reviewed = Bind(group, image, FamilyImageOrigin.UserSuppliedLegend);
        Assert.Contains("לא אומתו", reviewed.Disclosure);
        Assert.False(reviewed.TryTakePayload(group, Library, null, image, false, "synthetic-user", GrantTime, out var denied));
        Assert.Null(denied);
        Assert.True(reviewed.TryTakePayload(group, Library, null, image, true, "synthetic-user", GrantTime, out var payload));
        Assert.Same(image, Assert.Single(payload!.Images));
        var request = FamilyRecognitionAssist.Prepare(group, Library, null, new[] { image }).Request!;
        Assert.True(VisionImagePolicy.IsPermitted(request, payload));
        Assert.False(reviewed.TryTakePayload(group, Library, null, image, true, "synthetic-user", GrantTime, out var reused));
        Assert.Null(reused);
    }

    [Fact]
    public void SamePixelsForOtherSourceOrChangedContext_AndReplacedImage_RejectOldReview()
    {
        var group = Group();
        var image = Image(VisionImagePolicy.LegendCrop, false);
        var reviewed = Bind(group, image, FamilyImageOrigin.UserSuppliedLegend);
        Assert.False(reviewed.IsCurrent(Group("other-source"), Library, null, image));
        Assert.False(reviewed.IsCurrent(group, Library, "אבני שפה בכביש", image));
        Assert.False(reviewed.IsCurrent(group, Library, null, Image(VisionImagePolicy.LegendCrop, true)));
        Assert.False(reviewed.TryTakePayload(Group("other-source"), Library, null, image, true, "synthetic-user", GrantTime, out _));
        // New explicit review of the new selection is a usable recovery, not permanent refusal.
        var replacement = Image(VisionImagePolicy.LegendCrop, true);
        var current = Bind(group, replacement, FamilyImageOrigin.UserSuppliedLegend);
        Assert.NotEqual(reviewed.ContextId, current.ContextId);
        Assert.True(current.TryTakePayload(group, Library, null, replacement, true, "synthetic-user", GrantTime, out _));
    }

    [Fact]
    public void SchematicIsLabeledAndCannotMasqueradeAsUserSuppliedLegend_AbsenceDoesNotAuthorize()
    {
        var group = Group();
        var image = Image(VisionImagePolicy.GroupPreview, false);
        var reviewed = Bind(group, image, FamilyImageOrigin.SchematicGroup);
        Assert.Contains("סכמטית", reviewed.Disclosure);
        Assert.Contains("לא צילום", reviewed.Disclosure);
        Assert.Null(ReviewedFamilyImage.Bind(group, Library, null, image, FamilyImageOrigin.UserSuppliedLegend, out _));
        Assert.False(reviewed.IsCurrent(group, Library, null, null));
        Assert.False(reviewed.TryTakePayload(group, Library, null, null, true, "synthetic-user", GrantTime, out _));
        Assert.True(reviewed.TryTakePayload(group, Library, null, image, true, "synthetic-user", GrantTime, out _));
        // Missing images are not an admission requirement for the existing text route.
        Assert.NotNull(FamilyRecognitionAssist.Prepare(group, Library, null).Request);
    }

    private static ReviewedFamilyImage Bind(RecognitionGroupInput group, VisionImage image, FamilyImageOrigin origin)
        => Assert.IsType<ReviewedFamilyImage>(ReviewedFamilyImage.Bind(group, Library, null, image, origin, out _));

    private static VisionImage Image(string kind, bool alternate)
    {
        IReadOnlyList<(double X, double Y)> points = alternate ? new[] { (0d, 0d), (1d, 1d) } : new[] { (0d, 0d), (1d, 0d), (1d, 1d) };
        var bytes = GroupPreviewRenderer.Render(new[] { points }, 64)!;
        Assert.True(VisionImagePolicy.TryCreate(bytes, kind, out var image, out var reason), reason);
        return image!;
    }

    private static RecognitionGroupInput Group(string source = "synthetic-source")
        => new("synthetic-group", source, DraftSourceRole.Design, "asdasd23423", "length", "m", "open", null,
            new[] { new NeutralQuantityRecord { RecordId = "record-A", ProjectProfileId = "synthetic-profile", RunId = "synthetic-run",
                Source = new QuantitySource { Drawing = source, DrawingHash = new string('a', 64), Handle = "A", Layer = "asdasd23423", EntityType = "LWPOLYLINE" },
                Measurement = new QuantityMeasurement {
                Kind = "length", Method = "polyline-length", Unit = "m", RawValue = 20,
                Parameters = new Dictionary<string, string> {
                    [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + "_status"] = "read",
                    [EvidenceKeys.NearbyText] = "[{\"text\":\"אבן שפה\"}]", [EvidenceKeys.NearbyText + "_status"] = "read"
                } } } });
}
