using System.IO;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

/// <summary>
/// A family decision bound to the exact visual scope an image proposal was made for (FamilyVisualBindingPolicy),
/// ported from Codex's offline persistence harness (family-vision-persistence/CODEX_Test.cs, manifest 2E80A6BF):
/// the typed binding from an image-only response, the decision identity, the real YAML writer/loader (schema 4),
/// currentness on an unchanged rescan and staleness on every changed member, geometry or revision, tampering, and
/// the untouched legacy semantics. A fake provider stands in for the image model. All data is SYNTHETIC; no host,
/// network, price or engineering approval.
/// </summary>
public sealed class FamilyVisualBindingLifecycleTests : IDisposable
{
    private const string Reviewer = "SYNTHETIC-REVIEW";
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "mhd-family-visual-" + Guid.NewGuid().ToString("N"));

    public FamilyVisualBindingLifecycleTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // This directory is created uniquely by this fixture, never a product profile directory.
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public async Task AnImageOnlyProposalCarriesATypedBindingAndNoCadEvidence()
    {
        var (_, proposal) = await ProposeAsync(Group());

        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.VisualBinding.Should().NotBeNull();
        proposal.EvidenceRefs.Should().BeEmpty();
        FamilyVisualBindingPolicy.IsCurrent(proposal.VisualBinding!, Group().Records).Should().BeTrue();
    }

    [Fact]
    public async Task TheBindingIsPartOfTheDecisionIdentityAndApprovesNoItem()
    {
        var group = Group();
        var (_, proposal) = await ProposeAsync(group);
        var decision = Approve(group);
        var legacyId = decision.DecisionId;

        FamilyDecisionPolicy.AttachVisualBinding(decision, group, proposal.VisualBinding!);

        decision.DecisionId.Should().NotBe(legacyId);
        decision.ItemApprovals.Should().BeEmpty();
        FamilyDecisionPolicy.StructuralProblems(decision).Should().BeEmpty();
        Resolve(decision, group).Should().Be(FamilyDecisionState.Applied);
        var fromJson = JsonSerializer.Deserialize<ProjectProfile.EstimateProfile.FamilyDecision>(JsonSerializer.Serialize(decision))!;
        FamilyDecisionPolicy.DecisionId(fromJson).Should().Be(decision.DecisionId);
        Resolve(fromJson, Group(run: "rescan-run", id: "new-record-id")).Should().Be(FamilyDecisionState.Applied);
    }

    [Fact]
    public async Task TheRealYamlWriterAndLoaderKeepSchema4TheSignedBindingAndTheEffectiveHash()
    {
        var (profile, back) = await SaveAndReopenAsync();

        profile.SchemaVersion.Should().Be(4);
        back.DecisionId.Should().Be(profile.Estimate.FamilyDecisions.Single().DecisionId);
        FamilyDecisionPolicy.DecisionId(back).Should().Be(back.DecisionId);
        Resolve(back, Group(run: "new-run", id: "new-id")).Should().Be(FamilyDecisionState.Applied);
    }

    [Fact]
    public async Task EveryChangeToTheBoundScopeMakesTheReopenedDecisionStale()
    {
        var (_, back) = await SaveAndReopenAsync();

        Resolve(back, Group(hash: new string('b', 64))).Should().Be(FamilyDecisionState.Stale, "changed source revision");
        Resolve(back, Group(geometry: "{\"points\":[[0,0],[1,1]],\"closed\":false}")).Should().Be(FamilyDecisionState.Stale, "changed geometry");
        Resolve(back, Group(handle: "BB")).Should().Be(FamilyDecisionState.Stale, "changed source member");
        Resolve(back, Group(raw: 2)).Should().Be(FamilyDecisionState.Stale, "changed measured content");
        var extra = Group();
        ((List<NeutralQuantityRecord>)extra.Records).Add(Record("R2", "BB", new string('a', 64), "run", 1, null));
        Resolve(back, extra).Should().Be(FamilyDecisionState.Stale, "a new same-layer member is not silently covered");
        var unreadable = Group();
        unreadable.Records[0].Measurement.Parameters[EvidenceKeys.GeometrySample + "_status"] = "unavailable";
        Resolve(back, unreadable).Should().Be(FamilyDecisionState.Stale, "geometry that became unreadable");
    }

    [Fact]
    public async Task MissingIdentityRefusesAndReorderedMembersStayCurrent()
    {
        var (image, proposal) = await ProposeAsync(Group());

        FamilyVisualBindingPolicy.IsCurrent(proposal.VisualBinding!, Group(hash: "missing").Records).Should().BeFalse();
        var multiple = Group();
        ((List<NeutralQuantityRecord>)multiple.Records).Add(Record("R2", "BB", new string('a', 64), "run", 1, null));
        var binding = FamilyVisualBindingPolicy.Capture(multiple, new[] { new FamilyRankImage(image.Sha256, image.Kind) });
        ((List<NeutralQuantityRecord>)multiple.Records).Reverse();
        FamilyVisualBindingPolicy.IsCurrent(binding, multiple.Records).Should().BeTrue();
    }

    [Fact]
    public async Task ATamperedOrUnknownBindingCannotApply()
    {
        var (_, back) = await SaveAndReopenAsync();
        var originalImage = back.Selectors[0].VisualBinding!.Images[0].Sha256;

        var clone = FamilyDecisionPolicy.Clone(back)!;
        clone.Selectors[0].VisualBinding!.Images[0].Sha256 = new string('c', 64);
        back.Selectors[0].VisualBinding!.Images[0].Sha256.Should().Be(originalImage, "the clone is deep");
        FamilyDecisionPolicy.DecisionId(clone).Should().NotBe(back.DecisionId);
        Resolve(clone, Group()).Should().NotBe(FamilyDecisionState.Applied);

        clone = FamilyDecisionPolicy.Clone(back)!;
        clone.Selectors[0].VisualBinding!.Schema = "future/unknown";
        FamilyDecisionPolicy.StructuralProblems(clone).Should().NotBeEmpty();
    }

    [Fact]
    public async Task LegacyDecisionsKeepTheirSemanticsAndABindingCannotBlessATamperedOne()
    {
        var group = Group();
        var (_, proposal) = await ProposeAsync(group);
        var legacy = FamilyDecisionPolicy.CreateApproval("curb-road", new[] { group }, Array.Empty<string>(), Library,
            Reviewer, "Legacy deliberate source-layer decision", DateTime.UtcNow);

        Resolve(legacy, Group(hash: new string('b', 64))).Should().Be(FamilyDecisionState.Applied);
        FamilyDecisionPolicy.Clone(legacy)!.Selectors[0].VisualBinding.Should().BeNull();
        var tampered = FamilyDecisionPolicy.Clone(legacy)!;
        tampered.Reason = "edited without explicit new decision";
        var attach = () => FamilyDecisionPolicy.AttachVisualBinding(tampered, group, proposal.VisualBinding!);
        attach.Should().Throw<InvalidOperationException>();
    }

    private async Task<(ProjectProfile Profile, ProjectProfile.EstimateProfile.FamilyDecision Back)> SaveAndReopenAsync()
    {
        var group = Group();
        var (_, proposal) = await ProposeAsync(group);
        var decision = Approve(group);
        FamilyDecisionPolicy.AttachVisualBinding(decision, group, proposal.VisualBinding!);
        var profile = new ProjectProfile { ProfileId = "SYNTHETIC-VISUAL", ProjectName = "SYNTHETIC ONLY" };
        profile.Estimate.FamilyDecisions.Add(decision);
        var path = Path.Combine(_directory, "project-profile.yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        ProjectProfileWriter.Save(profile, path, "Synthetic visual lifecycle", Reviewer, expected);
        File.ReadAllText(path).Should().Contain("schema_version: 4");

        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.ConvertAll(f => f.Code)));
        loaded.Profile!.SchemaVersion.Should().Be(4);
        EstimateTraceIdentity.EffectiveProfileHash(loaded.Profile).Should().Be(EstimateTraceIdentity.EffectiveProfileHash(profile));
        return (profile, loaded.Profile.Estimate.FamilyDecisions.Single());
    }

    private static ProjectProfile.EstimateProfile.FamilyDecision Approve(RecognitionGroupInput group) =>
        FamilyDecisionPolicy.CreateApproval("curb-road", new[] { group }, Array.Empty<string>(), Library,
            Reviewer, "SYNTHETIC reviewed interpretation, not project approval", DateTime.UtcNow);

    internal static async Task<(VisionImage Image, RecognitionProposal Proposal)> ProposeAsync(RecognitionGroupInput group)
    {
        var png = GroupPreviewRenderer.Render(GroupPreviewRenderer.Samples(group.Records), 64)!;
        VisionImagePolicy.TryCreate(png, VisionImagePolicy.GroupPreview, out var image, out var why).Should().BeTrue(why);
        var prepared = FamilyRecognitionAssist.Prepare(group, Library, null, new[] { image! }).Request!;
        var payload = new VisionPayload(new[] { image! },
            new VisionSendPermit(prepared.ContextId, new[] { image!.Sha256 }, Reviewer, DateTimeOffset.UtcNow));
        var proposals = await new FamilyRecognitionAssist(new FakeProvider()).AssistAsync(group, Library, null,
            Array.Empty<RecognitionProposal>(), null, payload, default);
        return (image!, proposals.Single());
    }

    private static FamilyDecisionState Resolve(ProjectProfile.EstimateProfile.FamilyDecision decision, RecognitionGroupInput group) =>
        FamilyDecisionPolicy.Resolve(new[] { decision }, new[] { group }, Library).Single().State;

    internal static RecognitionGroupInput Group(string? hash = null, string handle = "AA", string run = "run", string id = "R1",
        string? geometry = null, double raw = 1) =>
        new("G", EngineerBoqDraftBuilder.HostSource, DraftSourceRole.Host, "x7q", "length", "m", "open", null,
            new List<NeutralQuantityRecord> { Record(id, handle, hash ?? new string('a', 64), run, raw, geometry) });

    internal static NeutralQuantityRecord Record(string id, string handle, string hash, string run, double raw, string? geometry,
        string layer = "x7q") => new()
    {
        RecordId = id,
        ProjectProfileId = "SYNTHETIC-VISUAL",
        RunId = run,
        Source = new QuantitySource
        {
            Drawing = "synthetic.dwg", DrawingPath = "C:/SYNTHETIC/DO-NOT-OPEN/synthetic.dwg", DrawingHash = hash,
            Handle = handle, EntityType = "LWPOLYLINE", Layer = layer,
        },
        Measurement = new QuantityMeasurement
        {
            Kind = "length", Method = "polyline-length", RawValue = raw, Unit = "m",
            Parameters = new()
            {
                [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1, [EvidenceKeys.Schema + "_status"] = "read",
                [EvidenceKeys.GeometrySample] = geometry ?? "{\"points\":[[0,0],[1,0],[1,1]],\"closed\":false}",
                [EvidenceKeys.GeometrySample + "_status"] = "read",
            },
        },
    };

    internal sealed class FakeProvider : IFamilyRecognitionProvider, IFamilyVisionGate
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
