using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.GraphicsInterface;
using MahodAI.CivilDelivery.Shared;
using P2 = MahodAI.CivilDelivery.Shared.SectionProjectionLogic.P2;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// The live preview while the engineer picks (2026-10): as the cursor moves, the section line that a click would
    /// mark follows it — snapped to the nearest road axis, perpendicular, ±half-width — with the station beside the
    /// axis point. Green when a click marks it; red when it would be refused (too far from the axis, already marked).
    /// Pure display: nothing is written until the drag returns and the command commits the same resolution.
    /// </summary>
    internal sealed class TypicalPickJig : DrawJig
    {
        public const string UndoKeyword = "Undo";
        public const string WidthKeyword = "Width";

        private const short ReadyColor = 3;    // green
        private const short RefusedColor = 1;  // red

        private readonly TypicalSectionPicker _picker;
        private readonly double _halfWidth;
        private readonly IReadOnlyList<P2> _taken;
        private readonly string _message;
        private readonly bool _canFinish;
        private readonly Func<double> _textHeightNow;
        private Point3d _last = new(double.NaN, double.NaN, double.NaN);

        public TypicalPickJig(TypicalSectionPicker picker, double halfWidth, IReadOnlyList<P2> taken,
            string message, bool canFinish, Func<double> textHeight)
        {
            _picker = picker;
            _halfWidth = halfWidth;
            _taken = taken;
            _message = message;
            _canFinish = canFinish;
            _textHeightNow = textHeight;
        }

        /// <summary>
        /// The label height for the zoom on screen now: the engineer zooms in while picking, and a height fixed when the
        /// command started drew the station in letters wider than the road (live 2026-11-02).
        /// </summary>
        private double _textHeight
        {
            get
            {
                var height = _textHeightNow();
                return height > 0 && double.IsFinite(height) ? height : 1.0;
            }
        }

        /// <summary>The resolution under the cursor at the moment the drag ended (null before the first move).</summary>
        public PickResolution? Current { get; private set; }

        public Point3d LastPoint => _last;

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions(_message)
            {
                UserInputControls = UserInputControls.Accept3dCoordinates |
                                    UserInputControls.GovernedByOrthoMode |
                                    (_canFinish ? UserInputControls.NullResponseAccepted : 0),
            };
            // Explicit ASCII global keywords with a Hebrew display (a keyword parsed out of a Hebrew message crashed
            // Civil — see MhdSectionsCommand).
            if (_canFinish) options.Keywords.Add(UndoKeyword, UndoKeyword, "בטל אחרון");
            options.Keywords.Add(WidthKeyword, WidthKeyword, "רוחב");
            options.AppendKeywordsToMessage = true;

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Cancel) return SamplerStatus.Cancel;
            if (result.Status != PromptStatus.OK) return SamplerStatus.OK; // keyword or Enter: the drag ends with it
            var point = result.Value;
            if (!double.IsNaN(_last.X) && point.DistanceTo(_last) < 1e-9) return SamplerStatus.NoChange;
            _last = point;
            Current = _picker.Resolve(new P2(point.X, point.Y), _halfWidth, _taken);
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(WorldDraw draw)
        {
            var current = Current;
            if (current == null) return true;
            var geometry = draw.Geometry;
            var traits = draw.SubEntityTraits;
            traits.Color = current.IsReady ? ReadyColor : RefusedColor;

            if (current.State == TypicalSectionPickLogic.PickState.NoAlignment)
            {
                // Nothing to snap to: a small red ring at the cursor says "not here".
                geometry.Circle(new Point3d(_last.X, _last.Y, 0), _textHeight * 0.5, Vector3d.ZAxis);
                return true;
            }

            var a = new Point3d(current.A.X, current.A.Y, 0);
            var b = new Point3d(current.B.X, current.B.Y, 0);
            var axis = new Point3d(current.Axis.X, current.Axis.Y, 0);
            // The same wide band the click will draw, so the preview is as easy to see as the marker.
            var width = TypicalSectionContracts.MarkerWidth(_halfWidth);
            using (var band = new Autodesk.AutoCAD.DatabaseServices.Polyline(2) { ColorIndex = current.IsReady ? ReadyColor : RefusedColor })
            {
                band.AddVertexAt(0, new Point2d(a.X, a.Y), 0, width, width);
                band.AddVertexAt(1, new Point2d(b.X, b.Y), 0, width, width);
                geometry.Draw(band);
            }
            geometry.Circle(axis, _textHeight * 0.35, Vector3d.ZAxis);
            // A dashed tie from the cursor to the axis point shows what the click snaps to.
            if (!double.IsNaN(_last.X) && new Point3d(_last.X, _last.Y, 0).DistanceTo(axis) > _textHeight * 0.5)
                geometry.WorldLine(new Point3d(_last.X, _last.Y, 0), axis);

            var rotation = TypicalSectionPickLogic.ReadableTextRotationDeg(current.TangentDeg + 90.0) * Math.PI / 180.0;
            var direction = new Vector3d(Math.Cos(rotation), Math.Sin(rotation), 0);
            var up = direction.RotateBy(Math.PI / 2, Vector3d.ZAxis);
            var label = TypicalSectionContracts.MarkerLabel(current.Station) +
                        (current.State == TypicalSectionPickLogic.PickState.TooFar ? "  >" + _halfWidth.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "m" :
                         current.State == TypicalSectionPickLogic.PickState.Duplicate ? "  x2" : "");
            var position = axis + up * (_textHeight * 0.6) - direction * (label.Length * _textHeight * 0.45);
            geometry.Text(position, Vector3d.ZAxis, direction, _textHeight, 1.0, 0.0, label);
            return true;
        }
    }
}
