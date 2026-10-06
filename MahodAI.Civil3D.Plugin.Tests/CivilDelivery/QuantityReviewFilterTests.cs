using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Windows.Data;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantityReviewFilterTests
{
    private static QuantityRowViewModel Row(string key = "kerb", string? code = null) => new()
    {
        RuleKey = key, Layer = "HW-CURB", EntityType = "LWPOLYLINE", Method = "polyline-length",
        ObjectCount = 297, Quantity = 29287.62954513264, Unit = "מטר", MappingState = "לבדיקה",
        CatalogCode = code, CatalogDescription = code == null ? null : "אבן שפה",
        ProposedCode = "U51.06.1900", Findings = "EST-MIXED-DIMENSION-LAYER",
    };

    [Fact]
    public void ProposalTooltipRefreshesAfterTheOverviewPopulatesBoundRows()
    {
        var row = Row();
        var changed = new System.Collections.Generic.List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        row.ProposalSearchText = "W1 · צינור מים";
        Assert.Contains(nameof(row.CatalogCodeDetail), changed);
        Assert.Contains("צינור מים", row.CatalogCodeDetail);
        Assert.Null(row.CatalogCode);
    }

    [Fact]
    public void HebrewProposalSearchAndWorklistViewsDoNotApproveOrHideMeasurementAlternatives()
    {
        var row = Row();
        row.ProposalSearchText = "U57.02.0010 · צינור מים; חשמל אינו הוכחת מפרט";
        Assert.True(QuantityReviewFilter.Matches(row, "מים", QuantityReviewFilter.Mode.Proposed));
        Assert.False(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.WithoutProposal));
        Assert.False(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.Mapped));
        Assert.Null(row.CatalogCode);
        row.AlternativeRuleKey = "other"; row.AlternativeCatalogCode = "U.OTHER";
        Assert.False(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.Proposed));
        Assert.True(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.All));
        Assert.Equal(29287.62954513264, row.Quantity);
    }

    [Fact]
    public void SearchFindsLayerCodeDescriptionAndFindingWithAllTokensAndBidiPastes()
    {
        var row = Row(code: "U51.06.1900");
        Assert.True(QuantityReviewFilter.Matches(row, "hw-curb אבן", QuantityReviewFilter.Mode.All));
        Assert.True(QuantityReviewFilter.Matches(row, "\u2066U51.06.1900\u2069", QuantityReviewFilter.Mode.All));
        Assert.True(QuantityReviewFilter.Matches(row, "mixed-dimension", QuantityReviewFilter.Mode.All));
        Assert.False(QuantityReviewFilter.Matches(row, "hw-curb מים", QuantityReviewFilter.Mode.All));
    }

    [Fact]
    public void ProposalIsNotMappingAndUnmappedFilterDoesNotDiscardTheMeasurement()
    {
        var row = Row();
        Assert.True(QuantityReviewFilter.Matches(row, "1900", QuantityReviewFilter.Mode.Unmapped));
        Assert.False(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.Mapped));
        Assert.Null(row.CatalogCode); Assert.Equal(29287.62954513264, row.Quantity);
        row.CatalogCode = "U51.06.1900";
        Assert.True(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.Mapped));
        Assert.False(QuantityReviewFilter.Matches(row, "", QuantityReviewFilter.Mode.Unmapped));
    }

    [Fact]
    public void IgnoredAndHistoricalRowsRemainVisibleInAllAndTheirExplicitViews()
    {
        var ignored = Row(); ignored.IsIgnored = true;
        var history = Row(code: "U51.06.1900"); history.HistoricalReason = "previous source only";
        Assert.True(QuantityReviewFilter.Matches(ignored, "", QuantityReviewFilter.Mode.All));
        Assert.True(QuantityReviewFilter.Matches(ignored, "", QuantityReviewFilter.Mode.Ignored));
        Assert.False(QuantityReviewFilter.Matches(ignored, "", QuantityReviewFilter.Mode.Unmapped));
        Assert.True(QuantityReviewFilter.Matches(history, "", QuantityReviewFilter.Mode.History));
        Assert.False(QuantityReviewFilter.Matches(history, "", QuantityReviewFilter.Mode.Mapped));
    }

    [Fact]
    public void CompiledWpfViewFiltersAndRestoresWithoutChangingTheSourceOrUnresolvedRows()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rows = new ObservableCollection<QuantityRowViewModel>(new[] { Row(), Row("other", "TEST.2") });
                rows[1].IsIgnored = true;
                var view = new ListCollectionView(rows);
                view.Filter = item => QuantityReviewFilter.Matches((QuantityRowViewModel)item, "1900", QuantityReviewFilter.Mode.Unmapped);
                Assert.Single(view.Cast<QuantityRowViewModel>()); Assert.Equal(2, rows.Count);
                Assert.Null(rows[0].CatalogCode); Assert.True(rows[1].IsIgnored);
                view.Filter = item => QuantityReviewFilter.Matches((QuantityRowViewModel)item, "", QuantityReviewFilter.Mode.All);
                Assert.Equal(2, view.Count); Assert.Same(rows[0], view.GetItemAt(0));
                view.Filter = _ => false;
                Assert.Equal(0, view.Count); Assert.Equal(2, rows.Count);
                Assert.Equal(297, rows[0].ObjectCount);
            }
            catch (Exception failure) { error = failure; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (error != null) throw new InvalidOperationException("WPF quantity view test failed", error);
    }
}
