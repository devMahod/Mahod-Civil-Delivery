using System;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilDb = Autodesk.Civil.DatabaseServices;
using CivilStyles = Autodesk.Civil.DatabaseServices.Styles;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// Product-owned SectionView presentation used only when no preserved manual view
    /// supplies the office style. Natalie/Arthur's reference is a clean typical section,
    /// not a Civil analysis grid: all native axes, repeated elevation annotations,
    /// graph title and grids are hidden. Section geometry remains native Civil data and
    /// the tool draws one explicit datum plus its owned annotations above it.
    /// </summary>
    internal static class SectionViewPresentationStyleService
    {
        internal const string StyleName = "MHD-TYPICAL-SECTION-V2";
        internal const string StyleDescription =
            "Mahod-owned clean typical-section style v1: native Civil grid/axes hidden; one owned datum annotation.";

        internal static ObjectId Ensure(Transaction tr, CivilDocument civilDoc, bool allowModify = true)
        {
            var styles = civilDoc.Styles.SectionViewStyles;
            var existed = styles.Contains(StyleName);
            var styleId = existed
                ? styles[StyleName]
                : styles.Add(StyleName);
            var style = (CivilStyles.SectionViewStyle)tr.GetObject(
                styleId, existed ? OpenMode.ForRead : OpenMode.ForWrite);

            if (existed && !string.Equals(
                    style.Description, StyleDescription, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"SectionView style name collision: '{StyleName}' is not Mahod-owned; it was not modified.");
            if (existed)
            {
                // A style every managed view references: selected scope validates,
                // never modifies (review, 02/09).
                var action = SharedResourceLogic.Decide(true, IsCompliant(style),
                    allowModify ? SharedResourceLogic.Mode.NormalizeAll
                                : SharedResourceLogic.Mode.CreateOnlyNeverModify);
                if (action == SharedResourceLogic.Action.NoOp) return styleId;
                if (action == SharedResourceLogic.Action.Block)
                    throw new InvalidOperationException(
                        $"SectionView style '{StyleName}' exists but differs from the contract; selected APPLY does " +
                        $"not modify shared styles ({SectionFindingCodes.SharedResourceChangeRequired}).");
                style.UpgradeOpen();
            }
            style.Description = StyleDescription;

            // A Civil document default displayed repeated 285/290/... values on both
            // sides and a huge title in the failed 1.2.13 acceptance run. Hide every
            // SectionView presentation component deterministically; our tool-owned
            // annotation layer carries the axis, dimensions, strips and one datum.
            foreach (CivilStyles.SectionViewDisplayStyleType component in
                     Enum.GetValues(typeof(CivilStyles.SectionViewDisplayStyleType)))
            {
                var display = style.GetDisplayStylePlan(component);
                display.Visible = false;
                if (display.Visible)
                    throw new InvalidOperationException(
                        $"SectionView style component '{component}' remained visible after write-back.");
            }

            // Do not write the ShowTickAndLabel property on any SectionView axis. Civil
            // 3D 2027 rejects these setters for product-owned styles at runtime (the
            // first rejected axis varied across live candidates). Every axis, tick and
            // label is already hidden by the display-component loop above.
            style.GraphStyle.VerticalExaggeration = 1.0;

            // No invisible grid padding should keep the actual plot extents tall/wide.
            style.GridStyle.AxisOffsetAbove = 0;
            style.GridStyle.AxisOffsetBottom = 0;
            style.GridStyle.AxisOffsetLeft = 0;
            style.GridStyle.AxisOffsetRight = 0;
            style.GridStyle.GridPaddingAbove = 0;
            style.GridStyle.GridPaddingBottom = 0;
            style.GridStyle.GridPaddingLeft = 0;
            style.GridStyle.GridPaddingRight = 0;

            if (!string.Equals(style.Description, StyleDescription, StringComparison.Ordinal) ||
                Math.Abs(style.GraphStyle.VerticalExaggeration - 1.0) > 1e-9 ||
                style.GridStyle.AxisOffsetAbove != 0 ||
                style.GridStyle.AxisOffsetBottom != 0 ||
                style.GridStyle.AxisOffsetLeft != 0 ||
                style.GridStyle.AxisOffsetRight != 0 ||
                style.GridStyle.GridPaddingAbove != 0 ||
                style.GridStyle.GridPaddingBottom != 0 ||
                style.GridStyle.GridPaddingLeft != 0 ||
                style.GridStyle.GridPaddingRight != 0)
                throw new InvalidOperationException(
                    $"SectionView style '{StyleName}' failed its live property read-back.");

            return styleId;
        }

        private static bool IsCompliant(CivilStyles.SectionViewStyle style)
        {
            if (!string.Equals(style.Description, StyleDescription, StringComparison.Ordinal)) return false;
            foreach (CivilStyles.SectionViewDisplayStyleType component in
                     Enum.GetValues(typeof(CivilStyles.SectionViewDisplayStyleType)))
            {
                if (style.GetDisplayStylePlan(component).Visible) return false;
            }
            return Math.Abs(style.GraphStyle.VerticalExaggeration - 1.0) <= 1e-9 &&
                   style.GridStyle.AxisOffsetAbove == 0 && style.GridStyle.AxisOffsetBottom == 0 &&
                   style.GridStyle.AxisOffsetLeft == 0 && style.GridStyle.AxisOffsetRight == 0 &&
                   style.GridStyle.GridPaddingAbove == 0 && style.GridStyle.GridPaddingBottom == 0 &&
                   style.GridStyle.GridPaddingLeft == 0 && style.GridStyle.GridPaddingRight == 0;
        }

        internal static void Apply(
            Transaction tr, CivilDocument civilDoc, CivilDb.SectionView view)
        {
            view.StyleId = Ensure(tr, civilDoc);
        }

        internal static void Apply(CivilDb.SectionView view, ObjectId ensuredStyleId)
        {
            if (!view.IsWriteEnabled)
                throw new InvalidOperationException(
                    "SectionView must be open ForWrite before applying its presentation style.");
            if (ensuredStyleId.IsNull || ensuredStyleId.IsErased)
                throw new InvalidOperationException(
                    "The once-per-batch SectionView presentation style is unavailable.");
            view.StyleId = ensuredStyleId;
        }

        /// <summary>
        /// Read-only eligibility proof for reusing a foreign/manual SectionView.  The
        /// object is never restyled: it is reusable only when its live native style
        /// already hides every grid/axis/title component and it carries no native band
        /// rows.  This keeps the mandatory single owned datum from sitting alongside
        /// repeated Civil elevation labels.
        /// </summary>
        internal static bool TryReadSingleDatumCompatibility(
            Transaction tr,
            CivilDb.SectionView view,
            out bool compatible,
            out string evidence,
            int? knownBandCount = null)
        {
            compatible = false;
            evidence = "unreadable";
            try
            {
                if (view.StyleId.IsNull ||
                    tr.GetObject(view.StyleId, OpenMode.ForRead) is not CivilStyles.SectionViewStyle style)
                {
                    evidence = "section-view style is missing or unreadable";
                    return false;
                }

                var visible = new System.Collections.Generic.List<string>();
                foreach (CivilStyles.SectionViewDisplayStyleType component in
                         Enum.GetValues(typeof(CivilStyles.SectionViewDisplayStyleType)))
                {
                    if (style.GetDisplayStylePlan(component).Visible)
                        visible.Add(component.ToString());
                }

                var bandCount = knownBandCount ?? ReadBandCount();
                compatible = visible.Count == 0 && bandCount == 0;
                evidence = $"visible_components={string.Join(",", visible)}; native_band_items={bandCount}";
                return true;

                int ReadBandCount()
                {
                    using var bottomBands = view.Bands.GetBottomBandItems();
                    using var topBands = view.Bands.GetTopBandItems();
                    return Count(bottomBands) + Count(topBands);
                }
            }
            catch (Exception ex)
            {
                evidence = $"presentation read failed: {ex.GetType().Name}: {ex.Message}";
                return false;
            }

            static int Count(System.Collections.IEnumerable items)
            {
                var count = 0;
                foreach (var _ in items) count++;
                return count;
            }
        }
    }
}
