using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Core.Tests.Estimate.Recognition;

public sealed class FamilyRecognitionAssistTests
{
    private static readonly EngineerBoqLibrary Library = EngineerBoqLibrary.RoadsV1;
    private static readonly IReadOnlyList<RecognitionProposal> NoLocal = Array.Empty<RecognitionProposal>();

    [Fact]
    public async Task RandomLayerWithReadNearbyText_ReachesProviderAndYieldsUnapprovedAiProposal()
    {
        var provider = new FakeProvider();
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);

        provider.Calls.Should().Be(1);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.Origin.Should().Be(RecognitionProposal.OriginAi);
        proposal.FamilyId.Should().Be("curb-road");
        proposal.GroupId.Should().Be(FamilyAssistFixtures.GroupId);
        proposal.RecordIds.Should().Equal("rec-1", "rec-2", "rec-3");
        proposal.CandidateCodes.Should().Equal("U51.06.1900");
        var reference = proposal.EvidenceRefs.Should().ContainSingle().Subject;
        reference.Key.Should().Be(EvidenceKeys.NearbyText);
        reference.RecordIds.Should().Equal("rec-1", "rec-2", "rec-3");
        proposal.Inferred.Should().Contain(FamilyRecognitionAssist.ReviewWarning);
        proposal.Observed.Should().ContainSingle().Which.Should().Contain("אבן שפה");

