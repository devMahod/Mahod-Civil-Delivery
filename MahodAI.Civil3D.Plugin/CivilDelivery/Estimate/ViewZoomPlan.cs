using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Pure affine WCS-to-DCS projection and viewport fitting; no host or camera mutation.</summary>
internal static class ViewZoomPlan
{
    internal readonly record struct Point(double X, double Y, double Z)
    {
        internal bool Finite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    }

    // Origin is WCS origin expressed in DCS; the three vectors are transformed WCS unit axes.
    internal readonly record struct Frame(Point Origin, Point XAxis, Point YAxis, Point ZAxis);
    internal readonly record struct Fit(double CenterX, double CenterY, double Width, double Height);

    /// <summary>Screen-pixel rectangle. Y grows downward, as in Win32 window coordinates.</summary>
    internal readonly record struct PixelRect(double Left, double Top, double Right, double Bottom)
    {
        internal double Width => Right - Left;
        internal double Height => Bottom - Top;
        internal bool Valid => double.IsFinite(Left) && double.IsFinite(Top) &&
            double.IsFinite(Right) && double.IsFinite(Bottom) && Width > 0 && Height > 0;
    }

    /// <summary>
    /// Smallest share of the viewport (per axis) that must remain uncovered before the
    /// target is framed inside the uncovered part. Below it the plain framing is kept:
    /// zooming far out to dodge a palette would hide more than the palette does.
    /// </summary>
    internal const double MinFreeFraction = 0.35;

    // Model geometry may be viewed in Model or through an active layout viewport,
    // never by treating the paper-space viewport (CVPORT=1) as model coordinates.
    internal static bool AllowsModelGeometry(bool tileMode, int viewport) =>
        viewport > 0 && (tileMode || viewport > 1);

    internal static bool HasFinitePlanBounds(double[]? endpoints) =>
        endpoints is { Length: 4 } &&
        double.IsFinite(endpoints[0]) && double.IsFinite(endpoints[1]) &&
        double.IsFinite(endpoints[2]) && double.IsFinite(endpoints[3]);

