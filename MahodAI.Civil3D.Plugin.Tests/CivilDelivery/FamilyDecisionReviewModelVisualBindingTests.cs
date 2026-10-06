using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The review model's side of the visual binding, ported from Codex's offline persistence harness
/// (family-vision-persistence/CODEX_Test.cs): two groups proposed from images only stay two batches, each with its
/// own exact binding and no invented CAD evidence keys, and the citation notice describes the exact visual scope
/// (a changed member needs a new review), not the legacy layer-only permanence. Real draft builder and batch
/// builder; a fake provider stands in for the image model; no WPF control. All data is SYNTHETIC.
/// </summary>
public sealed class FamilyDecisionReviewModelVisualBindingTests
{
    [Fact]
    public async Task TwoImageOnlyGroupsKeepSeparateBindingBatchesWithoutFabricatedEvidence()
    {
        var library = EngineerBoqLibrary.RoadsV1;
        var seed = Group("x7q");
        var png = GroupPreviewRenderer.Render(GroupPreviewRenderer.Samples(seed.Records), 64)!;
        VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out var why).Should().BeTrue(why);
        var prepared = FamilyRecognitionAssist.Prepare(seed, library, null, new[] { image! }).Request!;
        var payload = new VisionPayload(new[] { image! },
            new VisionSendPermit(prepared.ContextId, new[] { image!.Sha256 }, "SYNTHETIC-REVIEW", DateTimeOffset.UtcNow));
        var proposal = (await new FamilyRecognitionAssist(new FakeProvider()).AssistAsync(seed, library, null,
            Array.Empty<RecognitionProposal>(), null, payload, default)).Single();

        var records = new List<NeutralQuantityRecord> { Record("UI-1", "A1", "x7q"), Record("UI-2", "B1", "x8q") };
        var draft = EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(),
            new CatalogSnapshot { SnapshotId = "NO-CATALOG", FileHash = "none" }, null, library,
            new EngineerDraftContext("synthetic", "synthetic", "run", "synthetic.dwg", "none", Array.Empty<string>(),
                Classifier: LocalFamilyClassifier.Instance));
        var model = FamilyDecisionReviewModel.Create(draft);
        model.Rows.Should().HaveCount(2);
        foreach (var row in model.Rows)
        {
            var binding = FamilyVisualBindingPolicy.Capture(row.Group, new[] { new FamilyRankImage(image.Sha256, image.Kind) });
            var rowProposal = proposal with
            {
                GroupId = row.Group.GroupId, RecordIds = row.Group.Records.Select(r => r.RecordId).ToArray(), VisualBinding = binding,
            };
            row.ApplyAssistant(new[] { rowProposal }, s => s);
            row.ChosenFamily = row.FamilyOptions.Single(f => f.FamilyId == "curb-road");
            row.IsSelected = true;
        }

        var batches = model.SelectedBatches();
        batches.Should().HaveCount(2);
        batches.Should().OnlyContain(b => b.Groups.Count == 1 && b.VisualBinding != null && b.EvidenceKeys.Count == 0);
        model.CitationNotice().Should().Contain("יחייב בדיקה מחדש").And.NotContain("לא יסמן");
    }

    private static RecognitionGroupInput Group(string layer) =>
        new("G", EngineerBoqDraftBuilder.HostSource, DraftSourceRole.Host, layer, "length", "m", "open", null,
            new List<NeutralQuantityRecord> { Record("R1", "AA", layer) });

    private static NeutralQuantityRecord Record(string id, string handle, string layer) => new()
    {
        RecordId = id,
        ProjectProfileId = "SYNTHETIC-VISUAL",
        RunId = "run",
        Source = new QuantitySource
        {
            Drawing = "synthetic.dwg", DrawingPath = "C:/SYNTHETIC/DO-NOT-OPEN/synthetic.dwg", DrawingHash = new string('a', 64),
            Handle = handle, EntityType = "LWPOLYLINE", Layer = layer,
        },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "polyline-length", RawValue = 1, Unit = "m",
            Parameters = new()
            {
                [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + "_status"] = "read",
                [EvidenceKeys.GeometrySample] = "{\"points\":[[0,0],[1,0],[1,1]],\"closed\":false}",
                [EvidenceKeys.GeometrySample + "_status"] = "read",
            },
        },
    };

    private sealed class FakeProvider : IFamilyRecognitionProvider, IFamilyVisionGate
    {
        public bool IsVisionAllowed() => true;

        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken ct) =>
            Task.FromResult(new FamilyRankResponse(request.ContextId, request.LibraryHash,
                new[]
                {
                    new FamilyRankChoice("curb-road", "השערה סינתטית בלבד",
                        new[] { new FamilyRankCitation("image:" + request.Images[0].Sha256, "השערה חזותית לבדיקה בלבד") }),
                }, null));
    }
}