        var request = provider.Request!;
        request.LibraryId.Should().Be(Library.Id);
        request.LibraryHash.Should().Be(LibraryIdentity.LibraryHash(Library));
        request.MeasurementKind.Should().Be("length");
        request.CanonicalUnit.Should().Be("m");
        request.Images.Should().BeEmpty();
        request.Vision.Should().BeNull();
        var nearby = request.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText);
        nearby.Status.Should().Be("read");
        nearby.Value.Should().Be("אבן שפה לאורך המדרכה (3)");
        (nearby.Coverage, nearby.Total).Should().Be((3, 3));
        request.Evidence.Single(item => item.Key == FamilyRecognitionAssist.LayerKey).Status.Should().Be("identifier");
        request.Candidates.Select(candidate => candidate.FamilyId).Should().Contain("curb-road").And.NotContain("road-pavement");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Q742")]
    public async Task RandomLayerAlone_DoesNotReachProvider(string? context)
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(parameters: _ => new Dictionary<string, string>
        {
            [EvidenceKeys.EntityLinetype] = "Continuous", [EvidenceKeys.BlockNameEffective] = "asdasd23423",
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, context, null, default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Abstained);
        result[0].MissingDetails.Single().Should().Contain("שם שכבה לבדו");
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("no-schema")]
    [InlineData("other-schema")]
    [InlineData("unavailable")]
    [InlineData("absent")]
    [InlineData("no-status")]
    public async Task EvidenceWithoutAgreedSchemaOrReadStatus_IsNotUsed(string variant)
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            switch (variant)
            {
                case "no-schema": parameters.Remove(EvidenceKeys.Schema); break;
                case "other-schema": parameters[EvidenceKeys.Schema] = "mahod-evidence/2"; break;
                case "unavailable": parameters[EvidenceKeys.NearbyText + "_status"] = "unavailable:xref-unloaded"; break;
                case "absent": parameters[EvidenceKeys.NearbyText + "_status"] = "absent"; break;
                default: parameters.Remove(EvidenceKeys.NearbyText + "_status"); break;
            }
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Abstained);
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task UnusableEvidence_IsSentAsStateOnlyWithoutItsReasonText()
    {
        var provider = new FakeProvider { Rank = request => FamilyAssistFixtures.Valid(request, key: FamilyRecognitionAssist.EngineerContextKey) };
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters[EvidenceKeys.NearbyText + "_status"] = "unavailable:xref-unloaded-SECRET";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, "השכבה היא אבן שפה לאורך המדרכה", null, default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Proposed);
        result[0].EvidenceRefs.Should().BeEmpty();
        result[0].Inferred.Should().Contain(line => line.Contains("אבן שפה"));
        var nearby = provider.Request!.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText);
        (nearby.Value, nearby.Status, nearby.Coverage).Should().Be((string.Empty, "unavailable", 0));
        JsonSerializer.Serialize(provider.Request).Should().NotContain("SECRET").And.NotContain("xref-unloaded");
    }

    [Theory]
    [InlineData("context")]
    [InlineData("hash")]
    [InlineData("invented-family")]
    [InlineData("wrong-basis-family")]
    [InlineData("quote-not-in-evidence")]
    [InlineData("layer-identifier")]
    [InlineData("geometry-key")]
    [InlineData("short-quote")]
    [InlineData("duplicate-family")]
    [InlineData("four-families")]
    [InlineData("no-citation")]
    [InlineData("seven-citations")]
    [InlineData("support-only-citation")]
    [InlineData("price-explanation")]
    [InlineData("private-missing-detail")]
    [InlineData("six-missing-details")]
    [InlineData("second-choice-invalid")]
    [InlineData("abstention")]
    [InlineData("null-response")]
    public async Task InvalidRanking_IsAllOrNothing(string defect)
    {
        var provider = new FakeProvider { Rank = request =>
        {
            var valid = FamilyAssistFixtures.Valid(request);
            var choice = valid.Ranked[0];
            return defect switch
            {
                "context" => valid with { ContextId = "stale" },
                "hash" => valid with { LibraryHash = new string('b', 64) },
                "invented-family" => valid with { Ranked = new[] { choice with { FamilyId = "made-up-family" } } },
                "wrong-basis-family" => valid with { Ranked = new[] { choice with { FamilyId = "road-pavement" } } },
                "quote-not-in-evidence" => FamilyAssistFixtures.Valid(request, quote: "בטון דרוך"),
                "layer-identifier" => FamilyAssistFixtures.Valid(request, key: FamilyRecognitionAssist.LayerKey, quote: "asdasd"),
                "geometry-key" => FamilyAssistFixtures.Valid(request, key: EvidenceKeys.GeometrySample, quote: "204539"),
                "short-quote" => FamilyAssistFixtures.Valid(request, quote: "שפ"),
                "duplicate-family" => valid with { Ranked = new[] { choice, choice } },
                "four-families" => valid with { Ranked = new[] { choice, choice with { FamilyId = "curb-island" },
                    choice with { FamilyId = "curb-garden" }, choice with { FamilyId = "curb-lowered" } } },
                "no-citation" => valid with { Ranked = new[] { choice with { Citations = Array.Empty<FamilyRankCitation>() } } },
                "seven-citations" => valid with { Ranked = new[] { choice with { Citations = Enumerable.Range(0, 7)
                    .Select(index => new FamilyRankCitation(EvidenceKeys.NearbyText + index, "אבן שפה")).ToArray() } } },
                "support-only-citation" => FamilyAssistFixtures.Valid(request, key: EvidenceKeys.LayerLinetype, quote: "DASHED"),
                "price-explanation" => valid with { Ranked = new[] { choice with { Explanation = "מחיר 42 למטר" } } },
                "private-missing-detail" => valid with { Ranked = new[] { choice with { MissingDetails = new[] { "C:\\private\\source.dwg" } } } },
                "six-missing-details" => valid with { Ranked = new[] { choice with { MissingDetails = Enumerable.Repeat("לבדוק", 6).ToArray() } } },
                "second-choice-invalid" => valid with { Ranked = new[] { choice, choice with { FamilyId = "made-up-family" } } },
                "null-response" => null!,
                _ => valid with { AbstentionReason = "אין די ראיות" },
            };
        } };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.FamilyId.Should().BeNull();
        proposal.CandidateCodes.Should().BeEmpty();
        proposal.Alternatives.Should().BeEmpty();
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task SupportingCadCitation_BesideASubjectCitation_IsAccepted()
    {
        var provider = new FakeProvider { Rank = request => new FamilyRankResponse(request.ContextId, request.LibraryHash, new[]
        {
            new FamilyRankChoice("curb-road", "ייתכן שזו אבן שפה לפי הטקסט הסמוך וסוג הקו", new[]
            {
                new FamilyRankCitation(EvidenceKeys.NearbyText, "אבן שפה"),
                new FamilyRankCitation(EvidenceKeys.LayerLinetype, "DASHED"),
            }),
            new FamilyRankChoice("curb-island", "חלופה: אבן שפה לאי תנועה", new[] { new FamilyRankCitation(EvidenceKeys.NearbyText, "אבן שפה") }),
        }) };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.EvidenceRefs.Select(reference => reference.Key).Should().Equal(EvidenceKeys.LayerLinetype, EvidenceKeys.NearbyText);
        proposal.Alternatives.Should().ContainSingle().Which.FamilyId.Should().Be("curb-island");
        proposal.MissingDetails.Should().BeEmpty();
    }

    [Fact]
    public async Task UnsafeEvidenceLeaf_IsWithheldIntactAndOnlyTheSafeLeafOfTheKeyIsSent()
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters[EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new[]
            {
                new { tag = "SRC", value = "C:\\private\\block.dwg" }, new { tag = "TYPE", value = "CURBSTONE" },
            });
            parameters[EvidenceKeys.BlockAttributes + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        provider.Calls.Should().Be(1);
        provider.Request!.WithheldKeys.Should().Equal(EvidenceKeys.BlockAttributes);
        // The private leaf is omitted whole (tag and value); the public leaf of the same key travels as it is.
        var attributes = provider.Request.Evidence.Single(item => item.Key == EvidenceKeys.BlockAttributes);
        (attributes.Value, attributes.Coverage, attributes.Total).Should().Be(("TYPE=CURBSTONE (3)", 3, 3));
        JsonSerializer.Serialize(provider.Request).Should().NotContain("block.dwg").And.NotContain("SRC=");
        result.Should().ContainSingle().Which.MissingDetails.Should().Contain(detail =>
            detail.Contains(EvidenceKeys.BlockAttributes) && detail.Contains("אין להסיק מכך שכל ערכי המפתח נשלחו"));
    }

    [Fact]
    public async Task WireRequest_NeverCarriesCoordinatesMatricesXrefNamesHandlesRecordIdsOrQuantities()
    {
        var provider = new FakeProvider();
        await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var wire = JsonSerializer.Serialize(provider.Request);
        wire.Should().NotContain("204539").And.NotContain("649342").And.NotContain("777777").And.NotContain("PRIVATE-XREF-NAME")
            .And.NotContain("matrix").And.NotContain("points").And.NotContain("NEARHANDLE77").And.NotContain("HREC")
            .And.NotContain("rec-1").And.NotContain("29287").And.NotContain("private-source").And.NotContain("distance")
            .And.NotContain("Price");
        provider.Request!.Evidence.Select(item => item.Key).Should()
            .NotContain(new[] { EvidenceKeys.GeometrySample, EvidenceKeys.XrefTransform, EvidenceKeys.Schema });
    }

    [Theory]
    [InlineData("C:\\private\\source.dwg")]
    [InlineData("\\\\server\\project")]
    [InlineData("204539.12, 649342.4")]
    [InlineData("כמות 297 אבן שפה")]
    [InlineData("מחיר 20 ש״ח")]
    public async Task PrivateOrQuantityPriceEngineerContext_IsNotSent(string context)
    {
        var provider = new FakeProvider();
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, context, null, default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Abstained);
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task EngineerContextOverLimit_IsNotSent()
    {
        var provider = new FakeProvider();
        await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal,
            new string('א', FamilyRecognitionAssist.MaxEngineerContextLength + 1), null, default);
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LocalSingleFamily_DoesNotCallProvider()
    {
        var provider = new FakeProvider();
        var local = new[] { FamilyAssistFixtures.Local("curb-road") };
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), local, null, null, default);
        result.Should().BeEmpty();
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("two-families")]
    [InlineData("local-abstained")]
    [InlineData("other-group-only")]
    [InlineData("ai-origin-only")]
    public async Task LocalAmbiguityAbstentionOrAbsence_CallsProvider(string variant)
    {
        var provider = new FakeProvider();
        IReadOnlyList<RecognitionProposal> local = variant switch
        {
            "two-families" => new[] { FamilyAssistFixtures.Local("curb-road", "rec-1"), FamilyAssistFixtures.Local("curb-island", "rec-2", "rec-3") },
            "local-abstained" => new[] { FamilyAssistFixtures.Local("curb-road", "rec-1"), FamilyAssistFixtures.Local(null, "rec-2", "rec-3") },
            "other-group-only" => new[] { FamilyAssistFixtures.Local("curb-road") with { GroupId = "another-group" } },
            _ => new[] { FamilyAssistFixtures.Local("curb-road") with { Origin = RecognitionProposal.OriginAi } },
        };
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), local, null, null, default);
        provider.Calls.Should().Be(1);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Proposed);
    }

    [Fact]
    public async Task CandidateCodes_ComeOnlyFromTheFamilyRecipeThatExistsInThePriceList()
    {
        var provider = new FakeProvider();
        var assist = new FamilyRecognitionAssist(provider);
        var other = new CatalogSnapshot { SnapshotId = "other", FileHash = FamilyAssistFixtures.Hash };
        other.Items.Add("U99.99.9999", new CatalogItem { Code = "U99.99.9999", Description = "אבן שפה", UnitRaw = "מ'" });
        (await assist.AssistAsync(FamilyAssistFixtures.Group(), Library, other, NoLocal, null, null, default))
            .Single().CandidateCodes.Should().BeEmpty();
        (await assist.AssistAsync(FamilyAssistFixtures.Group(), Library, null, NoLocal, null, null, default))
            .Single().CandidateCodes.Should().BeEmpty();
        var invalidIdentity = new CatalogSnapshot { SnapshotId = "bad", FileHash = "not-a-hash" };
        invalidIdentity.Items.Add("U51.06.1900", new CatalogItem { Code = "U51.06.1900", Description = "אבן שפה", UnitRaw = "מ'" });
        (await assist.AssistAsync(FamilyAssistFixtures.Group(), Library, invalidIdentity, NoLocal, null, null, default))
            .Single().CandidateCodes.Should().BeEmpty();
        provider.Calls.Should().Be(3);
    }

    [Theory]
    [InlineData("area", "hatch", "מ\"ר", "road-pavement", "curb-road")]
    [InlineData("length", "open", "m", "curb-road", "road-pavement")]
    [InlineData("length", "closed-perimeter", "m", "curb-road", "curb-bike")]
    [InlineData("length", "open-w15", "m", "marking-lines", "sign-plates")]
    [InlineData("count", "block", "יח'", "sign-plates", "curb-road")]
    [InlineData("area", "painted-width", "מ\"ר", "marking-lines", "road-pavement")]
    public void Candidates_MirrorTheDraftQuantityBasis(string kind, string methodClass, string unit, string included, string excluded)
    {
        var preparation = FamilyRecognitionAssist.Prepare(FamilyAssistFixtures.Group(kind: kind, methodClass: methodClass, unit: unit), Library, null);
        var request = preparation.Request!;
        request.Candidates.Select(candidate => candidate.FamilyId).Should().Contain(included).And.NotContain(excluded);
        request.Candidates.Should().OnlyContain(candidate =>
            candidate.RuleFingerprint == LibraryIdentity.RuleFingerprint(Library.Rules.Single(rule => rule.Id == candidate.FamilyId)));
    }

    [Fact]
    public void VolumeGroup_HasNoCandidateFamilyAndAbstainsBeforeProvider()
    {
        var preparation = FamilyRecognitionAssist.Prepare(FamilyAssistFixtures.Group(kind: "volume", methodClass: "corridor", unit: "מ\"ק"), Library, null);
        preparation.Request.Should().BeNull();
        preparation.AbstentionMessage.Should().Contain("בסיס המדידה");
    }

    [Fact]
    public void Prepare_IsDeterministicAndBindsTheEngineerContext()
    {
        var first = FamilyRecognitionAssist.Prepare(FamilyAssistFixtures.Group(), Library, null).Request!;
        var second = FamilyRecognitionAssist.Prepare(FamilyAssistFixtures.Group(), Library, null).Request!;
        CatalogIdentity.IsValidSha256(first.ContextId).Should().BeTrue();
        second.ContextId.Should().Be(first.ContextId);
        JsonSerializer.Serialize(second).Should().Be(JsonSerializer.Serialize(first));
        FamilyRecognitionAssist.Prepare(FamilyAssistFixtures.Group(), Library, "אבן שפה").Request!.ContextId.Should().NotBe(first.ContextId);
    }

    [Fact]
    public void Evidence_IsAggregatedToTopDistinctValuesWithinFixedBounds()
    {
        var group = FamilyAssistFixtures.Group(count: 40, parameters: index =>
            FamilyAssistFixtures.Evidence(new string('א', 180) + " " + index.ToString(CultureInfo.InvariantCulture)));
        var request = FamilyRecognitionAssist.Prepare(group, Library, null).Request!;
        var nearby = request.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText);
        // Coverage counts only the records behind the values that fit the bounds (here one distinct text per record).
        var sent = nearby.Value.Split(FamilyRecognitionAssist.ValueSeparator);
        sent.Should().OnlyContain(piece => piece.EndsWith(" (1)", StringComparison.Ordinal));
        (nearby.Coverage, nearby.Total).Should().Be((sent.Length, 40));
        nearby.Coverage.Should().BeLessThan(40);
        nearby.Value.Length.Should().BeLessThanOrEqualTo(FamilyRecognitionAssist.MaxValueChars);
        nearby.Value.Split(FamilyRecognitionAssist.ValueSeparator).Length.Should().BeInRange(1, FamilyRecognitionAssist.MaxValuesPerKey);
        request.Evidence.Sum(item => item.Value.Length).Should().BeLessThanOrEqualTo(FamilyRecognitionAssist.MaxEvidenceChars);
    }

    [Fact]
    public async Task TotalBudget_AbstainsWithoutPartialResult()
    {
        var provider = new FakeProvider { Never = true };
        var result = await new FamilyRecognitionAssist(provider, TimeSpan.FromMilliseconds(30))
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.MissingDetails.Single().Should().Contain("בזמן");
    }

    [Fact]
    public async Task UserCancellationBeforeCall_DoesNotCallProvider()
    {
        var provider = new FakeProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, cancellation.Token);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("בוטלה");
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task LateResultAfterUserCancellation_IsDiscarded()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new FakeProvider { OnRank = _ => cancellation.Cancel() };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, cancellation.Token);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.MissingDetails.Single().Should().Contain("בוטלה");
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task ProviderException_IsSanitized()
    {
        var provider = new FakeProvider { Rank = _ => throw new InvalidOperationException("SECRET_CANARY body C:\\server\\path") };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        JsonSerializer.Serialize(result).Should().NotContain("SECRET_CANARY").And.NotContain("server");
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("אינו זמין");
    }

    [Fact]
    public async Task EvidenceMutationDuringCall_AbstainsAndTheSentSnapshotIsUnchanged()
    {
        var group = FamilyAssistFixtures.Group();
        var provider = new FakeProvider { OnRank = request =>
        {
            group.Records[0].Measurement.Parameters[EvidenceKeys.NearbyText] = JsonSerializer.Serialize(new[] { new { text = "ספסל רחוב" } });
            request.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText).Value.Should().Contain("אבן שפה");
        } };
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("השתנו");
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task CatalogEditDuringCall_Abstains()
    {
        var catalog = FamilyAssistFixtures.Catalog();
        var provider = new FakeProvider { OnRank = _ =>
            catalog.Items.Add("U51.06.2460", new CatalogItem { Code = "U51.06.2460", Description = "אבן שפה לאי", UnitRaw = "מ'" }) };
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(FamilyAssistFixtures.Group(), Library, catalog, NoLocal, null, null, default);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("השתנו");
    }

    [Theory]
    [InlineData("no-permit")]
    [InlineData("hash-mismatch")]
    [InlineData("context-mismatch")]
    [InlineData("three-images")]
    [InlineData("forged-image")]
    public async Task ImagesWithoutExactPermit_DoNotReachProvider(string defect)
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group();
        IReadOnlyList<VisionImage> images = Enumerable.Range(0, defect == "three-images" ? 3 : 1).Select(index => TestPng.Image(4 + index)).ToArray();
        if (defect == "forged-image")
        {
            // Built past the factory on purpose: the assist must re-validate what the host hands over.
            var bytes = TestPng.Create(4, 4, ("tEXt", Encoding.ASCII.GetBytes("Comment\0hidden")));
            images = new[] { new VisionImage(bytes, VisionImagePolicy.GroupPreview, VisionImagePolicy.Sha256(bytes), 4, 4) };
        }
        var contextId = FamilyRecognitionAssist.Prepare(group, Library, null, images).Request?.ContextId ?? new string('0', 64);
        var hashes = images.Select(image => image.Sha256).ToArray();
        VisionSendPermit? permit = defect switch
        {
            "no-permit" => null,
            "hash-mismatch" => new VisionSendPermit(contextId, new[] { new string('b', 64) }, "Arthur", DateTimeOffset.UtcNow),
            "context-mismatch" => new VisionSendPermit(new string('e', 64), hashes, "Arthur", DateTimeOffset.UtcNow),
            _ => new VisionSendPermit(contextId, hashes, "Arthur", DateTimeOffset.UtcNow),
        };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, new VisionPayload(images, permit), default);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("התמונות");
        provider.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ExactPermit_SendsImageHashesOnlyInTheSerializedRequest()
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group();
        var png = TestPng.Create(6, 6);
        VisionImagePolicy.TryCreate(png, VisionImagePolicy.LegendCrop, out var image, out _).Should().BeTrue();
        var images = new[] { image! };
        var preparation = FamilyRecognitionAssist.Prepare(group, Library, null, images);
        var permit = new VisionSendPermit(preparation.Request!.ContextId, new[] { image!.Sha256 }, "Arthur", DateTimeOffset.UtcNow);
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, new VisionPayload(images, permit), default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Proposed);
        provider.Request!.ContextId.Should().Be(preparation.Request.ContextId);
        provider.Request.Images.Should().ContainSingle().Which.Should().Be(new FamilyRankImage(image.Sha256, VisionImagePolicy.LegendCrop));
        provider.Request.Vision!.Permit.Should().BeSameAs(permit);
        JsonSerializer.Serialize(provider.Request).Should().NotContain(Convert.ToBase64String(png)).And.NotContain("Arthur");
    }

    // ---- Adversarial-review findings 24-30 (work93b, component "ai") -------------------------------------------------

    [Fact]
    public async Task XrefAndBoundXrefPrefixesOfBlockLinetypeAndLayerNames_NeverReachTheRequest()
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            // The real-scan shape (native75-qualified-survey-marker.json): the XREF file name prefixes the block name.
            parameters[EvidenceKeys.BlockNameEffective] = "6422-SP-MEDVA-ALL-2026-MHD|S_POINT_E";
            parameters[EvidenceKeys.EntityLinetype] = "OUTER-XREF-CANARY|INNER-XREF-CANARY|DASHED";
            parameters[EvidenceKeys.LayerLinetype] = "BOUND-XREF-CANARY$0$HIDDEN2";
            return parameters;
        }) with { LayerLeaf = "BOUND-LAYER-CANARY$0$asdasd23423" };
        await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);

        provider.Calls.Should().Be(1);
        var request = provider.Request!;
        JsonSerializer.Serialize(request).Should().NotContain("6422-SP-MEDVA").And.NotContain("XREF-CANARY")
            .And.NotContain("BOUND-LAYER-CANARY").And.NotContain("MHD|");
        request.Evidence.Single(item => item.Key == EvidenceKeys.BlockNameEffective).Value.Should().Be("S_POINT_E (3)");
        request.Evidence.Single(item => item.Key == EvidenceKeys.EntityLinetype).Value.Should().Be("DASHED (3)");
        request.Evidence.Single(item => item.Key == EvidenceKeys.LayerLinetype).Value.Should().Be("HIDDEN2 (3)");
        request.Evidence.Single(item => item.Key == FamilyRecognitionAssist.LayerKey).Value.Should().Be("asdasd23423");
    }

    [Theory]
    [InlineData("hatch-solid")]
    [InlineData("hatch-ansi")]
    [InlineData("hatch-named")]
    [InlineData("legend-by-colour")]
    [InlineData("legend-basis-unstated")]
    public async Task SupportingHintAlone_NeverAdmitsARequest(string variant)
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(kind: "area", methodClass: "hatch", unit: "מ\"ר", parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters.Remove(EvidenceKeys.NearbyText);
            parameters.Remove(EvidenceKeys.NearbyText + "_status");
            var (key, value) = variant switch
            {
                "hatch-solid" => (EvidenceKeys.Hatch, "{\"pattern\":\"SOLID\",\"pattern_type\":\"predefined\",\"solid\":true}"),
                "hatch-ansi" => (EvidenceKeys.Hatch, "{\"pattern\":\"ANSI31\",\"pattern_type\":\"predefined\",\"solid\":false}"),
                "hatch-named" => (EvidenceKeys.Hatch, "{\"pattern\":\"GRASS\",\"pattern_type\":\"predefined\",\"solid\":false}"),
                "legend-by-colour" => (EvidenceKeys.LegendRow, JsonSerializer.Serialize(new { text = "מדרכה מרוצפת", match = "color", handle = "1F" })),
                _ => (EvidenceKeys.LegendRow, JsonSerializer.Serialize(new { text = "מדרכה מרוצפת", handle = "1F" })),
            };
            parameters[key] = value;
            parameters[key + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        result.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Abstained);
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData(false, RecognitionStatus.Abstained)]
    [InlineData(true, RecognitionStatus.Proposed)]
    public async Task HatchCitation_MaySupportAProposalButNeverCarriesOne(bool withSubjectCitation, RecognitionStatus expected)
    {
        var citations = new List<FamilyRankCitation> { new(EvidenceKeys.Hatch, "GRASS") };
        if (withSubjectCitation) citations.Add(new FamilyRankCitation(FamilyRecognitionAssist.EngineerContextKey, "שטח גינון"));
        var provider = new FakeProvider { Rank = request => new FamilyRankResponse(request.ContextId, request.LibraryHash, new[]
        {
            new FamilyRankChoice("landscape", "ייתכן שזה שטח גינון; נדרש לבדוק מול השרטוט", citations),
        }) };
        var group = FamilyAssistFixtures.Group(kind: "area", methodClass: "hatch", unit: "מ\"ר", parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters[EvidenceKeys.Hatch] = "{\"pattern\":\"GRASS\",\"pattern_type\":\"predefined\",\"solid\":false}";
            parameters[EvidenceKeys.Hatch + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, "שטח גינון בין המדרכות", null, default);
        provider.Calls.Should().Be(1);
        provider.Request!.Evidence.Single(item => item.Key == EvidenceKeys.Hatch).Value.Should().Be("GRASS (3)");
        result.Should().ContainSingle().Which.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("linetype", RecognitionStatus.Proposed)]
    [InlineData("pattern", RecognitionStatus.Proposed)]
    [InlineData("color", RecognitionStatus.Abstained)]
    public async Task LegendRow_CarriesAProposalOnlyWhenMatchedByShapeAndIsSentWithItsBasis(string match, RecognitionStatus expected)
    {
        var provider = new FakeProvider { Rank = request => FamilyAssistFixtures.Valid(request, key: EvidenceKeys.LegendRow) };
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters[EvidenceKeys.LegendRow] = JsonSerializer.Serialize(new { text = "אבן שפה טרומית", match, handle = "1F", verified = false });
            parameters[EvidenceKeys.LegendRow + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        provider.Calls.Should().Be(1);
        provider.Request!.Evidence.Single(item => item.Key == EvidenceKeys.LegendRow).Value.Should().Be(match + ": אבן שפה טרומית (3)");
        result.Should().ContainSingle().Which.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("dynamic-property")]
    [InlineData("attribute")]
    public async Task TagOrPropertyNameOverAMeaninglessValue_NeverAdmitsARequest(string variant)
    {
        var provider = new FakeProvider();
        var group = FamilyAssistFixtures.Group(kind: "count", methodClass: "block", unit: "יח'", parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters.Remove(EvidenceKeys.NearbyText);
            parameters.Remove(EvidenceKeys.NearbyText + "_status");
            // The exact shapes CivilEvidenceCollector writes (ReadDynamicProperties / ReadAttributes).
            var key = variant == "attribute" ? EvidenceKeys.BlockAttributes : EvidenceKeys.BlockProps;
            parameters[key] = variant == "attribute"
                ? JsonSerializer.Serialize(new[] { new { tag = "ELEV", value = "45.30", invisible = false } })
                : JsonSerializer.Serialize(new[] { new { name = "Flip state1", value = "0", units = "NoUnits" } });
            parameters[key + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        result.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("שם שכבה לבדו");
        provider.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("ELEV", RecognitionStatus.Abstained)]
    [InlineData("TYPE", RecognitionStatus.Abstained)]
    [InlineData("סככת המתנה", RecognitionStatus.Proposed)]
    [InlineData("TYPE=סככת המתנה", RecognitionStatus.Proposed)]
    public async Task OnlyAnAttributeValue_CanBeQuotedAsWhatTheGroupIs(string quote, RecognitionStatus expected)
    {
        var provider = new FakeProvider { Rank = request =>
            FamilyAssistFixtures.Valid(request, family: "bus-shelters", key: EvidenceKeys.BlockAttributes, quote: quote) };
        var group = FamilyAssistFixtures.Group(kind: "count", methodClass: "block", unit: "יח'", parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters.Remove(EvidenceKeys.NearbyText);
            parameters.Remove(EvidenceKeys.NearbyText + "_status");
            parameters[EvidenceKeys.BlockAttributes] = JsonSerializer.Serialize(new[]
            {
                new { tag = "TYPE", value = "סככת המתנה לאוטובוס" }, new { tag = "ELEV", value = "45.30" },
            });
            parameters[EvidenceKeys.BlockAttributes + "_status"] = "read";
            return parameters;
        });
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        provider.Calls.Should().Be(1);
        result.Should().ContainSingle().Which.Status.Should().Be(expected);
    }

    [Theory]
    [InlineData("labelled")]
    [InlineData("colon-labelled")]
    [InlineData("split-labels")]
    [InlineData("local-five-digit")]
    public void CoordinatesInNearbyTexts_AreWithheldIntactWhateverTheirLayout(string variant)
    {
        string[] texts = variant switch
        {
            "labelled" => new[] { "אבן שפה לאורך המדרכה", "X=204539.12 Y=649342.40" },
            "colon-labelled" => new[] { "אבן שפה לאורך המדרכה", "E:204539.12 N:649342.40" },
            "split-labels" => new[] { "אבן שפה לאורך המדרכה", "204539.12", "649342.40" },
            _ => new[] { "אבן שפה לאורך המדרכה", "X=12345.67" },
        };
        var group = FamilyAssistFixtures.Group(parameters: _ =>
        {
            var parameters = FamilyAssistFixtures.Evidence();
            parameters[EvidenceKeys.NearbyText] = JsonSerializer.Serialize(texts.Select(text => new { text, distance_m = 2.0 }).ToArray());
            return parameters;
        });
        var request = FamilyRecognitionAssist.Prepare(group, Library, "אבן שפה לאורך המדרכה").Request!;
        request.WithheldKeys.Should().Contain(EvidenceKeys.NearbyText);
        // Every coordinate leaf is omitted whole; only the intact public text of the key is sent.
        request.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText).Value.Should().Be("אבן שפה לאורך המדרכה (3)");
        request.Evidence.Should().NotContain(item =>
            item.Value.Contains("204539") || item.Value.Contains("649342") || item.Value.Contains("12345"));
    }

    [Theory]
    [InlineData("X=204539.12 Y=649342.40", false)]
    [InlineData("204539.12 (1) | 649342.40 (1)", false)]
    [InlineData("E:204539.12 N:649342.40", false)]
    [InlineData("X=12345.67", false)]
    [InlineData("נקודה 1234567", false)]
    [InlineData("אבן שפה U51.06.1900", true)]
    [InlineData("גובה 45.30 קוטר 110", true)]
    [InlineData("asdasd23423", true)]
    public void FamilyTextGuard_TreatsLongNumbersAsCoordinatesWhateverTheirLayout(string text, bool safe) =>
        FamilyTextGuards.SafeText(text, FamilyRecognitionAssist.MaxValueChars).Should().Be(safe);

    [Fact]
    public void CountGroup_SendsCoarseSharesAndNeverItsMeasuredCount()
    {
        var group = FamilyAssistFixtures.Group(count: 87, kind: "count", methodClass: "block", unit: "יח'", parameters: index =>
            FamilyAssistFixtures.Evidence(index <= 60 ? "סככת המתנה לאוטובוס" : "תחנת אוטובוס"));
        var request = FamilyRecognitionAssist.Prepare(group, Library, "סככות המתנה").Request!;
        request.Evidence.Should().OnlyContain(item => item.Total == 100 && item.Coverage % 10 == 0 && item.Coverage >= 0 && item.Coverage <= 100);
        var nearby = request.Evidence.Single(item => item.Key == EvidenceKeys.NearbyText);
        nearby.Value.Should().Be("סככת המתנה לאוטובוס (70%) | תחנת אוטובוס (30%)");
        (nearby.Coverage, nearby.Total).Should().Be((100, 100));
        request.Evidence.Should().NotContain(item => item.Value.Contains("87") || item.Value.Contains("60") || item.Value.Contains("27"));
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(5, 5, 100)]
    [InlineData(1, 2, 50)]
    [InlineData(1, 3, 30)]
    [InlineData(2, 3, 70)]
    [InlineData(1, 100, 10)]
    [InlineData(99, 100, 90)]
    [InlineData(3, 0, 0)]
    public void ShareBucket_IsACoarsePercentThatNeverRoundsToNoneOrAll(int part, int total, int expected) =>
        FamilyRecognitionAssist.ShareBucket(part, total).Should().Be(expected);

    [Theory]
    [InlineData(true, "מדיניות הארגון")]
    [InlineData(false, "אין הרשאה תקפה")]
    public async Task TransportVisionRefusal_OffersTheTextOnlyPathInsteadOfAnOutage(bool policyDisabled, string expected)
    {
        var provider = new FakeProvider { Rank = _ => throw new VisionNotPermittedException(policyDisabled) };
        var result = await new FamilyRecognitionAssist(provider)
            .AssistAsync(FamilyAssistFixtures.Group(), Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Abstained);
        proposal.MissingDetails.Single().Should().Contain(expected).And.Contain("בלי תמונות").And.NotContain("אינו זמין");
    }

    [Fact]
    public async Task VisionGateOff_StopsOnlyImagesBeforeTheProvider_TextOnlyStillWorks()
    {
        var provider = new AiFixGatedProvider(allowed: false);
        var group = FamilyAssistFixtures.Group();
        var images = new[] { TestPng.Image(4) };
        var contextId = FamilyRecognitionAssist.Prepare(group, Library, null, images).Request!.ContextId;
        var vision = new VisionPayload(images, new VisionSendPermit(contextId, new[] { images[0].Sha256 }, "Arthur", DateTimeOffset.UtcNow));
        var assist = new FamilyRecognitionAssist(provider);

        var withImage = await assist.AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, vision, default);
        withImage.Should().ContainSingle().Which.MissingDetails.Single().Should().Contain("מדיניות הארגון").And.Contain("בלי תמונות");
        provider.Calls.Should().Be(0);

        var textOnly = await assist.AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        textOnly.Should().ContainSingle().Which.Status.Should().Be(RecognitionStatus.Proposed);
        provider.Calls.Should().Be(1);
    }

    [Fact]
    public async Task InfrastructureFamily_IsProposedAsADomainWithoutAnyInventedItem()
    {
        var provider = new FakeProvider { Rank = request => FamilyAssistFixtures.Valid(request, family: "utility-water", quote: "קו מים") };
        var group = FamilyAssistFixtures.Group(parameters: _ => FamilyAssistFixtures.Evidence("קו מים מתוכנן"));
        var result = await new FamilyRecognitionAssist(provider).AssistAsync(group, Library, FamilyAssistFixtures.Catalog(), NoLocal, null, null, default);
        provider.Request!.Candidates.Select(candidate => candidate.FamilyId).Should().Contain("utility-water").And.Contain("utility-sewer");
        var proposal = result.Should().ContainSingle().Subject;
        proposal.Status.Should().Be(RecognitionStatus.Proposed);
        proposal.Origin.Should().Be(RecognitionProposal.OriginAi);
        proposal.FamilyId.Should().Be("utility-water");
        proposal.CandidateCodes.Should().BeEmpty();
        proposal.Inferred.Should().Contain(FamilyRecognitionAssist.ReviewWarning);
    }

    private sealed class AiFixGatedProvider(bool allowed) : IFamilyRecognitionProvider, IFamilyVisionGate
    {
        public int Calls;

        public bool IsVisionAllowed() => allowed;

        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(FamilyAssistFixtures.Valid(request));
        }
    }

    private sealed class FakeProvider : IFamilyRecognitionProvider
    {
        public int Calls;
        public bool Never;
        public FamilyRankRequest? Request;
        public Func<FamilyRankRequest, FamilyRankResponse>? Rank;
        public Action<FamilyRankRequest>? OnRank;

        public Task<FamilyRankResponse> RankFamiliesAsync(FamilyRankRequest request, CancellationToken ct)
        {
            Calls++;
            Request = request;
            OnRank?.Invoke(request);
            if (Never) return new TaskCompletionSource<FamilyRankResponse>().Task;
            return Task.FromResult(Rank != null ? Rank(request) : FamilyAssistFixtures.Valid(request));
        }
    }
}

