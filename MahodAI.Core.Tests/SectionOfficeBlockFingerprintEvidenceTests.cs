using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests;

public sealed class SectionOfficeBlockFingerprintEvidenceTests
{
    private static readonly BlockDefinitionFingerprintLogic.Header Header = new(
        0, 0, 0, "Undefined", "Any", false, "False", "False");

    // Deliberately retain tiny values and display fields: evidence is not a
    // normalization step, and handles/entity order must not enter the hash.
    private static readonly string[] Signatures =
    {
        "Polyline|layer=0|normal=2.514629062161204E-22,-1.335115863818987E-09,1|elevation=-1.17E-11",
        "Line|layer=0|color-index=256|start=0,0,0|end=1,2,0",
        "Line|layer=0|color-index=256|start=0,0,0|end=1,2,0",
    };

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CaptureDoesNotChangeHashOrExistingAcceptanceDecision(bool mismatch, bool writeFails)
    {
        var liveHash = BlockDefinitionFingerprintLogic.Compose(Header, Signatures);
        var storedHash = mismatch ? new string('d', 64) : liveHash;
        var decisionBefore = Validate(storedHash, liveHash);
        string? written = null;
        var messages = new List<string>();
        var writer = new SectionOfficeBlockFingerprintEvidenceWriter("unused-test-directory", messages.Add,
            (_, json) =>
            {
                if (writeFails) throw new IOException("test evidence disk unavailable");
                written = json;
            });
        var evidence = Evidence(mismatch ? "verify-mismatch" : "verify-match", liveHash);

        writer.WriteOnce(evidence);

        BlockDefinitionFingerprintLogic.Compose(Header, Signatures).Should().Be(liveHash);
        Validate(storedHash, liveHash).Should().Be(decisionBefore).And.Be(!mismatch);
        evidence.Snapshots.Single().Entities.Select(e => e.Signature).Should().Equal(Signatures);
        if (writeFails)
        {
            written.Should().BeNull();
            messages.Should().ContainSingle().Which.Should().Contain("evidence_unavailable");
        }
        else
        {
            using var json = JsonDocument.Parse(written!);
            var snapshot = json.RootElement.GetProperty("Snapshots")[0];
            var roundTripHeader = snapshot.GetProperty("Header")
                .Deserialize<BlockDefinitionFingerprintLogic.Header>()!;
            var roundTripSignatures = snapshot.GetProperty("Entities").EnumerateArray()
                .Select(e => e.GetProperty("Signature").GetString()!).ToArray();
            BlockDefinitionFingerprintLogic.Compose(roundTripHeader, roundTripSignatures).Should().Be(liveHash);
            BlockDefinitionFingerprintLogic.ComposeEntities(roundTripSignatures)
                .Should().Be(BlockDefinitionFingerprintLogic.ComposeEntities(Signatures));
            json.RootElement.GetProperty("RunId").GetString().Should().Be("sections-verify-fixture");
            snapshot.GetProperty("BlockHandle").GetString().Should().Be("BD8938");
        }
    }

    [Fact]
    public void WriteAndDiagnosticFailuresDoNotReplaceAnExistingValidationException()
    {
        var writer = new SectionOfficeBlockFingerprintEvidenceWriter("unused-test-directory",
            _ => throw new InvalidOperationException("diagnostic callback failed"),
            (_, _) => throw new IOException("evidence disk unavailable"));
        var original = new InvalidOperationException("Protected office block was changed after import");

        Action validate = () =>
        {
            try { throw original; }
            catch
            {
                writer.WriteOnce(Evidence("load", new string('a', 64)));
                throw;
            }
        };

        validate.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OncePerAssetPhaseBoundsOutputButPreservesFirstLaterMismatch(bool writeFails)
    {
        var attempts = 0;
        var writer = new SectionOfficeBlockFingerprintEvidenceWriter("unused-test-directory", write: (_, _) =>
        {
            attempts++;
            if (writeFails) throw new IOException("unavailable");
        });
        for (var i = 0; i < 84; i++)
            writer.WriteOnce(Evidence("verify-match", new string('a', 64)));
        for (var i = 0; i < 84; i++)
            writer.WriteOnce(Evidence("verify-mismatch", new string('b', 64)));
        writer.WriteOnce(Evidence("verify-read-failed", null));

        attempts.Should().Be(3, "each asset/phase gets one attempt, even if the sink fails");
    }

    private static bool Validate(string storedHash, string liveHash)
    {
        var view = SectionFurnitureLogic.OfficeCarView.Front;
        return SectionOfficeVehicleAssetEvidenceLogic.TryValidateLiveEvidence(view,
            SectionOfficeVehicleAssetEvidenceLogic.FrontSha256.Substring(0, 16),
            SectionOfficeVehicleAssetEvidenceLogic.ProtectedBlockName(view),
            SectionOfficeVehicleAssetEvidenceLogic.ProvenanceComment(view, storedHash), liveHash, out _);
    }

    private static SectionOfficeBlockFingerprintEvidence Evidence(string phase, string? fingerprint)
    {
        var snapshot = new SectionOfficeBlockFingerprintEvidence.Snapshot
        {
            Stage = "verify-attestation", HashKind = BlockDefinitionFingerprintLogic.Version,
            Fingerprint = fingerprint, Header = Header, BlockHandle = "BD8938",
        };
        snapshot.Entities.AddRange(Signatures.Select((s, i) =>
            new SectionOfficeBlockFingerprintEvidence.EntitySignature(i.ToString("X"), "fixture", s)));
        return new SectionOfficeBlockFingerprintEvidence
        {
            OperationId = "operation-fixture", RunId = "sections-verify-fixture",
            Asset = "Front", SourceSha256 = SectionOfficeVehicleAssetEvidenceLogic.FrontSha256,
            Phase = phase, Outcome = phase, Snapshots = new[] { snapshot },
        };
    }
}
