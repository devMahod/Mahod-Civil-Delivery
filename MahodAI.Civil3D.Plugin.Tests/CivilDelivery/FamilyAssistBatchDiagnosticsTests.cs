using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

// FamilyAssistProviderAdmission is process-wide: batch runs must not overlap with each other.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FamilyAssistAdmissionCollection
{
    public const string Name = "FamilyAssistProviderAdmission";
}

/// <summary>
/// Batch diagnostics V1, ported from Codex's isolated harness (family-batch-diagnostics/CODEX_Test.cs, DFFE2220):
/// the summary separates proposals from abstentions, failures, blocked groups and the unsent tail; a provider reason
/// is shown only when it is bound to the request and public; the local receipt carries no identifiers, prompt or
/// provider text and is bounded without deleting anything. Real draft builder, manifest and batch runner; a fake
/// provider stands in for the assistant; no WPF window, Civil or network. All data is SYNTHETIC. Counts are
/// diagnostics, not product acceptance: nothing here selects or approves a family.
/// </summary>
[Collection(FamilyAssistAdmissionCollection.Name)]
public sealed class FamilyAssistBatchDiagnosticsTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private const string Context = "SYNTHETIC curb hypothesis";

    [Fact]
    public async Task APublicBoundAbstentionIsShownAsAReasonNotAProposal()
    {
        var proposal = await Ask(Model(2).Rows.First(), r => Abstain(r, "The available evidence does not distinguish these families"));

        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.MissingDetails.Should().ContainSingle().Which.Should().Contain("The available evidence");
        FamilyRecognitionAssist.PublicOutcomeCode(proposal).Should().Be("provider_abstained");
    }

    [Theory]
    [InlineData("Read C:\\Users\\private-person\\file.dwg")]
    [InlineData("X=204539.12 Y=649342.40")]
    [InlineData("api_key=secretvalue")]
    [InlineData("quantity 42")]
    [InlineData("price 20")]
    [InlineData("line\nnew")]
    public async Task APrivateOrMalformedAbstentionStaysGeneric(string reason)
    {
        var proposal = await Ask(Model(2).Rows.First(), r => Abstain(r, reason));
        proposal.MissingDetails.Should().NotContain(detail => detail.Contains(reason));
    }

    [Fact]
    public async Task AnOverlongAbstentionStaysGeneric()
    {
        var reason = new string('x', 241);
        var proposal = await Ask(Model(2).Rows.First(), r => Abstain(r, reason));
        proposal.MissingDetails.Should().NotContain(detail => detail.Contains(reason));
    }

    [Fact]
    public async Task AForeignContextOrLibraryCannotSurfaceItsReason()
    {
        var row = Model(2).Rows.First();
        (await Ask(row, r => Abstain(r, "foreign reason") with { ContextId = "foreign" }))
            .MissingDetails.Should().NotContain(detail => detail.Contains("foreign reason"));
        (await Ask(row, r => Abstain(r, "foreign library") with { LibraryHash = "foreign" }))
            .MissingDetails.Should().NotContain(detail => detail.Contains("foreign library"));
    }

    [Fact]
    public async Task AnExactClosedCandidateCitationRemainsOnlyProposed()
    {
        var proposal = await Ask(Model(2).Rows.First(), r => new(r.ContextId, r.LibraryHash, new[]
        {
            new FamilyRankChoice("curb-road", "SYNTHETIC test interpretation",
                new[] { new FamilyRankCitation("engineer_context", "curb hypothesis") }),
        }));

        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.FamilyId.Should().Be("curb-road");
    }

    [Fact]
    public async Task TwoReturnedAbstentionsCountAsZeroProposedFamilies()
    {
        var model = Model(2);
        var provider = new FamilyRecognitionAssist(new Fake(r => Abstain(r, "Not enough grounded subject evidence")));
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(model.Rows), Library,
            (g, l, c, v, t) => provider.AssistAsync(g, Library, null, l, c, v, t), true, null, default);

        var counts = FamilyAssistBatchDiagnostics.Capture(2, outcomes).Counts;
        counts.AssistantDispatches.Should().Be(2);
        counts.Abstained.Should().Be(2);
        counts.ReturnedWithProposal.Should().Be(0);
    }

    [Fact]
    public async Task ACoreCaughtProviderErrorIsFailedNotASemanticAbstentionAndLeaksNothing()
    {
        var model = Model(2);
        var row = model.Rows.First();
        var unavailable = (await new FamilyRecognitionAssist(new Fake(_ => throw new InvalidOperationException("C:/private/provider-body")))
            .AssistAsync(row.Group, Library, null, new[] { row.Proposal }, Context, null, default)).Single();
        var provider = new FamilyRecognitionAssist(new Fake(r => Abstain(r, "Not enough grounded subject evidence")));
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(model.Rows), Library,
            (g, l, c, v, t) => provider.AssistAsync(g, Library, null, l, c, v, t), true, null, default);

        var counts = FamilyAssistBatchDiagnostics.Capture(1, new[] { outcomes[0] with { Proposals = new[] { unavailable } } }).Counts;
        counts.Failed.Should().Be(1);
        counts.Abstained.Should().Be(0);
        unavailable.MissingDetails.Should().NotContain(detail => detail.Contains("private"));
    }

    [Fact]
    public async Task PositiveAndAbstainedCountsAreSeparated()
    {
        var model = Model(2);
        var positive = await Ask(model.Rows.First(), r => new(r.ContextId, r.LibraryHash, new[]
        {
            new FamilyRankChoice("curb-road", "SYNTHETIC test interpretation",
                new[] { new FamilyRankCitation("engineer_context", "curb hypothesis") }),
        }));
        var provider = new FamilyRecognitionAssist(new Fake(r => Abstain(r, "Not enough grounded subject evidence")));
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(model.Rows), Library,
            (g, l, c, v, t) => provider.AssistAsync(g, Library, null, l, c, v, t), true, null, default);

        FamilyAssistBatchDiagnostics.Capture(2, new[] { outcomes[0] with { Proposals = new[] { positive } }, outcomes[1] })
            .Counts.ReturnedWithProposal.Should().Be(1);
    }

    [Fact]
    public async Task APreflightRefusalIsBlockedNotSentAndNotTheUnsentTail()
    {
        var model = Model(2);
        var blocked = FamilyAssistManifest.Review(
            model.Rows.Select(r => new FamilyAssistSelection(r, null, null)).ToArray(), Library, false, "");
        var calls = 0;
        var refused = await FamilyAssistBatch.RunAsync(blocked, Library, (g, l, c, v, t) =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<RecognitionProposal>>(Array.Empty<RecognitionProposal>());
        }, true, null, default);

        calls.Should().Be(0);
        refused.Should().OnlyContain(o => !o.AssistantDispatchStarted);
        var counts = FamilyAssistBatchDiagnostics.Capture(2, refused).Counts;
        counts.Blocked.Should().Be(2);
        counts.Unsent.Should().Be(0);
    }

    [Fact]
    public async Task AStaleManifestIsRefusedBeforeTheAssistantIsInvoked()
    {
        var stale = Manifest(Model(2).Rows);
        stale.Invalidate();

        var run = () => FamilyAssistBatch.RunAsync(stale, Library, (_, _, _, _, _) => throw new Exception("must not invoke"),
            true, null, default);
        await run.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CancellingBeforeDispatchProducesOnlyTheUnsentTail()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(Model(2).Rows), Library,
            (_, _, _, _, _) => throw new Exception("must not invoke"), true, null, cancelled.Token);

        outcomes.Should().OnlyContain(o => o.State == "not-sent" && !o.AssistantDispatchStarted);
    }

    [Fact]
    public async Task ATimeoutAfterDispatchIsSeparatedFromTheUnattemptedTailAndALateResultIsNotPresented()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<RecognitionProposal>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(Model(2).Rows), Library, (_, _, _, _, _) => pending.Task,
            true, null, default, TimeSpan.FromMilliseconds(80));

        outcomes[0].State.Should().Be("cancelled");
        outcomes[0].AssistantDispatchStarted.Should().BeTrue();
        outcomes[1].State.Should().Be("not-sent");
        outcomes[1].AssistantDispatchStarted.Should().BeFalse();

        pending.SetResult(Array.Empty<RecognitionProposal>());
        for (var i = 0; i < 100 && FamilyAssistProviderAdmission.IsBusy; i++) await Task.Delay(5);
        FamilyAssistBatch.Present(outcomes[0], Library).Should().BeFalse();
    }

    [Fact]
    public async Task TheReceiptCarriesNoIdentifiersPromptEvidenceOrProviderReason()
    {
        var receipt = await AbstainedReceipt();
        var serialized = JsonSerializer.Serialize(receipt);

        foreach (var forbidden in new[] { "arbitrary", "SYNTHETIC", "hypothesis", "Not enough", "DrawingPath", "private-person" })
            serialized.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task TheReceiptIsBoundedUpdatedInPlaceAndRetentionNeverDeletes()
    {
        var receipt = await AbstainedReceipt();
        var directory = Path.Combine(Path.GetTempPath(), "mahod-batch-receipts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = Guid.NewGuid();
            FamilyAssistBatchDiagnostics.TryWrite(receipt, session, directory, out var written).Should().BeTrue();
            File.Exists(written).Should().BeTrue();
            FamilyAssistBatchDiagnostics.TryWrite(receipt, session, directory, out var same).Should().BeTrue();
            same.Should().Be(written);
            Directory.GetFiles(directory).Should().HaveCount(1);

            var oversized = () => FamilyAssistBatchDiagnostics.Capture(FamilyAssistBatchDiagnostics.MaxRows + 1,
                Array.Empty<FamilyAssistBatchOutcome>());
            oversized.Should().Throw<ArgumentException>();

            for (var i = 1; i < FamilyAssistBatchDiagnostics.MaxReceipts; i++)
                FamilyAssistBatchDiagnostics.TryWrite(receipt, Guid.NewGuid(), directory, out _).Should().BeTrue();
            FamilyAssistBatchDiagnostics.TryWrite(receipt, Guid.NewGuid(), directory, out _).Should().BeFalse();
            Directory.GetFiles(directory).Should().HaveCount(FamilyAssistBatchDiagnostics.MaxReceipts);
            FamilyAssistBatchDiagnostics.TryWrite(receipt, session, directory, out _).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ARejectedAnswerShowsOnlyItsFixedClassInTheRowAndTheReceipt()
    {
        const string canary = "CANARYMODELTEXT";
        var model = Model(2);
        var provider = new FamilyRecognitionAssist(new Fake(r => new(r.ContextId, r.LibraryHash, new[]
        {
            new FamilyRankChoice("curb-road", canary + " curb/island", new[] { new FamilyRankCitation("engineer_context", "curb hypothesis") }),
        })));
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(model.Rows), Library,
            (g, l, c, v, t) => provider.AssistAsync(g, Library, null, l, c, v, t), true, null, default);

        var row = outcomes[0].Entry.Row;
        row.ApplyAssistant(outcomes[0].Proposals, id => id);
        row.AiStatus.Should().Contain("(קוד אבחון: " + FamilyRankRejection.ProseRefused + ")").And.NotContain(canary);
        var receipt = FamilyAssistBatchDiagnostics.Capture(2, outcomes);
        // The existing ReasonCode stays for its consumers; RejectionCode is additive.
        receipt.Entries.Should().HaveCount(2).And.OnlyContain(e => e.ReasonCode == "response_not_grounded_or_unusable" &&
            e.RejectionCode == FamilyRankRejection.ProseRefused && e.State == "abstained");
        JsonSerializer.Serialize(receipt).Should().NotContain(canary).And.NotContain("curb/island");

        var abstained = await AbstainedReceipt();
        abstained.Entries.Should().OnlyContain(e => e.ReasonCode == "provider_abstained" && e.RejectionCode == null);
    }

    private static async Task<FamilyAssistBatchDiagnostics.Receipt> AbstainedReceipt()
    {
        var model = Model(2);
        var provider = new FamilyRecognitionAssist(new Fake(r => Abstain(r, "Not enough grounded subject evidence")));
        var outcomes = await FamilyAssistBatch.RunAsync(Manifest(model.Rows), Library,
            (g, l, c, v, t) => provider.AssistAsync(g, Library, null, l, c, v, t), true, null, default);
        return FamilyAssistBatchDiagnostics.Capture(2, outcomes);
    }

    private static async Task<RecognitionProposal> Ask(FamilyDecisionRow row, Func<FamilyRankRequest, FamilyRankResponse> reply) =>
        (await new FamilyRecognitionAssist(new Fake(reply))
            .AssistAsync(row.Group, Library, null, new[] { row.Proposal }, Context, null, default)).Single();

    private static FamilyRankResponse Abstain(FamilyRankRequest request, string reason) =>
        new(request.ContextId, request.LibraryHash, Array.Empty<FamilyRankChoice>(), reason);

    private static FamilyAssistManifest Manifest(IReadOnlyList<FamilyDecisionRow> rows) =>
        FamilyAssistManifest.Review(rows.Select(r => new FamilyAssistSelection(r, Context, null)).ToArray(), Library, false, "");

    private static FamilyDecisionReviewModel Model(int count)
    {
        var records = Enumerable.Range(1, count).Select(i => new NeutralQuantityRecord
        {
            RecordId = "R" + i,
            RunId = "synthetic",
            ProjectProfileId = "synthetic",
            Source = new QuantitySource
            {
                Drawing = "synthetic.dwg",
                DrawingPath = "C:/SYNTHETIC/private-person/test.dwg",
                DrawingHash = new string('a', 64),
                Layer = "arbitrary" + i,
                Handle = i.ToString("X"),
                EntityType = "LWPOLYLINE",
            },
            Measurement = new QuantityMeasurement
            {
                Kind = "length",
                Unit = "m",
                Method = "polyline-length",
                RawValue = 1,
                Parameters = new() { [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + "_status"] = "read" },
            },
        }).ToArray();
        return FamilyDecisionReviewModel.Create(EngineerBoqDraftBuilder.Build(records, Array.Empty<DeliveryFinding>(),
            new CatalogSnapshot { SnapshotId = "synthetic", FileHash = "none" }, null, Library,
            new EngineerDraftContext("synthetic", "synthetic", "synthetic", "synthetic.dwg", "none", Array.Empty<string>(),
                Classifier: LocalFamilyClassifier.Instance)));
    }

    private sealed class Fake(Func<FamilyRankRequest, FamilyRankResponse> response) : IFamilyRecognitionProvider
    {
        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken token) =>
            Task.FromResult(response(request));
    }
}
