using System;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools
{
    /// <summary>
    /// Session→document targeting matrix (2026-08-03 parallel-sessions fix):
    /// a tool from session X may only touch session X's drawing.
    /// </summary>
    public class ToolTargetPlannerTests
    {
        private const string DrawingA = @"C:\Projects\road-a.dwg";
        private const string DrawingB = @"C:\Projects\road-b.dwg";

        private static readonly string[] BothOpen = { DrawingA, DrawingB };

        [Fact]
        public void NullSessionId_UsesActive_LegacyBehavior()
        {
            var plan = ToolTargetPlanner.Plan(null, new SessionBinding(true, DrawingA), BothOpen, DrawingB);
            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void NullBinding_UsesActive_LegacyBehavior()
        {
            var plan = ToolTargetPlanner.Plan("s1", null, BothOpen, DrawingB);
            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void UnknownSession_Fails_WithHebrewMessage()
        {
            var plan = ToolTargetPlanner.Plan("s1", new SessionBinding(false, null), BothOpen, DrawingA);

            Assert.Equal(ToolTargetKind.Fail, plan.Kind);
            Assert.False(string.IsNullOrWhiteSpace(plan.ErrorMessageHe));
        }

        [Fact]
        public void DrawinglessChatTab_UsesActive()
        {
            var plan = ToolTargetPlanner.Plan("s1", new SessionBinding(true, null), BothOpen, DrawingA);
            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawingClosed_Fails_AndNamesTheFile()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1", new SessionBinding(true, DrawingA), new[] { DrawingB }, DrawingB);

            Assert.Equal(ToolTargetKind.Fail, plan.Kind);
            Assert.Contains("road-a.dwg", plan.ErrorMessageHe);
        }

        [Fact]
        public void BoundDrawingClosed_NoDocumentsOpen_Fails()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1", new SessionBinding(true, DrawingA), Array.Empty<string>(), null);

            Assert.Equal(ToolTargetKind.Fail, plan.Kind);
        }

        [Fact]
        public void BoundDrawingIsActive_UsesActive()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1", new SessionBinding(true, DrawingA), BothOpen, DrawingA);

            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawingIsActive_CaseInsensitive_UsesActive()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1",
                new SessionBinding(true, DrawingA.ToUpperInvariant()),
                new[] { DrawingA, DrawingB },
                DrawingA);

            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawingOpenButNotActive_Activates()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1", new SessionBinding(true, DrawingA), BothOpen, DrawingB);

            Assert.Equal(ToolTargetKind.Activate, plan.Kind);
            Assert.Equal(DrawingA, plan.TargetDocument);
        }

        [Fact]
        public void BoundDrawingOpen_ActiveUnknown_Activates()
        {
            var plan = ToolTargetPlanner.Plan(
                "s1", new SessionBinding(true, DrawingA), BothOpen, null);

            Assert.Equal(ToolTargetKind.Activate, plan.Kind);
            Assert.Equal(DrawingA, plan.TargetDocument);
        }

        [Fact]
        public void DisplayName_ReturnsFileName_AndSurvivesInvalidPaths()
        {
            Assert.Equal("road-a.dwg", ToolTargetPlanner.DisplayName(DrawingA));
            Assert.Equal("na\"me", ToolTargetPlanner.DisplayName("na\"me"));
        }

        // ── UI actions (analyze / read drawing) share the tool policy ────────
        // Owner report 2026-08-04: pressing "analyse" from a chat tab whose
        // drawing was open but NOT active analysed the ACTIVE drawing instead,
        // opened a second tab for it, and left the original tab loading forever.

        [Fact]
        public void BoundDrawing_OpenButNotActive_IsActivated_NotSilentlySwapped()
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                boundDrawing: DrawingA,
                openDocuments: new[] { DrawingB, DrawingA },
                activeDocument: DrawingB);

            Assert.Equal(ToolTargetKind.Activate, plan.Kind);
            Assert.Equal(DrawingA, plan.TargetDocument);
        }

        [Fact]
        public void BoundDrawing_AlreadyActive_UsesActive()
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                DrawingA, new[] { DrawingA, DrawingB }, DrawingA);

            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawing_NotOpen_FailsWithAnActionableHebrewMessage()
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                DrawingA, new[] { DrawingB }, DrawingB);

            Assert.Equal(ToolTargetKind.Fail, plan.Kind);
            Assert.Contains("road-a.dwg", plan.ErrorMessageHe);
            // Must tell the engineer what to DO, not just that it failed.
            Assert.Contains("פתח", plan.ErrorMessageHe);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void DrawinglessChatTab_KeepsActiveDocumentBehaviour(string? bound)
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                bound, new[] { DrawingA }, DrawingA);

            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawing_MatchIsCaseInsensitive()
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                DrawingA.ToUpperInvariant(), new[] { DrawingA }, DrawingA);

            Assert.Equal(ToolTargetKind.UseActive, plan.Kind);
        }

        [Fact]
        public void BoundDrawing_NoDocumentsOpenAtAll_Fails()
        {
            var plan = ToolTargetPlanner.PlanForBoundDrawing(
                DrawingA, System.Array.Empty<string>(), null);

            Assert.Equal(ToolTargetKind.Fail, plan.Kind);
        }
    }
}
