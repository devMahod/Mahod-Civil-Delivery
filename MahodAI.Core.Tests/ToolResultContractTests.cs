using System.Collections.Generic;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Pure-logic tests for the P0-01 outcome/hard-gate contract. These assert the
    /// commit-gate semantics that <see cref="ToolExecutor"/> relies on
    /// (<see cref="ToolResult.MayCommitProductionObject"/>) without needing a Civil 3D host.
    /// </summary>
    public class ToolResultContractTests
    {
        #region Outcome derivation & factories

        [Fact]
        public void Ok_IsSucceeded_AndMayCommit()
        {
            var r = ToolResult.Ok(new { x = 1 });
            r.Success.Should().BeTrue();
            r.Outcome.Should().Be(ToolOutcome.Succeeded);
            r.MayCommitProductionObject.Should().BeTrue();
        }

        [Fact]
        public void Fail_IsFailed_AndMayNotCommit()
        {
            var r = ToolResult.Fail("BOOM", "kaboom");
            r.Success.Should().BeFalse();
            r.Outcome.Should().Be(ToolOutcome.Failed);
            r.MayCommitProductionObject.Should().BeFalse();
        }

        [Fact]
        public void Cancelled_IsNeutral_AndMayNotCommit()
        {
            var r = ToolResult.Cancelled(new { cancelled = true });
            // Neutral in the UI (not an error), but never commits a production object.
            r.Success.Should().BeTrue();
            r.Outcome.Should().Be(ToolOutcome.Cancelled);
            r.MayCommitProductionObject.Should().BeFalse();
        }

        [Fact]
        public void Rejected_CarriesViolations_AndMayNotCommit()
        {
            var violations = new[]
            {
                new EngineeringViolation
                {
                    Code = "FINAL_GEOMETRY_OUTSIDE_BUILDABLE",
                    Message = "arc exits buildable area",
                    Index = 3,
                    Station = 142.5,
                    Requested = 1.5,
                    Achieved = 0.4
                }
            };

            var r = ToolResult.Rejected("FINAL_GEOMETRY_OUTSIDE_BUILDABLE", "off surface", violations);

            r.Success.Should().BeFalse();
            r.Outcome.Should().Be(ToolOutcome.Rejected);
            r.MayCommitProductionObject.Should().BeFalse();
            r.Engineering.Should().NotBeNull();
            r.Engineering!.HardGatesPassed.Should().BeFalse();
            r.Engineering.Violations.Should().ContainSingle()
                .Which.Code.Should().Be("FINAL_GEOMETRY_OUTSIDE_BUILDABLE");
            r.Error!.Code.Should().Be("FINAL_GEOMETRY_OUTSIDE_BUILDABLE");
        }

        [Fact]
        public void Candidate_MayNotCommit_EvenThoughSuccessTrue()
        {
            var eng = new EngineeringGateResult
            {
                HardGatesPassed = false,
                RequiresEngineerApproval = true,
                Violations = new List<EngineeringViolation>
                {
                    new EngineeringViolation { Code = "RADIUS_RELAXED", Severity = "warning", Requested = 400, Achieved = 250 }
                }
            };

            var r = ToolResult.Candidate(new { preview = true }, eng);

            r.Success.Should().BeTrue();
            r.Outcome.Should().Be(ToolOutcome.CandidateGenerated);
            r.MayCommitProductionObject.Should().BeFalse();
            r.Engineering!.RequiresEngineerApproval.Should().BeTrue();
        }

        [Fact]
        public void RawSuccessFlag_DerivesOutcome_WhenNotSetExplicitly()
        {
            new ToolResult { Success = true }.Outcome.Should().Be(ToolOutcome.Succeeded);
            new ToolResult { Success = false }.Outcome.Should().Be(ToolOutcome.Failed);
        }

        #endregion

        #region Hard-gate coupling

        [Fact]
        public void Succeeded_WithFailingHardGate_MayNotCommit()
        {
            // The critical P0-01 case: a Succeeded outcome must STILL be blocked when a
            // hard engineering gate failed. success=true alone can no longer commit.
            var r = ToolResult.Ok(new { });
            r.Engineering = new EngineeringGateResult { HardGatesPassed = false };
            r.MayCommitProductionObject.Should().BeFalse();
        }

        [Fact]
        public void Succeeded_WithPassingHardGate_MayCommit()
        {
            var r = ToolResult.Ok(new { });
            r.Engineering = new EngineeringGateResult { HardGatesPassed = true };
            r.MayCommitProductionObject.Should().BeTrue();
        }

        [Fact]
        public void Succeeded_WithNoGate_MayCommit()
        {
            // The overwhelming majority of tools: no engineering gate → commit as before.
            ToolResult.Ok(new { }).MayCommitProductionObject.Should().BeTrue();
        }

        #endregion

        #region Wire round-trip

        [Theory]
        [InlineData("succeeded")]
        [InlineData("cancelled")]
        [InlineData("candidate_generated")]
        [InlineData("rejected")]
        [InlineData("failed")]
        public void OutcomeWireValue_RoundTrips(string wire)
        {
            // Parse the wire string back to the enum, then re-serialise: the pair must
            // be a stable round-trip. (Enum values are intentionally NOT passed via
            // InlineData — xUnit 2.x can abort sibling discovery on cross-assembly enum
            // arguments.)
            var outcome = ToolExecutor.ParseOutcome(wire, success: true);
            ToolExecutor.OutcomeWireValue(outcome).Should().Be(wire);
        }

        [Fact]
        public void ParseOutcome_FallsBackToSuccessFlag_ForLegacyPayloads()
        {
            // Older payloads (pre-P0-01) have no outcome field.
            ToolExecutor.ParseOutcome(null, success: true).Should().Be(ToolOutcome.Succeeded);
            ToolExecutor.ParseOutcome(null, success: false).Should().Be(ToolOutcome.Failed);
            ToolExecutor.ParseOutcome("nonsense", success: false).Should().Be(ToolOutcome.Failed);
        }

        #endregion
    }
}