/// <summary>A design-model group on a random layer whose read evidence says what it is; private identifiers are planted as canaries.</summary>
internal static class FamilyAssistFixtures
{
    public const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string GroupId = "private-source|asdasd23423|length|open";
    public const string Layer = "asdasd23423";

    public static Dictionary<string, string> Evidence(string nearby = "אבן שפה לאורך המדרכה") => new()
    {
        [EvidenceKeys.Schema] = EvidenceKeys.SchemaV1,
        [EvidenceKeys.Schema + "_status"] = "read",
        [EvidenceKeys.NearbyText] = JsonSerializer.Serialize(new[] { new { text = nearby, distance_m = 1.25, handle = "NEARHANDLE77", source = "host" } }),
        [EvidenceKeys.NearbyText + "_status"] = "read",
        [EvidenceKeys.GeometrySample] = "{\"space\":\"host\",\"points\":[[204539.12,649342.4],[204541.5,649344.75]]}",
        [EvidenceKeys.GeometrySample + "_status"] = "read",
        [EvidenceKeys.XrefTransform] = "{\"space\":\"host\",\"chain\":[{\"xref\":\"PRIVATE-XREF-NAME\",\"matrix\":[1,0,0,777777.5,0,1,0,0,0,0,1,0,0,0,0,1]}]," +
                                       "\"class\":\"rigid\",\"scale\":[1,1,1],\"units\":{\"source\":\"Meters\",\"host\":\"Meters\"}}",
        [EvidenceKeys.XrefTransform + "_status"] = "read",
        [EvidenceKeys.EntityLinetype] = "Continuous",
        [EvidenceKeys.LayerLinetype] = "DASHED2",
    };