    internal static bool TryCreate(Point min, Point max, Frame frame, bool perspective,
        double aspect, double margin, out Fit fit)
    {
        fit = default;
        if (!double.IsFinite(aspect) || aspect <= 0 || !double.IsFinite(margin) || margin < 1) return false;
        if (!TryProject(min, max, frame, perspective, out var minX, out var minY, out var maxX, out var maxY))
            return false;

        var width = Math.Max(maxX - minX, 1.0) * margin;
        var height = Math.Max(maxY - minY, 1.0) * margin;
        if (width / height < aspect) width = height * aspect;
        else height = width / aspect;
        var cx = minX + (maxX - minX) / 2;
        var cy = minY + (maxY - minY) / 2;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(cx) || !double.IsFinite(cy)) return false;
        fit = new Fit(cx, cy, width, height);
        return true;
    }

    /// <summary>
    /// Frames the target inside the part of a single tiled viewport that a floating window
    /// (the Civil Delivery palette) does not cover. Seen live 2026-09-24: after ShowAndVerify
    /// the datum label of STA-42676 sat under the floating palette. With no overlap, or too
    /// little uncovered room, this is exactly <see cref="TryCreate"/> at the viewport aspect.
    /// </summary>
    internal static bool TryCreateAvoidingOcclusion(Point min, Point max, Frame frame, bool perspective,
        double margin, PixelRect viewport, PixelRect occluder, out Fit fit)
    {
        fit = default;
        if (!viewport.Valid) return false;
        if (!TryCreate(min, max, frame, perspective, viewport.Width / viewport.Height, margin, out var plain))
            return false;
        var free = FreeRegion(viewport, occluder);
        if (free.Width >= viewport.Width - 0.5 && free.Height >= viewport.Height - 0.5 ||
            free.Width < viewport.Width * MinFreeFraction || free.Height < viewport.Height * MinFreeFraction)
        {
            fit = plain;
            return true;
        }
        if (!TryProject(min, max, frame, perspective, out var minX, out var minY, out var maxX, out var maxY))
            return false;

        // Scale (DCS units per pixel) so the margin-padded target fits the free slab; the
        // viewport keeps its own pixel aspect, so the view is simply larger than the target.
        var targetWidth = Math.Max(maxX - minX, 1.0) * margin;
        var targetHeight = Math.Max(maxY - minY, 1.0) * margin;
        var scale = Math.Max(targetWidth / free.Width, targetHeight / free.Height);
        var width = viewport.Width * scale;
        var height = viewport.Height * scale;
        // Put the target centre at the free slab's centre: shift the view centre the other way.
        // Pixel Y grows downward, DCS Y upward.
        var freeCenterX = (free.Left + free.Right) / 2;
        var freeCenterY = (free.Top + free.Bottom) / 2;
        var viewportCenterX = (viewport.Left + viewport.Right) / 2;
        var viewportCenterY = (viewport.Top + viewport.Bottom) / 2;
        var cx = minX + (maxX - minX) / 2 - (freeCenterX - viewportCenterX) * scale;
        var cy = minY + (maxY - minY) / 2 + (freeCenterY - viewportCenterY) * scale;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 ||
            !double.IsFinite(cx) || !double.IsFinite(cy)) return false;
        fit = new Fit(cx, cy, width, height);
        return true;
    }

    /// <summary>
    /// Whether a window client size can be the drawing canvas that holds the current viewport
    /// of SCREENSIZE pixels. The canvas is never smaller than its viewport (3% tolerance for
    /// borders) and holds at most a few viewports, so it is never many times larger either.
    /// </summary>
    internal static bool IsCanvasCandidate(double width, double height, double screenWidth, double screenHeight)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(screenWidth) || !double.IsFinite(screenHeight)) return false;
        if (screenWidth < 40 || screenHeight < 40) return false;
        return width >= 0.97 * screenWidth && height >= 0.97 * screenHeight &&
               width <= 4 * screenWidth && height <= 4 * screenHeight;
    }

    /// <summary>
    /// Index of the canvas among the client sizes of visible child windows, or -1. An exact
    /// SCREENSIZE match (a single viewport) wins. Otherwise the smallest size that still
    /// contains the current viewport: seen live 2026-09-24 on Civil 3D 2027 with two tiled
    /// viewports, SCREENSIZE 1475x788, the canvas 1912x788 and an unrelated 1896x989 embedded
    /// browser surface; window class names (AfxFrameOrView, OpenGL) identified nothing there.
    /// </summary>
    internal static int ChooseCanvasCandidate(IReadOnlyList<(double Width, double Height)> sizes,
        double screenWidth, double screenHeight)
    {
        if (sizes == null || sizes.Count == 0) return -1;
        var best = -1;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < sizes.Count; i++)
        {
            var diff = Math.Abs(sizes[i].Width - screenWidth) + Math.Abs(sizes[i].Height - screenHeight);
            if (double.IsFinite(diff) && diff <= 12 && diff < bestDiff) { best = i; bestDiff = diff; }
        }
        if (best >= 0) return best;
        var bestArea = double.MaxValue;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (!IsCanvasCandidate(sizes[i].Width, sizes[i].Height, screenWidth, screenHeight)) continue;
            var area = sizes[i].Width * sizes[i].Height;
            if (area < bestArea) { best = i; bestArea = area; }
        }
        return best;
    }

    /// <summary>
    /// Re-expresses a fit at the view's own width/height aspect. A fit computed from window
    /// pixels (1477x788 from the canvas fraction) differs from the aspect AutoCAD reports for
    /// the current view (SCREENSIZE 1475x788, 0.14% live on 24.09.2026); AutoCAD keeps the
    /// height and re-derives the width from its own aspect, so the read-back would miss the
    /// 1e-9 navigation check and the framing would be reported as failed although the view
    /// moved. The larger of the two constraints is kept, so everything framed stays visible.
    /// </summary>
    internal static Fit WithViewAspect(Fit fit, double aspect)
    {
        if (!double.IsFinite(aspect) || aspect <= 0 || fit.Width <= 0 || fit.Height <= 0) return fit;
        var height = Math.Max(fit.Height, fit.Width / aspect);
        return new Fit(fit.CenterX, fit.CenterY, height * aspect, height);
    }

    /// <summary>
    /// Pixel rectangle of one tiled viewport inside the canvas, from the viewport table
    /// record's fractional corners (origin bottom-left, 0..1). Pixel Y grows downward.
    /// </summary>
    internal static bool TryViewportRect(PixelRect canvas, double lowerLeftX, double lowerLeftY,
        double upperRightX, double upperRightY, out PixelRect viewport)
    {
        viewport = default;
        if (!canvas.Valid || !double.IsFinite(lowerLeftX) || !double.IsFinite(lowerLeftY) ||
            !double.IsFinite(upperRightX) || !double.IsFinite(upperRightY) ||
            lowerLeftX < 0 || lowerLeftY < 0 || upperRightX > 1 || upperRightY > 1 ||
            lowerLeftX >= upperRightX || lowerLeftY >= upperRightY)
            return false;
        viewport = new PixelRect(
            canvas.Left + lowerLeftX * canvas.Width,
            canvas.Top + (1 - upperRightY) * canvas.Height,
            canvas.Left + upperRightX * canvas.Width,
            canvas.Top + (1 - lowerLeftY) * canvas.Height);
        return viewport.Valid;
    }

    /// <summary>
    /// Largest full-height or full-width slab of the viewport left uncovered by the occluder.
    /// The whole viewport when the two do not overlap; an empty rectangle when fully covered.
    /// </summary>
    internal static PixelRect FreeRegion(PixelRect viewport, PixelRect occluder)
    {
        if (!viewport.Valid || !occluder.Valid) return viewport;
        var left = Math.Max(viewport.Left, occluder.Left);
        var right = Math.Min(viewport.Right, occluder.Right);
        var top = Math.Max(viewport.Top, occluder.Top);
        var bottom = Math.Min(viewport.Bottom, occluder.Bottom);
        if (left >= right || top >= bottom) return viewport;
        var best = viewport with { Right = viewport.Left };
        foreach (var candidate in new[]
                 {
                     viewport with { Right = left },
                     viewport with { Left = right },
                     viewport with { Bottom = top },
                     viewport with { Top = bottom },
                 })
        {
            if (candidate.Width > 0 && candidate.Height > 0 &&
                candidate.Width * candidate.Height > best.Width * best.Height)
                best = candidate;
        }
        return best;
    }

    private static bool TryProject(Point min, Point max, Frame frame, bool perspective,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = double.PositiveInfinity;
        minY = double.PositiveInfinity;
        maxX = double.NegativeInfinity;
        maxY = double.NegativeInfinity;
        if (perspective || !min.Finite || !max.Finite ||
            min.X > max.X || min.Y > max.Y || min.Z > max.Z ||
            !frame.Origin.Finite || !Unit(frame.XAxis) || !Unit(frame.YAxis) || !Unit(frame.ZAxis) ||
            Math.Abs(Dot(frame.XAxis, frame.YAxis)) > 1e-8 ||
            Math.Abs(Dot(frame.XAxis, frame.ZAxis)) > 1e-8 ||
            Math.Abs(Dot(frame.YAxis, frame.ZAxis)) > 1e-8)
            return false;

        // All eight corners matter in a twisted or oblique view, not just min/max.
        for (var corner = 0; corner < 8; corner++)
        {
            var x = (corner & 1) == 0 ? min.X : max.X;
            var y = (corner & 2) == 0 ? min.Y : max.Y;
            var z = (corner & 4) == 0 ? min.Z : max.Z;
            var px = frame.Origin.X + x * frame.XAxis.X + y * frame.YAxis.X + z * frame.ZAxis.X;
            var py = frame.Origin.Y + x * frame.XAxis.Y + y * frame.YAxis.Y + z * frame.ZAxis.Y;
            if (!double.IsFinite(px) || !double.IsFinite(py)) return false;
            minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
            minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
        }
        return true;
    }

    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static bool Unit(Point p) => p.Finite && Math.Abs(Dot(p, p) - 1.0) <= 1e-8;
}
