using System;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>Navigation only: preserves the active viewport, target, direction and twist.</summary>
internal static class NativeViewZoomService
{
    internal const string RecoveryGuidance =
        "ודא שחלון השרטוט גלוי; עבור ללשונית Model או הפעל חלון מבט של המודל בתוך Layout, ובחר מבט אורתוגרפי לפני ניסיון נוסף.";

    /// <summary>
    /// What the last <see cref="TryZoom"/> decided and why, in one line for the activity
    /// and stage logs. Live 1.2.85 and 1.2.86 both "did nothing visible" because the plain
    /// fit equalled the saved view; without this line nobody could tell which branch ran.
    /// </summary>
    internal static string LastDiagnostic { get; private set; } = "";

    internal static bool TryZoom(Document doc, Extents3d ext, double margin)
    {
        LastDiagnostic = "not started";
        try
        {
            if (!ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc))
            {
                LastDiagnostic = "no fit: document is not the active document";
                return false;
            }
            var ed = doc.Editor;
            var viewport = Convert.ToInt32(AcadApp.GetSystemVariable("CVPORT"));
            var tileMode = doc.Database.TileMode;
            LastDiagnostic = $"no fit: CVPORT {viewport} TILEMODE {tileMode} does not show model geometry";
            if (!ViewZoomPlan.AllowsModelGeometry(tileMode, viewport)) return false;
            using (doc.LockDocument())
            using (var view = ed.GetCurrentView())
            {
                LastDiagnostic = "no fit: perspective view";
                if (view.PerspectiveEnabled) return false;
                // Same Autodesk WCS/DCS contract used by ProblemOverlayService.WorldToEye.
                // Even an untwisted TOP view can have a nonzero WCS Target.
                var worldToEye = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                    Matrix3d.Displacement(view.Target - Point3d.Origin) *
                    Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
                var origin = Point3d.Origin.TransformBy(worldToEye);
                var x = Vector3d.XAxis.TransformBy(worldToEye);
                var y = Vector3d.YAxis.TransformBy(worldToEye);
                var z = Vector3d.ZAxis.TransformBy(worldToEye);
                var frame = new ViewZoomPlan.Frame(Point(origin), Point(x), Point(y), Point(z));
                // Current view aspect belongs to this tiled viewport. SCREENSIZE may
                // describe the whole drawing area and is incorrect for split viewports.
                var aspect = view.Height > 0 ? view.Width / view.Height : double.NaN;
                // The floating palette covers part of the canvas (seen live 2026-09-24:
                // the datum label landed under it). Frame inside the uncovered part; when
                // the canvas or the palette cannot be located, keep the plain framing.
                ViewZoomPlan.Fit fit;
                string mode;
                if (TryGetOcclusion(doc, view, out var canvas, out var palette, out var why) &&
                    ViewZoomPlan.TryCreateAvoidingOcclusion(Point(ext.MinPoint), Point(ext.MaxPoint), frame,
                        view.PerspectiveEnabled, margin, canvas, palette, out fit))
                {
                    // The pixel-derived fit is re-expressed at the view's own aspect: AutoCAD
                    // keeps the height and re-derives the width, and the read-back below must
                    // match to 1e-9 (simulated 24.09.2026: 0.14% aspect gap fails it otherwise).
                    var pixelFit = fit;
                    fit = ViewZoomPlan.WithViewAspect(fit, aspect);
                    var free = ViewZoomPlan.FreeRegion(canvas, palette);
                    var engaged = free.Width < canvas.Width - 0.5 || free.Height < canvas.Height - 0.5;
                    var enough = free.Width >= canvas.Width * ViewZoomPlan.MinFreeFraction &&
                                 free.Height >= canvas.Height * ViewZoomPlan.MinFreeFraction;
                    mode = (engaged && enough ? "occlusion-aware" : engaged ? "plain (free slab below minimum)" : "plain (no overlap)") +
                           $": free={Describe(free)} view={Num(view.Width)}x{Num(view.Height)} pixelfit={Num(pixelFit.Width)}x{Num(pixelFit.Height)} {why}";
                }
                else if (ViewZoomPlan.TryCreate(Point(ext.MinPoint), Point(ext.MaxPoint), frame,
                             view.PerspectiveEnabled, aspect, margin, out fit))
                {
                    mode = "plain: " + why;
                }
                else
                {
                    LastDiagnostic = "no fit: " + why;
                    return false;
                }
                LastDiagnostic = mode + $" fit=({Num(fit.CenterX)},{Num(fit.CenterY)}) {Num(fit.Width)}x{Num(fit.Height)}" +
                                 $" was=({Num(view.CenterPoint.X)},{Num(view.CenterPoint.Y)}) {Num(view.Width)}x{Num(view.Height)}";

                view.CenterPoint = new Point2d(fit.CenterX, fit.CenterY);
                view.Width = fit.Width;
                view.Height = fit.Height;
                ed.SetCurrentView(view);
                ed.UpdateScreen();
                using var actual = ed.GetCurrentView();
                // A queued command or a successful setter alone is not completed navigation.
                var ok = ReferenceEquals(AcadApp.DocumentManager.MdiActiveDocument, doc) &&
                    tileMode == doc.Database.TileMode &&
                    viewport == Convert.ToInt32(AcadApp.GetSystemVariable("CVPORT")) &&
                    !actual.PerspectiveEnabled &&
                    Close(actual.CenterPoint.X, fit.CenterX) && Close(actual.CenterPoint.Y, fit.CenterY) &&
                    Close(actual.Width, fit.Width) && Close(actual.Height, fit.Height) &&
                    actual.Target.IsEqualTo(view.Target) &&
                    actual.ViewDirection.IsEqualTo(view.ViewDirection) &&
                    Close(actual.ViewTwist, view.ViewTwist);
                if (!ok) LastDiagnostic += " (view read-back did not match the fit)";
                return ok;
            }
        }
        catch (Exception ex)
        {
            // Do not queue ZOOM and report "shown" before the command has executed.
            LastDiagnostic = "no fit: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Pixel rectangles of the current tiled viewport and of the floating Civil Delivery
    /// palette. Canvas candidates come from <see cref="GraphicsAreaLocator"/>; with several
    /// tiled viewports (seen live 2026-09-24: two viewports, SCREENSIZE 1475x788 inside a
    /// 1912x788 canvas) the active viewport's fractional corners select its own part of the
    /// canvas. A candidate is accepted only when that part agrees with SCREENSIZE (3%) and
    /// with the view aspect (2%); otherwise plain framing is kept. A docked palette already
    /// shrinks the canvas; a hidden palette needs nothing. <paramref name="reason"/> always
    /// says what was seen.
    /// </summary>
    private static bool TryGetOcclusion(Document doc, ViewTableRecord view,
        out ViewZoomPlan.PixelRect viewport, out ViewZoomPlan.PixelRect palette, out string reason)
    {
        viewport = default;
        palette = default;
        try
        {
            if (!UI.CivilDeliveryPalette.TryGetFloatingScreenRect(out var paletteRect, out var paletteDiag))
            {
                reason = "palette not floating (" + paletteDiag + ")";
                return false;
            }
            if (!GraphicsAreaLocator.TryGetScreenSize(out var screenWidth, out var screenHeight))
            {
                reason = "SCREENSIZE unavailable";
                return false;
            }
            double lowerLeftX = 0, lowerLeftY = 0, upperRightX = 1, upperRightY = 1;
            if (doc.Database.TileMode)
            {
                using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                if (tr.GetObject(doc.Editor.ActiveViewportId, OpenMode.ForRead) is not ViewportTableRecord record)
                {
                    reason = "active viewport record unavailable";
                    return false;
                }
                lowerLeftX = record.LowerLeftCorner.X;
                lowerLeftY = record.LowerLeftCorner.Y;
                upperRightX = record.UpperRightCorner.X;
                upperRightY = record.UpperRightCorner.Y;
            }
            if (!GraphicsAreaLocator.TryGetCanvasCandidates(doc, out var candidates, out var canvasDiag))
            {
                reason = "no canvas candidate (" + canvasDiag + ") palette=" + GraphicsAreaLocator.Describe(paletteRect);
                return false;
            }
            var viewAspect = view.Height > 0 ? view.Width / view.Height : double.NaN;
            var tried = new StringBuilder();
            foreach (var c in candidates)
            {
                var canvas = new ViewZoomPlan.PixelRect(c.Left, c.Top, c.Right, c.Bottom);
                if (!ViewZoomPlan.TryViewportRect(canvas, lowerLeftX, lowerLeftY, upperRightX, upperRightY, out var vp))
                {
                    tried.Append(" [").Append(GraphicsAreaLocator.Describe(c)).Append(": bad corners]");
                    continue;
                }
                // SCREENSIZE is the current viewport in pixels: the strongest available check that
                // the canvas window and the fractional corners describe the same viewport.
                if (Math.Abs(vp.Width - screenWidth) > 0.03 * screenWidth ||
                    Math.Abs(vp.Height - screenHeight) > 0.03 * screenHeight)
                {
                    tried.Append(" [").Append(GraphicsAreaLocator.Describe(c)).Append(": viewport ")
                        .Append(Num(vp.Width)).Append('x').Append(Num(vp.Height)).Append(" != SCREENSIZE]");
                    continue;
                }
                var viewportAspect = vp.Width / vp.Height;
                if (!double.IsFinite(viewAspect) || Math.Abs(viewportAspect - viewAspect) > 0.02 * viewAspect)
                {
                    tried.Append(" [").Append(GraphicsAreaLocator.Describe(c)).Append(": aspect ")
                        .Append(Num(viewportAspect)).Append(" vs view ").Append(Num(viewAspect)).Append(']');
                    continue;
                }
                viewport = vp;
                palette = new ViewZoomPlan.PixelRect(paletteRect.Left, paletteRect.Top, paletteRect.Right, paletteRect.Bottom);
                reason = $"canvas={GraphicsAreaLocator.Describe(c)} viewport={Describe(vp)} palette={GraphicsAreaLocator.Describe(paletteRect)} ({paletteDiag})" +
                         $" corners=({Num(lowerLeftX)},{Num(lowerLeftY)})-({Num(upperRightX)},{Num(upperRightY)}) {canvasDiag}";
                return viewport.Valid && palette.Valid;
            }
            reason = $"no canvas candidate matched SCREENSIZE {screenWidth}x{screenHeight} / view aspect {Num(viewAspect)}:{tried} {canvasDiag}" +
                     $" palette={GraphicsAreaLocator.Describe(paletteRect)} ({paletteDiag})";
            return false;
        }
        catch (Exception ex)
        {
            reason = "occlusion lookup failed: " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static string Describe(ViewZoomPlan.PixelRect r) => $"{Num(r.Left)},{Num(r.Top)}-{Num(r.Right)},{Num(r.Bottom)}";
    private static string Num(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static ViewZoomPlan.Point Point(Point3d p) => new(p.X, p.Y, p.Z);
    private static ViewZoomPlan.Point Point(Vector3d p) => new(p.X, p.Y, p.Z);
    private static bool Close(double a, double b) => double.IsFinite(a) && double.IsFinite(b) &&
        Math.Abs(a - b) <= Math.Max(1e-6, Math.Abs(b) * 1e-9);
}