    public static RecognitionGroupInput Group(int count = 3, Func<int, Dictionary<string, string>>? parameters = null,
        string kind = "length", string methodClass = "open", string unit = "m") =>
        new(GroupId, "private-source", DraftSourceRole.Design, Layer, kind, unit, methodClass, null,
            Enumerable.Range(1, count).Select(index => Record("rec-" + index.ToString(CultureInfo.InvariantCulture),
                (parameters ?? (_ => Evidence()))(index), kind, unit)).ToArray());

    public static CatalogSnapshot Catalog()
    {
        var catalog = new CatalogSnapshot { SnapshotId = "local-catalog", FileHash = Hash };
        catalog.Items.Add("U51.06.1900", new CatalogItem { Code = "U51.06.1900", Description = "אבן שפה טרומית", UnitRaw = "מ'" });
        return catalog;
    }

    public static FamilyRankResponse Valid(FamilyRankRequest request, string family = "curb-road",
        string key = EvidenceKeys.NearbyText, string quote = "אבן שפה") =>
        new(request.ContextId, request.LibraryHash, new[]
        {
            new FamilyRankChoice(family, "ייתכן שהקבוצה היא אבן שפה לפי הראיה המצוטטת; נדרש לבדוק מול השרטוט",
                new[] { new FamilyRankCitation(key, quote) }),
        });

