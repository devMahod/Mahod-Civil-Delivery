using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class EstimateReviewPagerTests
{
    private static EstimateReviewPolicy.Issue Issue(int i, string? detail = null) => new(
        "סריקה", "EST-UNMAPPED", $"ממצא-{i:D6}", detail ?? $"exact-detail-{i:D6}",
        "בדוק שיוך", true, EstimateReviewPolicy.Recovery.Mapping,
        new[] { $"record-{i:D6}" }, new[] { "rule-1" });

    [Fact]
    public void SelectedIssueAndNextActionPrecedeLongGlobalScanContext()
    {
        var issue = Issue(1, "selected-exact-detail") with
        {
            Sources = new[] { new MahodAI.CivilDelivery.Shared.ProvenanceRef
            {
                SourceKind = "xref", SourcePathOrUri = @"C:\local\selected.dwg",
                DrawingChecksum = new string('a', 64), SourceHandle = "AB/CD", XrefPath = "GM > nested",
            } },
        };
        var pager = new EstimateReviewPager(new string('g', 20000), "host.dwg",
            new[] { ((string?)"global-other-xref.dwg", (string?)"global-chain") }, new[] { issue });
        pager.Text.Should().Contain(issue.Title).And.Contain("הצעד הבא: בדוק שיוך")
            .And.Contain("selected.dwg").And.Contain("AB/CD").And.Contain("selected-exact-detail");
        pager.Text.IndexOf("הצעד הבא:", StringComparison.Ordinal).Should().BeLessThan(
            pager.Text.IndexOf("הקשר הסריקה הכללי", StringComparison.Ordinal));
        var all = new StringBuilder();
        do { all.Append(pager.Text); } while (pager.MoveNext());
        all.ToString().Should().Contain("global-other-xref.dwg").And.Contain(new string('g', 20000));
    }

    [Fact]
    public void MissingVerifiedSourceExplainsWhyNavigationIsUnavailableWithoutHidingRecord()
    {
        var text = EstimateGuidedReviewText.BuildUnified(null, null, Array.Empty<(string?, string?)>(), new[] { Issue(7) });
        text.Should().Contain("לא קיימת זהות מקור מוכחת לאיתור").And.Contain("record-000007").And.Contain("בדוק שיוך");
    }

    [Fact]
    public void AllIssuesAcrossMultipleBatchesRemainAccessibleInOrder()
    {
        var issues = Enumerable.Range(0, 47).Select(i => Issue(i)).ToArray();
        var pager = new EstimateReviewPager("blocked", "host.dwg", Array.Empty<(string?, string?)>(), issues);
        var text = new StringBuilder();
        do { pager.Text.Length.Should().BeLessThanOrEqualTo(EstimateReviewPager.CharactersPerPage); text.Append(pager.Text); }
        while (pager.MoveNext());
        foreach (var issue in issues) text.ToString().Should().Contain(issue.Detail).And.Contain(issue.RecordIds[0]);
        pager.CanNext.Should().BeFalse();
        while (pager.MovePrevious()) { }
        pager.Text.Should().Contain("exact-detail-000000");
        pager.CanPrevious.Should().BeFalse();
        issues.Length.Should().Be(47);
    }

    [Fact]
    public void LongEvidenceIsPagedWithoutLosingCharactersOrRecordIdentities()
    {
        var detail = string.Concat(Enumerable.Range(0, 19000).Select(i => $"value-{i};"));
        var issues = new[] { Issue(1, detail) };
        var expected = EstimateGuidedReviewText.BuildUnified(null, null, Array.Empty<(string?, string?)>(), issues);
        var pager = new EstimateReviewPager(null, null, Array.Empty<(string?, string?)>(), issues);
        var text = new StringBuilder();
        do { text.Append(pager.Text); } while (pager.MoveNext());
        text.ToString().Should().Be(expected);
    }

    [Fact]
    public void FullModelScaleFormatsOnlyFirstBatchAndBoundsSelectedRowSummary()
    {
        var issues = Enumerable.Range(0, 75321).Select(i => Issue(i)).ToArray();
        var pager = new EstimateReviewPager("blocked", null, Array.Empty<(string?, string?)>(), issues);
        pager.IssueCount.Should().Be(75321);
        pager.BlockingCount.Should().Be(75321);
        pager.Text.Should().Contain("exact-detail-000000").And.NotContain("exact-detail-000020");
        pager.Text.Length.Should().BeLessThanOrEqualTo(EstimateReviewPager.CharactersPerPage);
        var summary = EstimateReviewPager.RowSummary(issues);
        summary.Length.Should().BeLessThan(1000);
        summary.Should().Contain("EST-UNMAPPED").And.Contain("כמויות וממצאים");
        issues.All(issue => issue.Blocking).Should().BeTrue();
    }

    [Fact]
    public void RecoveryBatchMovesWithPageAndNeverContainsAnotherBatch()
    {
        var issues = Enumerable.Range(0, 47).Select(i => Issue(i)).ToArray();
        var pager = new EstimateReviewPager(null, null, Array.Empty<(string?, string?)>(), issues);
        pager.CurrentBatch.Should().Equal(issues.Take(20));
        while (ReferenceEquals(pager.CurrentBatch[0], issues[0])) pager.MoveNext().Should().BeTrue();
        pager.CurrentBatch.Should().Equal(issues.Skip(20).Take(20));
        pager.MovePrevious().Should().BeTrue();
        pager.CurrentBatch.Should().Equal(issues.Take(20));
    }

    [Fact]
    public void EmptyReviewRetainsSourceContextAndHasNoNavigation()
    {
        var pager = new EstimateReviewPager("ready", "exact-host.dwg",
            new[] { ((string?)"exact-xref.dwg", (string?)"chain") }, Array.Empty<EstimateReviewPolicy.Issue>());
        pager.Text.Should().Contain("exact-host.dwg").And.Contain("exact-xref.dwg");
        pager.CanNext.Should().BeFalse();
        pager.CanPrevious.Should().BeFalse();
        pager.IssueCount.Should().Be(0);
    }
}
