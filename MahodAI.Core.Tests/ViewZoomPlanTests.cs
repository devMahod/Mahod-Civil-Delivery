using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using Xunit;
using P = MahodAI.Civil3D.Plugin.CivilDelivery.Estimate.ViewZoomPlan.Point;

namespace MahodAI.Core.Tests;

public sealed class ViewZoomPlanTests
{
    private static readonly ViewZoomPlan.Frame Identity = new(new P(0, 0, 0),
        new P(1, 0, 0), new P(0, 1, 0), new P(0, 0, 1));

    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, true)]
    [InlineData(false, 7, true)]
    [InlineData(true, 2, true)]
    [InlineData(true, 7, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 0, false)]
    public void ModelGeometryNeverNavigatesThePaperSpaceViewport(bool tileMode, int viewport, bool allowed)
        => ViewZoomPlan.AllowsModelGeometry(tileMode, viewport).Should().Be(allowed);

    [Fact]
    public void BoundsPresenceDoesNotDependOnWhetherNavigationSucceeds()
    {
        ViewZoomPlan.HasFinitePlanBounds(new[] { 100.0, 200.0, 110.0, 220.0 }).Should().BeTrue();
        ViewZoomPlan.HasFinitePlanBounds(new[] { 110.0, 220.0, 100.0, 200.0 }).Should().BeTrue();
        ViewZoomPlan.HasFinitePlanBounds(null).Should().BeFalse();
        ViewZoomPlan.HasFinitePlanBounds(Array.Empty<double>()).Should().BeFalse();
        ViewZoomPlan.HasFinitePlanBounds(new[] { 0.0, double.NaN, 1.0, 1.0 }).Should().BeFalse();
        ViewZoomPlan.HasFinitePlanBounds(new[] { 0.0, 0.0, double.PositiveInfinity, 1.0 }).Should().BeFalse();
    }

    [Fact]
    public void TranslatedTopViewProjectsWorldBoundsRelativeToTarget()
    {
        var translated = Identity with { Origin = new P(-215449, -655075, 0) };
        ViewZoomPlan.TryCreate(new P(215418, 655075, 0), new P(215480, 655096, 0),
            translated, false, 2, 1.25, out var fit).Should().BeTrue();
        fit.CenterX.Should().Be(0);
        fit.CenterY.Should().Be(10.5);
        fit.Width.Should().Be(77.5);
        fit.Height.Should().Be(38.75);
    }

    [Fact]
    public void ZeroTargetNative81BoundsRetainCorrectWorldCenter()
    {
        ViewZoomPlan.TryCreate(new P(215418.07786969343, 655075.4143364285, 0),
            new P(215480.07786969343, 655096.4143364285, 0), Identity,
            false, 1.25, 1.25, out var fit).Should().BeTrue();
        fit.CenterX.Should().BeApproximately(215449.07786969343, 1e-8);
        fit.CenterY.Should().BeApproximately(655085.9143364285, 1e-8);
    }

    [Theory]
    [InlineData(0.5, 25, 50)]
    [InlineData(2, 100, 50)]
    [InlineData(4, 200, 50)]
    public void AspectBelongsToCurrentViewportNotWholeSplitDrawing(double aspect, double width, double height)
    {
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(20, 40, 0), Identity,
            false, aspect, 1.25, out var fit).Should().BeTrue();
        fit.Width.Should().Be(width);
        fit.Height.Should().Be(height);
        (fit.Width / fit.Height).Should().Be(aspect);
    }

    [Fact]
    public void FortyFiveDegreeTwistProjectsAllCornersNotOnlyExtrema()
    {
        var s = Math.Sqrt(0.5);
        var frame = new ViewZoomPlan.Frame(new P(0, 0, 0), new P(s, s, 0),
            new P(-s, s, 0), new P(0, 0, 1));
        ViewZoomPlan.TryCreate(new P(-10, -10, 0), new P(10, 10, 0), frame,
            false, 1, 1, out var fit).Should().BeTrue();
        fit.CenterX.Should().BeApproximately(0, 1e-10);
        fit.CenterY.Should().BeApproximately(0, 1e-10);
        fit.Width.Should().BeApproximately(40 * s, 1e-10);
        fit.Height.Should().BeApproximately(40 * s, 1e-10);
    }

    [Fact]
    public void BottomViewDoesNotAssumePositiveZOrientation()
    {
        var frame = Identity with { XAxis = new P(-1, 0, 0), ZAxis = new P(0, 0, -1) };
        ViewZoomPlan.TryCreate(new P(10, 20, 0), new P(30, 30, 0), frame,
            false, 2, 1, out var fit).Should().BeTrue();
        fit.CenterX.Should().Be(-20);
        fit.CenterY.Should().Be(25);
    }

    [Fact]
    public void ObliqueOrthographicViewIncludesDepthInProjectedHeight()
    {
        var s = Math.Sqrt(0.5);
        var frame = new ViewZoomPlan.Frame(new P(0, 0, 0), new P(1, 0, 0),
            new P(0, s, -s), new P(0, s, s));
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(10, 20, 40), frame,
            false, 1, 1, out var fit).Should().BeTrue();
        fit.CenterX.Should().Be(5);
        fit.CenterY.Should().BeApproximately(30 * s, 1e-10);
        fit.Height.Should().BeApproximately(60 * s, 1e-10);
    }

    [Fact]
    public void PointBoundsHaveUsableMinimumSize()
    {
        ViewZoomPlan.TryCreate(new P(5, 6, 0), new P(5, 6, 0), Identity,
            false, 2, 1.1, out var fit).Should().BeTrue();
        fit.Should().Be(new ViewZoomPlan.Fit(5, 6, 2.2, 1.1));
    }

    [Fact]
    public void PerspectiveIsNotSilentlyTreatedAsOrthographic()
    {
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1), Identity,
            true, 1, 1, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonfiniteInputOrFrameIsRejected(double bad)
    {
        ViewZoomPlan.TryCreate(new P(bad, 0, 0), new P(1, 1, 1), Identity,
            false, 1, 1, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1),
            Identity with { Origin = new P(0, bad, 0) }, false, 1, 1, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1), Identity,
            false, bad, 1, out _).Should().BeFalse();
    }

    [Fact]
    public void ReversedBoundsDegenerateFrameInvalidAspectAndOverflowAreRejected()
    {
        ViewZoomPlan.TryCreate(new P(2, 0, 0), new P(1, 1, 1), Identity,
            false, 1, 1, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1),
            Identity with { YAxis = Identity.XAxis }, false, 1, 1, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1), Identity,
            false, 0, 1, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(1, 1, 1), Identity,
            false, 1, 0.5, out _).Should().BeFalse();
        ViewZoomPlan.TryCreate(new P(-double.MaxValue, 0, 0), new P(double.MaxValue, 1, 1), Identity,
            false, 1, 1.25, out _).Should().BeFalse();
    }

    private static readonly ViewZoomPlan.PixelRect Canvas = new(0, 0, 1000, 500);

    [Fact]
    public void NoOverlapWithTheFloatingPaletteKeepsPlainFramingAtCanvasAspect()
    {
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 2, 1.25, out var plain).Should().BeTrue();
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 1.25,
            Canvas, new ViewZoomPlan.PixelRect(2000, 0, 2500, 500), out var fit).Should().BeTrue();
        fit.Should().Be(plain);
        ViewZoomPlan.FreeRegion(Canvas, new ViewZoomPlan.PixelRect(2000, 0, 2500, 500)).Should().Be(Canvas);
    }

    [Fact]
    public void PaletteFloatingOverTheLeftOfTheCanvasFramesTheTargetInTheFreeRightSlab()
    {
        // Free slab: pixels 400..1000 (600 wide). Target 60x20 DCS at margin 1 needs 0.1 DCS/px.
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 1,
            Canvas, new ViewZoomPlan.PixelRect(0, 0, 400, 500), out var fit).Should().BeTrue();
        fit.Width.Should().BeApproximately(100, 1e-9);
        fit.Height.Should().BeApproximately(50, 1e-9);
        // View centre moves 200 px (20 DCS) left of the target centre, so the target sits at pixel 700.
        fit.CenterX.Should().BeApproximately(10, 1e-9);
        fit.CenterY.Should().BeApproximately(10, 1e-9);
        var targetPixelX = 500 + (30 - fit.CenterX) / (fit.Width / 1000);
        targetPixelX.Should().BeApproximately(700, 1e-9);
    }

    [Fact]
    public void PaletteFloatingOverTheTopOfTheCanvasFramesTheTargetBelowIt()
    {
        // Free slab: rows 100..500 (400 high). Scale = max(60/1000, 20/400) = 0.06 DCS/px.
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 1,
            Canvas, new ViewZoomPlan.PixelRect(0, 0, 1000, 100), out var fit).Should().BeTrue();
        fit.Width.Should().BeApproximately(60, 1e-9);
        fit.Height.Should().BeApproximately(30, 1e-9);
        fit.CenterX.Should().BeApproximately(30, 1e-9);
        fit.CenterY.Should().BeApproximately(13, 1e-9);
        var targetPixelY = 250 + (fit.CenterY - 10) / (fit.Height / 500);
        targetPixelY.Should().BeApproximately(300, 1e-9);
    }

    [Fact]
    public void PaletteCoveringMostOfTheCanvasKeepsPlainFramingInsteadOfZoomingFarOut()
    {
        ViewZoomPlan.TryCreate(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 2, 1.1, out var plain).Should().BeTrue();
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(60, 20, 0), Identity, false, 1.1,
            Canvas, new ViewZoomPlan.PixelRect(0, 0, 800, 500), out var fit).Should().BeTrue();
        fit.Should().Be(plain);
    }

    [Fact]
    public void FreeRegionIsTheLargestSlabBesideAPaletteInTheMiddle()
    {
        var free = ViewZoomPlan.FreeRegion(Canvas, new ViewZoomPlan.PixelRect(300, 100, 700, 400));
        free.Should().Be(new ViewZoomPlan.PixelRect(0, 0, 300, 500));
        ViewZoomPlan.FreeRegion(Canvas, new ViewZoomPlan.PixelRect(0, 0, 1000, 500)).Width.Should().Be(0);
    }

    [Fact]
    public void OcclusionAwareFramingRejectsAnInvalidCanvasOrPerspectiveView()
    {
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(1, 1, 0), Identity, false, 1,
            new ViewZoomPlan.PixelRect(0, 0, 0, 500), new ViewZoomPlan.PixelRect(0, 0, 10, 10), out _).Should().BeFalse();
        ViewZoomPlan.TryCreateAvoidingOcclusion(new P(0, 0, 0), new P(1, 1, 0), Identity, true, 1,
            Canvas, new ViewZoomPlan.PixelRect(0, 0, 10, 10), out _).Should().BeFalse();
    }

    [Fact]
    public void ActiveTiledViewportRectComesFromItsFractionalCornersWithPixelYDown()
    {
        // Live 2026-09-24: two tiled viewports, the active one spans x 0.2275..1 of the canvas.
        var canvas = new ViewZoomPlan.PixelRect(0, 100, 1909, 888);
        ViewZoomPlan.TryViewportRect(canvas, 0.22746479511260986, 0, 1, 1, out var right).Should().BeTrue();
        right.Left.Should().BeApproximately(434.23, 0.01);
        right.Right.Should().Be(1909);
        right.Top.Should().Be(100);
        right.Bottom.Should().Be(888);
        right.Width.Should().BeApproximately(1474.77, 0.01);
        ViewZoomPlan.TryViewportRect(canvas, 0, 0.5, 1, 1, out var upper).Should().BeTrue();
        upper.Top.Should().Be(100);
        upper.Bottom.Should().Be(494);
        ViewZoomPlan.TryViewportRect(canvas, 0, 0, 1, 1, out var whole).Should().BeTrue();
        whole.Should().Be(canvas);
    }

    [Theory]
    [InlineData(-0.1, 0, 1, 1)]
    [InlineData(0, 0, 1.1, 1)]
    [InlineData(0.5, 0, 0.5, 1)]
    [InlineData(0, 0.7, 1, 0.3)]
    [InlineData(double.NaN, 0, 1, 1)]
    public void InvalidViewportFractionsAreRejected(double llx, double lly, double urx, double ury)
    {
        ViewZoomPlan.TryViewportRect(new ViewZoomPlan.PixelRect(0, 0, 1000, 500), llx, lly, urx, ury, out _).Should().BeFalse();
        ViewZoomPlan.TryViewportRect(new ViewZoomPlan.PixelRect(0, 0, 0, 500), 0, 0, 1, 1, out _).Should().BeFalse();
    }

    // Live Civil 3D 2027 child-window client sizes, 2026-09-24, two tiled viewports
    // (SCREENSIZE 1475x788): the canvas is 1912x788, an embedded browser surface 1896x989.
    private static readonly (double Width, double Height)[] LiveChildren =
    {
        (1896, 989), (1896, 989), (1896, 989), (1912, 788), (1912, 788), (1912, 788), (1912, 154), (1912, 150), (1920, 53),
    };

    [Fact]
    public void SplitViewportCanvasIsTheSmallestWindowContainingTheViewportNotTheEmbeddedBrowser()
    {
        var index = ViewZoomPlan.ChooseCanvasCandidate(LiveChildren, 1475, 788);
        index.Should().Be(3);
        LiveChildren[index].Should().Be((1912, 788));
    }

    [Fact]
    public void SingleViewportCanvasIsTheExactScreenSizeMatch()
    {
        ViewZoomPlan.ChooseCanvasCandidate(LiveChildren, 1912, 788).Should().Be(3);
        ViewZoomPlan.ChooseCanvasCandidate(LiveChildren, 1908, 785).Should().Be(3);
    }

    [Fact]
    public void NoCanvasWhenNothingContainsTheViewport()
    {
        ViewZoomPlan.ChooseCanvasCandidate(new[] { (1400.0, 700.0), (200.0, 100.0), (1912.0, 154.0) }, 1475, 788).Should().Be(-1);
        ViewZoomPlan.ChooseCanvasCandidate(Array.Empty<(double, double)>(), 1475, 788).Should().Be(-1);
        ViewZoomPlan.ChooseCanvasCandidate(LiveChildren, 0, 788).Should().Be(-1);
    }

    /// <summary>
    /// The exact live geometry of 24.09.2026 (Civil 3D 2027, two tiled viewports, palette
    /// floating over the middle): canvas 1912x788 at (4,207), active viewport = right 77.25%,
    /// SCREENSIZE 1475x788, palette window 591,126-1311,1026. A section-sized target at the
    /// saved view centre must end up entirely inside the uncovered slab right of the palette.
    /// </summary>
    [Fact]
    public void LiveSplitViewportWithFloatingPaletteFramesTheTargetRightOfThePalette()
    {
        var canvas = new ViewZoomPlan.PixelRect(4, 207, 1916, 995);
        ViewZoomPlan.TryViewportRect(canvas, 0.22746479511260986, 0, 1, 1, out var viewport).Should().BeTrue();
        Math.Abs(viewport.Width - 1475).Should().BeLessThan(0.03 * 1475);
        Math.Abs(viewport.Height - 788).Should().BeLessThan(0.03 * 788);
        var palette = new ViewZoomPlan.PixelRect(591, 126, 1311, 1026);
        var free = ViewZoomPlan.FreeRegion(viewport, palette);
        free.Left.Should().Be(1311);
        free.Right.Should().Be(1916);
        free.Width.Should().BeGreaterThan(viewport.Width * ViewZoomPlan.MinFreeFraction);

        var top = new ViewZoomPlan.Frame(new P(0, 0, 0), new P(1, 0, 0), new P(0, 1, 0), new P(0, 0, 1));
        // Section STA-42676 at the saved view centre: ~30 m wide, ~18 m tall, already 8% padded.
        var min = new P(215449.08 - 15, 655086.29 - 9, 0);
        var max = new P(215449.08 + 15, 655086.29 + 9, 0);
        ViewZoomPlan.TryCreateAvoidingOcclusion(min, max, top, false, 1.25, viewport, palette, out var fit).Should().BeTrue();
        ViewZoomPlan.TryCreate(min, max, top, false, viewport.Width / viewport.Height, 1.25, out var plain).Should().BeTrue();
        fit.Should().NotBe(plain);

        // Project the target corners with the fit (DCS = WCS for a TOP view): all inside the free slab.
        double Px(ViewZoomPlan.Fit f, double x) => viewport.Left + (x - (f.CenterX - f.Width / 2)) / f.Width * viewport.Width;
        double Py(ViewZoomPlan.Fit f, double y) => viewport.Top + ((f.CenterY + f.Height / 2) - y) / f.Height * viewport.Height;
        foreach (var (x, y) in new[] { (min.X, min.Y), (max.X, min.Y), (min.X, max.Y), (max.X, max.Y) })
        {
            Px(fit, x).Should().BeInRange(free.Left, free.Right);
            Py(fit, y).Should().BeInRange(free.Top, free.Bottom);
        }
        // and the view is not absurdly wider than the plain fit
        (fit.Width / plain.Width).Should().BeLessThan(4);

        // AutoCAD reports the view at SCREENSIZE's aspect (1475/788), 0.14% narrower than the
        // pixel fit; the navigation read-back compares to 1e-9, so the fit must be re-expressed
        // at that aspect before it is applied, and the target must still sit in the free slab.
        var viewAspect = 1475.0 / 788.0;
        var applied = ViewZoomPlan.WithViewAspect(fit, viewAspect);
        (applied.Width / applied.Height).Should().BeApproximately(viewAspect, 1e-12);
        applied.Height.Should().BeGreaterThanOrEqualTo(fit.Height);
        applied.Width.Should().BeGreaterThanOrEqualTo(fit.Width - 1e-9);
        applied.CenterX.Should().Be(fit.CenterX);
        applied.CenterY.Should().Be(fit.CenterY);
        foreach (var (x, y) in new[] { (min.X, min.Y), (max.X, min.Y), (min.X, max.Y), (max.X, max.Y) })
        {
            Px(applied, x).Should().BeInRange(free.Left - 2, free.Right + 2);
            Py(applied, y).Should().BeInRange(free.Top - 2, free.Bottom + 2);
        }
    }

    [Fact]
    public void FitAtTheViewAspectKeepsTheLargerConstraintAndIsANoOpWhenAlreadyConsistent()
    {
        var wide = new ViewZoomPlan.Fit(10, 20, 152.59, 81.40);          // pixel aspect 1.8745
        var applied = ViewZoomPlan.WithViewAspect(wide, 1475.0 / 788.0); // view aspect 1.8718: width binds
        applied.Width.Should().BeApproximately(152.59, 1e-9);
        applied.Height.Should().BeApproximately(152.59 / (1475.0 / 788.0), 1e-9);
        var tall = new ViewZoomPlan.Fit(10, 20, 100, 100);
        var appliedTall = ViewZoomPlan.WithViewAspect(tall, 2.0);           // height binds
        appliedTall.Height.Should().Be(100);
        appliedTall.Width.Should().Be(200);
        var consistent = new ViewZoomPlan.Fit(1, 2, 200, 100);
        ViewZoomPlan.WithViewAspect(consistent, 2.0).Should().Be(consistent);
        ViewZoomPlan.WithViewAspect(consistent, double.NaN).Should().Be(consistent);
        ViewZoomPlan.WithViewAspect(consistent, 0).Should().Be(consistent);
    }

    [Theory]
    [InlineData(1912, 788, 1475, 788, true)]
    [InlineData(1896, 989, 1475, 788, true)]
    [InlineData(1475, 788, 1475, 788, true)]
    [InlineData(1400, 788, 1475, 788, false)]
    [InlineData(1912, 700, 1475, 788, false)]
    [InlineData(8000, 788, 1475, 788, false)]
    [InlineData(1912, 788, 30, 788, false)]
    [InlineData(double.NaN, 788, 1475, 788, false)]
    public void CanvasCandidatesContainTheViewportWithoutBeingAbsurdlyLarger(double w, double h, double sw, double sh, bool expected)
    {
        ViewZoomPlan.IsCanvasCandidate(w, h, sw, sh).Should().Be(expected);
    }
}