    public static RecognitionProposal Local(string? family, params string[] recordIds) =>
        new(GroupId, recordIds.Length == 0 ? new[] { "rec-1", "rec-2", "rec-3" } : recordIds,
            family == null ? RecognitionStatus.Abstained : RecognitionStatus.Proposed, family, Array.Empty<string>(),
            Array.Empty<RecognitionEvidenceRef>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<RecognitionAlternative>(),
            Array.Empty<string>(), RecognitionProposal.OriginLocal);

    /// <summary>A hand-built request for transport and permit tests (not produced by the assist).</summary>
    public static FamilyRankRequest Request(IReadOnlyList<FamilyRankImage>? images = null, string value = "אבן שפה לאורך המדרכה (3)") =>
        new(new string('c', 64), Library().Id, LibraryIdentity.LibraryHash(Library()), "length", "m", "open",
            new[] { new FamilyRankEvidence(EvidenceKeys.NearbyText, value, "read", 3, 3) }, Array.Empty<string>(),
            new[] { new FamilyRankCandidate("curb-road", "אבן שפה לכביש", "LengthWithClosedPerimeters", new string('d', 64)) },
            images ?? Array.Empty<FamilyRankImage>());

    private static EngineerBoqLibrary Library() => EngineerBoqLibrary.RoadsV1;

    private static NeutralQuantityRecord Record(string id, Dictionary<string, string> parameters, string kind, string unit) => new()
    {
        RecordId = id,
        ProjectProfileId = "profile",
        RunId = "run",
        Source = new QuantitySource
        {
            Drawing = "private-source.dwg", DrawingHash = Hash, Handle = "HREC" + id, EntityType = "LWPOLYLINE",
            Layer = "private-source|" + Layer, Xref = "private-source",
        },
        Measurement = new QuantityMeasurement { Kind = kind, Method = "polyline-length", RawValue = 29287.63, Unit = unit, Parameters = parameters },
    };
}
