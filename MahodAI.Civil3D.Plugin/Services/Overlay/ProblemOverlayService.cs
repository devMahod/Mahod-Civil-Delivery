using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.Civil.ApplicationServices;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.Utilities;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadCore = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcadDoc = Autodesk.AutoCAD.ApplicationServices.Document;
using WpfPoint = System.Windows.Point;
using MediaColor = System.Windows.Media.Color;

// Disambiguate from the WinForms / AutoCAD types pulled in by this multi-UI project.
using Visibility = System.Windows.Visibility;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using FlowDirection = System.Windows.FlowDirection;

namespace MahodAI.Civil3D.Plugin.Services.Overlay
{
    /// <summary>
    /// Floating, web-style overlay that pins analysis findings onto the AutoCAD drawing.
    /// A transparent, click-through top-level window is laid over the drawing area; each
    /// finding gets a numbered pin positioned by projecting its world coordinate to screen
    /// every tick (so it tracks pan/zoom with no drift). Clicking a pin opens a styled
    /// popup card with the problem details.
    ///
    /// The window is click-through (WS_EX_TRANSPARENT) by default — it never blocks AutoCAD
    /// input — and is made interactive only while the cursor is over a pin or open card.
    /// </summary>
    public class ProblemOverlayService
    {
        private Window? _window;
        private Canvas? _canvas;
        private DispatcherTimer? _timer;
        private IntPtr _hwnd = IntPtr.Zero;

        private List<ProblemMarker> _markers = new();
        private readonly Dictionary<ProblemMarker, PinVisual> _pins = new();
        private ProblemMarker? _openMarker;
        private Border? _openCard;
        private AcadDoc? _doc;
        private bool _clickThrough = true;

        public bool IsVisible => _window != null;

        /// <summary>The markers currently displayed (empty when hidden).</summary>
        public IReadOnlyList<ProblemMarker> Markers => _markers;

        private sealed class PinVisual
        {
            public required Canvas Element;
            public double Width;
            public double Height;
            public ScaleTransform? Scale;   // the circle's transform — used for the focus pulse
        }

        /// <summary>Per-tick screen placement for one pin (mutated by the de-stack pass).</summary>
        private sealed class PinPlacement
        {
            public required PinVisual Pin;
            public required ProblemMarker Marker;
            public double Px;
            public double Py;
        }

        /// <summary>A reusable "N markers here" badge shown in place of a collapsed pin cluster.</summary>
        private sealed class ClusterBadge
        {
            public required Border Element;
            public required TextBlock Text;
            public double WorldX;   // cluster centroid — click zooms here
            public double WorldY;
        }

        private readonly List<ClusterBadge> _clusterPool = new();

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the markers' world coordinates against the active drawing and shows the
        /// overlay. REPLACES any previously shown markers, reusing the existing overlay
        /// window when one is already up (so fix-phase re-renders — filter to fixable,
        /// recolor fixed — swap pins in place with no teardown flicker). Passing an empty
        /// list tears the overlay down (same as <see cref="Hide"/>).
        /// </summary>
        public void Show(List<ProblemMarker> markers)
        {
            if (markers == null || markers.Count == 0)
            {
                Hide();
                return;
            }

            ResolveWorldPoints(markers);
            var placeable = markers.Where(m => m.HasWorld).ToList();
            System.Diagnostics.Debug.WriteLine(
                $"[MahodAI] Overlay.Show: {markers.Count} markers, {placeable.Count} placeable on the active drawing.");
            MahodLogger.Info(
                $"Overlay.Show: {markers.Count} markers, {placeable.Count} placeable on the active drawing.");
            if (placeable.Count == 0)
            {
                Hide();
                return;
            }

            _markers = placeable;
            EnsureWindow();
            if (_window == null || _canvas == null)
                return;

            // Swap the pin set on the (possibly reused) window. Any open card belongs to
            // a now-stale marker instance, so close it first.
            CloseCard();
            _pins.Clear();
            _clusterPool.Clear();   // Children.Clear() below drops the badge elements too
            _canvas.Children.Clear();
            BuildPins();

            if (_timer == null)
                StartTimer();
            SyncOnce();
        }

        /// <summary>Hides the overlay and tears down all visuals.</summary>
        public void Hide()
        {
            StopTimer();
            CloseCard();
            _pins.Clear();
            _clusterPool.Clear();
            _markers = new();
            _openMarker = null;
            _doc = null;

            if (_canvas != null)
                _canvas.Children.Clear();

            if (_window != null)
            {
                try { _window.Close(); } catch { /* already gone */ }
                _window = null;
                _canvas = null;
                _hwnd = IntPtr.Zero;
            }
        }

        // ── Window / lifecycle ─────────────────────────────────────────────────

        private void EnsureWindow()
        {
            if (_window != null)
                return;

            _canvas = new Canvas { Background = null };
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                ResizeMode = ResizeMode.NoResize,
                Focusable = false,
                Title = "MahodProblemsOverlay",
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = _canvas,
                Left = 0,
                Top = 0,
                Width = 100,
                Height = 100,
            };

            var helper = new WindowInteropHelper(_window);
            try { helper.Owner = SafeMainWindowHandle(); } catch { /* no owner is fine */ }

            try
            {
                _window.Show();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] Overlay window Show() failed: {ex.Message}");
                _window = null;
                _canvas = null;
                return;
            }

            _hwnd = helper.Handle;
            if (_hwnd == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine("[MahodAI] Overlay window handle is zero — tearing down.");
                try { _window.Close(); } catch { }
                _window = null;
                _canvas = null;
                return;
            }

            // Tool window (no alt-tab) + transparent (click-through) by default. Do NOT OR in
            // WS_EX_LAYERED here — AllowsTransparency=true already makes WPF manage the layered
            // window; adding it manually can corrupt the layered state so it never paints.
            int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE,
                exStyle | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            _clickThrough = true;
        }

        private void StartTimer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(30),
            };
            _timer.Tick += (_, __) => SyncOnce();
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }
        }

        // ── Per-tick sync: position window + pins, anchor card, toggle click-through ──

        private void SyncOnce()
        {
            if (_window == null || _canvas == null)
                return;

            try
            {
                var doc = AcadCore.DocumentManager.MdiActiveDocument;
                if (doc == null || doc != _doc)
                {
                    _canvas.Visibility = Visibility.Hidden;
                    return;
                }

                if (!TryGetDrawingAreaRect(out var rectPx))
                {
                    _canvas.Visibility = Visibility.Hidden;
                    return;
                }

                double scale = 96.0 / GetDpi();
                _window.Left = rectPx.Left * scale;
                _window.Top = rectPx.Top * scale;
                double wDip = Math.Max(1, (rectPx.Right - rectPx.Left) * scale);
                double hDip = Math.Max(1, (rectPx.Bottom - rectPx.Top) * scale);
                _window.Width = wDip;
                _window.Height = hDip;

                var ed = doc.Editor;
                Matrix3d w2e;
                Point2d viewCenter;
                double viewW, viewH;
                using (var vtr = ed.GetCurrentView())
                {
                    w2e = WorldToEye(vtr);
                    viewCenter = vtr.CenterPoint;
                    viewW = vtr.Width;
                    viewH = vtr.Height;
                }
                if (viewW <= 0 || viewH <= 0)
                {
                    _canvas.Visibility = Visibility.Hidden;
                    return;
                }

                // Pass 1: base screen position for every visible pin; hide the off-screen ones.
                var placed = new List<PinPlacement>(_pins.Count);
                foreach (var kv in _pins)
                {
                    var m = kv.Key;
                    var pin = kv.Value;
                    if (!m.HasWorld)
                    {
                        pin.Element.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    var dcs = new Point3d(m.WorldX, m.WorldY, 0).TransformBy(w2e);
                    double nx = (dcs.X - viewCenter.X) / viewW + 0.5;
                    double ny = (dcs.Y - viewCenter.Y) / viewH + 0.5;

                    if (nx < -0.15 || nx > 1.15 || ny < -0.15 || ny > 1.15)
                    {
                        pin.Element.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    pin.Element.Visibility = Visibility.Visible;
                    placed.Add(new PinPlacement { Pin = pin, Marker = m, Px = nx * wDip, Py = (1 - ny) * hDip });
                }

                // Pass 2: cluster pins that overlap on screen. When zoomed out, a dense group
                // collapses into ONE count badge ("4"); zooming in separates them until each
                // shows on its own. The open (report-clicked) pin's group is always expanded so
                // that pin stays visible.
                LayoutClusters(placed);

                if (_openMarker != null && _openCard != null &&
                    _pins.TryGetValue(_openMarker, out var op))
                {
                    if (op.Element.Visibility == Visibility.Visible)
                    {
                        _openCard.Visibility = Visibility.Visible;
                        PositionCard(op);
                    }
                    else
                    {
                        if (_openCard.Visibility == Visibility.Visible)
                            MahodLogger.Info(
                                $"Overlay: card for pin #{_openMarker.Index} hidden — its pin is off-screen.");
                        _openCard.Visibility = Visibility.Collapsed;
                    }
                }

                _canvas.Visibility = Visibility.Visible;
                UpdateClickThrough();
            }
            catch
            {
                // Never let a sync error block AutoCAD — stay click-through.
                SetClickThrough(true);
            }
        }

        // ── World point resolution (reads the live drawing) ────────────────────

        private void ResolveWorldPoints(List<ProblemMarker> markers)
        {
            var doc = AcadCore.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            // Capture the target drawing up front. SyncOnce gates on doc == _doc only to
            // hide pins when the user switches drawings — it must NOT depend on the lock /
            // transaction below succeeding, or any hiccup would hide the canvas forever.
            _doc = doc;

            var db = doc.Database;
            CivilDocument? civilDoc = null;
            try { civilDoc = CivilDocument.GetCivilDocument(db); }
            catch { return; }
            if (civilDoc == null)
                return;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var m in markers)
                    {
                        try
                        {
                            var al = CivilObjectFinder.FindAlignmentByName(tr, civilDoc, m.Alignment);
                            if (al == null) continue;

                            double sta = Math.Max(al.StartingStation,
                                Math.Min(al.EndingStation, m.MidStation));
                            double x = 0, y = 0;
                            al.PointLocation(sta, 0, ref x, ref y);
                            m.WorldX = x;
                            m.WorldY = y;
                            m.HasWorld = true;
                        }
                        catch { /* skip this marker */ }
                    }
                    tr.Commit();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ResolveWorldPoints failed: {ex.Message}");
            }
        }

        // ── Pins ───────────────────────────────────────────────────────────────

        private void BuildPins()
        {
            if (_canvas == null) return;
            foreach (var m in _markers)
            {
                var pin = CreatePin(m);
                _pins[m] = pin;
                _canvas.Children.Add(pin.Element);
            }
        }

        // Pins closer than this (px) on screen are treated as one cluster.
        private const double ClusterThreshold = 34.0;
        private const double BadgeSize = 34.0;
        // Markers within this world distance sit on the same point (e.g. a radius + a
        // missing-spiral row on one curve) — zooming can never separate them, so such a
        // cluster is always fanned out rather than collapsed into a never-splitting badge.
        private const double CoincidentWorldEps = 0.5;

        /// <summary>
        /// Groups overlapping pins and lays them out: a group of 2+ collapses into a single
        /// count badge (unless it holds the open pin, in which case the group is fanned out so
        /// that pin stays visible); a lone pin renders where it is. Zooming in spreads pins
        /// past the threshold so groups dissolve into individual pins.
        /// </summary>
        private void LayoutClusters(List<PinPlacement> placed)
        {
            var clusters = BuildClusters(placed, ClusterThreshold);
            int badgeIdx = 0;

            foreach (var cluster in clusters)
            {
                bool expand = cluster.Count == 1
                    || (_openMarker != null && cluster.Any(p => p.Marker == _openMarker))
                    || IsCoincident(cluster);        // can't separate by zoom → never collapse

                if (expand)
                {
                    if (cluster.Count > 1)
                        SpreadCluster(cluster);      // coincident pins → fan out so all show
                    foreach (var e in cluster)
                    {
                        e.Pin.Element.Visibility = Visibility.Visible;
                        Canvas.SetLeft(e.Pin.Element, e.Px - e.Pin.Width / 2);
                        Canvas.SetTop(e.Pin.Element, e.Py - e.Pin.Height);   // tip on the point
                    }
                    continue;
                }

                // Collapse: hide the members, show one count badge at their centroid.
                double cx = 0, cy = 0, wx = 0, wy = 0;
                foreach (var e in cluster)
                {
                    e.Pin.Element.Visibility = Visibility.Collapsed;
                    cx += e.Px; cy += e.Py; wx += e.Marker.WorldX; wy += e.Marker.WorldY;
                }
                int n = cluster.Count;
                cx /= n; cy /= n; wx /= n; wy /= n;

                var badge = GetBadge(badgeIdx++);
                badge.WorldX = wx; badge.WorldY = wy;
                badge.Text.Text = n.ToString();
                badge.Element.Visibility = Visibility.Visible;
                Canvas.SetLeft(badge.Element, cx - BadgeSize / 2);
                Canvas.SetTop(badge.Element, cy - BadgeSize / 2);
            }

            for (int k = badgeIdx; k < _clusterPool.Count; k++)
                _clusterPool[k].Element.Visibility = Visibility.Collapsed;
        }

        /// <summary>True when every marker in the cluster sits on essentially the same world point.</summary>
        private static bool IsCoincident(List<PinPlacement> cluster)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var e in cluster)
            {
                var m = e.Marker;
                if (m.WorldX < minX) minX = m.WorldX;
                if (m.WorldX > maxX) maxX = m.WorldX;
                if (m.WorldY < minY) minY = m.WorldY;
                if (m.WorldY > maxY) maxY = m.WorldY;
            }
            return Math.Max(maxX - minX, maxY - minY) < CoincidentWorldEps;
        }

        /// <summary>Seed-based proximity grouping (no long chains): each free pin near the seed joins it.</summary>
        private static List<List<PinPlacement>> BuildClusters(List<PinPlacement> placed, double threshold)
        {
            var clusters = new List<List<PinPlacement>>();
            var used = new bool[placed.Count];
            for (int i = 0; i < placed.Count; i++)
            {
                if (used[i]) continue;
                var cluster = new List<PinPlacement> { placed[i] };
                used[i] = true;
                for (int j = i + 1; j < placed.Count; j++)
                {
                    if (used[j]) continue;
                    if (Math.Abs(placed[j].Px - placed[i].Px) <= threshold &&
                        Math.Abs(placed[j].Py - placed[i].Py) <= threshold)
                    {
                        cluster.Add(placed[j]);
                        used[j] = true;
                    }
                }
                clusters.Add(cluster);
            }
            return clusters;
        }

        /// <summary>Fans one cluster into a horizontal row (stable order by marker index).</summary>
        private static void SpreadCluster(List<PinPlacement> cluster)
        {
            cluster.Sort((a, b) => a.Marker.Index.CompareTo(b.Marker.Index));
            double baseX = 0, baseY = 0;
            foreach (var c in cluster) { baseX += c.Px; baseY += c.Py; }
            baseX /= cluster.Count;
            baseY /= cluster.Count;
            double spacing = cluster[0].Pin.Width * 0.9;
            for (int k = 0; k < cluster.Count; k++)
            {
                cluster[k].Px = baseX + (k - (cluster.Count - 1) / 2.0) * spacing;
                cluster[k].Py = baseY;
            }
        }

        private ClusterBadge GetBadge(int idx)
        {
            while (_clusterPool.Count <= idx)
            {
                var badge = CreateClusterBadge();
                _clusterPool.Add(badge);
                _canvas!.Children.Add(badge.Element);
            }
            return _clusterPool[idx];
        }

        private ClusterBadge CreateClusterBadge()
        {
            var text = new TextBlock
            {
                Foreground = System.Windows.Media.Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Width = BadgeSize,
                Height = BadgeSize,
                CornerRadius = new CornerRadius(BadgeSize / 2),
                Background = new SolidColorBrush(MediaColor.FromRgb(0x33, 0x41, 0x55)), // slate — distinct from pins
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(2),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 7, ShadowDepth = 1, Opacity = 0.4 },
                Cursor = Cursors.Hand,
                Child = text,
            };
            var badge = new ClusterBadge { Element = border, Text = text };
            border.MouseEnter += (_, __) => border.RenderTransform = new ScaleTransform(1.12, 1.12) { CenterX = BadgeSize / 2, CenterY = BadgeSize / 2 };
            border.MouseLeave += (_, __) => border.RenderTransform = null;
            border.MouseLeftButtonUp += (_, e) => { e.Handled = true; ZoomInToWorld(badge.WorldX, badge.WorldY); };
            return badge;
        }

        /// <summary>Zooms the drawing in ~2× centered on a point (breaks a cluster apart).</summary>
        private void ZoomInToWorld(double wx, double wy)
        {
            try
            {
                var doc = AcadCore.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                var ed = doc.Editor;
                double w, h;
                using (var cur = ed.GetCurrentView())
                {
                    w = cur.Width * 0.5;
                    h = cur.Height * 0.5;
                }
                using var view = new ViewTableRecord
                {
                    CenterPoint = new Point2d(wx, wy),
                    Width = Math.Max(1e-6, w),
                    Height = Math.Max(1e-6, h),
                };
                ed.SetCurrentView(view);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ZoomInToWorld failed: {ex.Message}");
            }
        }

        /// <summary>Brief attention pulse on a pin (used when a report row jumps to it).</summary>
        private static void PulsePin(PinVisual pin)
        {
            if (pin.Scale == null) return;
            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 1.55,
                Duration = TimeSpan.FromMilliseconds(200),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(2),
                FillBehavior = FillBehavior.Stop,   // return to base so hover scaling still works
            };
            pin.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            pin.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private PinVisual CreatePin(ProblemMarker m)
        {
            var color = PinColor(m);
            const double size = 30, tail = 11;

            var root = new Canvas { Width = size, Height = size + tail, Cursor = Cursors.Hand };

            var tri = new Polygon
            {
                Points = new PointCollection
                {
                    new WpfPoint(size / 2 - 7, size - 3),
                    new WpfPoint(size / 2 + 7, size - 3),
                    new WpfPoint(size / 2, size + tail),
                },
                Fill = new SolidColorBrush(color),
            };

            var circle = new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = new SolidColorBrush(color),
                BorderBrush = System.Windows.Media.Brushes.White,
                BorderThickness = new Thickness(2),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 7, ShadowDepth = 1, Opacity = 0.4 },
                RenderTransformOrigin = new WpfPoint(0.5, 0.5),
                Child = new TextBlock
                {
                    Text = m.Index.ToString(),
                    Foreground = new SolidColorBrush(PinForeground(m)),
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(circle, 0);
            Canvas.SetTop(circle, 0);

            root.Children.Add(tri);
            root.Children.Add(circle);

            var scale = new ScaleTransform(1, 1);
            circle.RenderTransform = scale;
            root.MouseEnter += (_, __) => { scale.ScaleX = scale.ScaleY = 1.18; };
            root.MouseLeave += (_, __) => { scale.ScaleX = scale.ScaleY = 1.0; };
            root.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnPinClicked(m); };

            return new PinVisual { Element = root, Width = size, Height = size + tail, Scale = scale };
        }

        private void OnPinClicked(ProblemMarker m)
        {
            if (_openMarker == m)
            {
                CloseCard();
                return;
            }
            OpenCardFor(m);
        }

        private void OpenCardFor(ProblemMarker m)
        {
            CloseCard();
            _openMarker = m;
            _openCard = CreateCard(m);
            _canvas!.Children.Add(_openCard);
            _openCard.UpdateLayout();
            if (_pins.TryGetValue(m, out var pin))
                PositionCard(pin);
        }

        /// <summary>
        /// Opens the popup for the pin matching a clicked report row — the
        /// "click a report row → jump to its pin" path (see the <c>zoom_to_location</c>
        /// handler). Matches on alignment + station, and — crucially — the row's problem text,
        /// so a curve carrying several violations (radius AND missing-spiral at the same
        /// station) opens the RIGHT pin instead of whichever shares the station. Returns false
        /// when the overlay is down or nothing matches. The per-tick <see cref="SyncOnce"/>
        /// places the card, so this works even right after a zoom moves the viewport.
        /// </summary>
        public bool FocusMarker(string alignment, double station, string? problem = null)
        {
            if (_canvas == null || _markers.Count == 0)
            {
                MahodLogger.Warning(
                    $"FocusMarker: overlay down (window={_window != null}, markers={_markers.Count}) — no pin to open.");
                return false;
            }

            string want = (alignment ?? string.Empty).Trim();
            bool haveProblem = !string.IsNullOrWhiteSpace(problem);
            ProblemMarker? best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var m in _markers)
            {
                if (!string.Equals((m.Alignment ?? string.Empty).Trim(), want,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                // Problem-text overlap is the primary signal (disambiguates same-station
                // violations); station proximity breaks ties and drives the no-text case.
                double overlap = haveProblem ? TextOverlap(m.Problem, problem!) : 0.0;
                double score = overlap * 1000.0 - StationDistance(m, station);
                if (score > bestScore) { bestScore = score; best = m; }
            }

            if (best == null)
            {
                // No pin carries this exact alignment name — the link and the pins can come
                // from different renders/parsers of the same table. Rather than silently doing
                // nothing, rescore across ALL pins so the click still opens the most similar one.
                foreach (var m in _markers)
                {
                    double overlap = haveProblem ? TextOverlap(m.Problem, problem!) : 0.0;
                    double score = overlap * 1000.0 - StationDistance(m, station);
                    if (score > bestScore) { bestScore = score; best = m; }
                }
                MahodLogger.Warning(
                    $"FocusMarker: no pin matched alignment '{want}' — pins carry [" +
                    string.Join(", ", _markers.Select(m => $"'{m.Alignment}'").Distinct().Take(8)) +
                    $"]; fallback → pin #{(best?.Index.ToString() ?? "none")}.");
            }
            if (best == null)
                return false;

            MahodLogger.Info(
                $"FocusMarker: opening pin #{best.Index} (align='{best.Alignment}', sta={station:F1}, " +
                $"score={bestScore:F2}, hasWorld={best.HasWorld}).");
            OpenCardFor(best);
            if (_pins.TryGetValue(best, out var pin))
                PulsePin(pin);
            SyncOnce();   // place the just-opened card without waiting for the next tick
            return true;
        }

        /// <summary>Distance from a station to a marker's [start, end] span (0 when inside it).</summary>
        private static double StationDistance(ProblemMarker m, double station)
        {
            double lo = m.StationEnd.HasValue ? Math.Min(m.StationStart, m.StationEnd.Value) : m.StationStart;
            double hi = m.StationEnd.HasValue ? Math.Max(m.StationStart, m.StationEnd.Value) : m.StationStart;
            if (station < lo) return lo - station;
            if (station > hi) return station - hi;
            return 0.0;
        }

        /// <summary>Jaccard word-overlap of two strings (0..1) — mirrors the fix matcher's metric.</summary>
        private static double TextOverlap(string? a, string? b)
        {
            var sa = Tokenize(a);
            var sb = Tokenize(b);
            if (sa.Count == 0 || sb.Count == 0) return 0.0;
            int inter = sa.Count(t => sb.Contains(t));
            int union = sa.Count + sb.Count - inter;
            return union == 0 ? 0.0 : (double)inter / union;
        }

        private static HashSet<string> Tokenize(string? s)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(s)) return set;
            foreach (var raw in s.Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '(', ')', '[', ']',
                        '/', '\\', '-', '—', '–', '"', '\'', '•', '|' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                var t = raw.Trim().ToLowerInvariant();
                if (t.Length >= 2) set.Add(t);
            }
            return set;
        }

        private void CloseCard()
        {
            if (_openCard != null && _canvas != null)
            {
                MahodLogger.Info($"Overlay: card for pin #{_openMarker?.Index} closed.");
                _canvas.Children.Remove(_openCard);
            }
            _openCard = null;
            _openMarker = null;
        }

        // ── Popup card ─────────────────────────────────────────────────────────

        private Border CreateCard(ProblemMarker m)
        {
            var sev = PinColor(m);
            var headerFg = new SolidColorBrush(PinForeground(m));
            var (headerText, noteText, noteColor) = CardStateText(m);

            var card = new Border
            {
                Width = 330,
                Background = System.Windows.Media.Brushes.White,
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xE2, 0xE8, 0xF0)),
                BorderThickness = new Thickness(1),
                FlowDirection = FlowDirection.RightToLeft,
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 20, ShadowDepth = 3, Opacity = 0.30 },
            };

            var stack = new StackPanel();

            // Header bar (severity colored, rounded top).
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = headerText,
                Foreground = headerFg,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            Grid.SetColumn(title, 0);

            var close = new TextBlock
            {
                Text = "✕",
                Foreground = headerFg,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseCard(); };
            Grid.SetColumn(close, 1);

            headerGrid.Children.Add(title);
            headerGrid.Children.Add(close);

            var header = new Border
            {
                Background = new SolidColorBrush(sev),
                CornerRadius = new CornerRadius(12, 12, 0, 0),
                Height = 38,
                Child = headerGrid,
            };
            stack.Children.Add(header);

            // Body.
            var body = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };

            body.Children.Add(MakeText(m.Problem, 13.5, FontWeights.SemiBold,
                MediaColor.FromRgb(0x1E, 0x29, 0x3B), wrap: true));

            if (noteText != null)
            {
                var noteTb = MakeText(noteText, 12.5, FontWeights.Bold, noteColor, wrap: true);
                noteTb.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(noteTb);
            }

            string metrics = BuildMetricsLine(m);
            if (!string.IsNullOrEmpty(metrics))
            {
                var metricsTb = MakeText(metrics, 12.5, FontWeights.Bold,
                    MediaColor.FromRgb(0x47, 0x55, 0x69), wrap: true);
                metricsTb.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(metricsTb);
            }

            if (!string.IsNullOrEmpty(m.LocationText))
            {
                var loc = MakeText("📍 " + m.LocationText, 11.5, FontWeights.Normal,
                    MediaColor.FromRgb(0x64, 0x74, 0x8B), wrap: true);
                loc.Margin = new Thickness(0, 8, 0, 0);
                body.Children.Add(loc);
            }

            if (!string.IsNullOrEmpty(m.Source))
            {
                var src = MakeText("📚 " + m.Source, 11, FontWeights.Normal,
                    MediaColor.FromRgb(0x94, 0xA3, 0xB8), wrap: true);
                src.Margin = new Thickness(0, 4, 0, 0);
                body.Children.Add(src);
            }

            stack.Children.Add(body);
            card.Child = stack;
            return card;
        }

        /// <summary>
        /// Header line + optional colored status note for a marker's popup, keyed on its
        /// fix state. Keeps the card wording in step with the pin color.
        /// </summary>
        private static (string headerText, string? noteText, MediaColor noteColor) CardStateText(ProblemMarker m)
        {
            switch (m.FixState)
            {
                case MarkerFixState.Fixed:
                    return ($"✔ תוקן #{m.Index}", "✔ התיקון בוצע בשרטוט", FixedGreen);
                case MarkerFixState.Failed:
                    return ($"✖ נכשל #{m.Index}", "✖ התיקון נכשל — הבעיה נותרה פתוחה", FailedRed);
                case MarkerFixState.VerifyFailed:
                    return ($"⚠ בוצע — האימות נכשל #{m.Index}",
                        "⚠ התיקון בוצע אך הערך בשרטוט אינו כנדרש", FailedRed);
                case MarkerFixState.ManualRequired:
                    return ($"⚠ דרוש טיפול ידני #{m.Index}",
                        "⚠ לא בוצע תיקון אוטומטי — נדרש טיפול ידני",
                        MediaColor.FromRgb(0xB4, 0x53, 0x09)); // amber-700 for readable text
                case MarkerFixState.Fixable:
                    return ($"⚠ בעיה #{m.Index}", "🛠 ניתן לתיקון אוטומטי", MediaColor.FromRgb(0xB4, 0x53, 0x09));
                default:
                    return ($"⚠ בעיה #{m.Index}", null, MediaColor.FromRgb(0x47, 0x55, 0x69));
            }
        }

        private static string BuildMetricsLine(ProblemMarker m)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.ActualValue)) parts.Add($"בפועל: {m.ActualValue}");
            if (!string.IsNullOrEmpty(m.Required)) parts.Add($"נדרש: {m.Required}");
            return string.Join("  •  ", parts);
        }

        private static TextBlock MakeText(string text, double size, FontWeight weight, MediaColor color, bool wrap)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = weight,
                Foreground = new SolidColorBrush(color),
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
                LineHeight = size * 1.45,
            };
        }

        private void PositionCard(PinVisual pin)
        {
            if (_openCard == null || _window == null)
                return;

            double pinLeft = Canvas.GetLeft(pin.Element);
            double pinTop = Canvas.GetTop(pin.Element);
            if (double.IsNaN(pinLeft) || double.IsNaN(pinTop))
                return;

            double pinCx = pinLeft + pin.Width / 2;
            double pinTipY = pinTop + pin.Height;

            double cardW = _openCard.ActualWidth > 0 ? _openCard.ActualWidth : _openCard.Width;
            double cardH = _openCard.ActualHeight > 0 ? _openCard.ActualHeight : 130;

            double left = pinCx - cardW / 2;
            double top = pinTop - cardH - 8;          // prefer above the pin
            if (top < 6)
                top = pinTipY + 8;                    // not enough room → below

            left = Math.Max(6, Math.Min(left, _window.Width - cardW - 6));
            top = Math.Max(6, Math.Min(top, _window.Height - cardH - 6));

            Canvas.SetLeft(_openCard, left);
            Canvas.SetTop(_openCard, top);
        }

        // ── Click-through toggle (interactive only over pins/card) ─────────────

        private void UpdateClickThrough()
        {
            SetClickThrough(!IsCursorOverInteractive());
        }

        private bool IsCursorOverInteractive()
        {
            if (_window == null || !GetCursorPos(out var cp))
                return false;

            double scale = 96.0 / GetDpi();
            double cx = cp.X * scale - _window.Left;
            double cy = cp.Y * scale - _window.Top;

            foreach (var kv in _pins)
            {
                var el = kv.Value.Element;
                if (el.Visibility != Visibility.Visible) continue;
                if (HitsElement(el, cx, cy)) return true;
            }

            foreach (var badge in _clusterPool)
            {
                if (badge.Element.Visibility != Visibility.Visible) continue;
                if (HitsElement(badge.Element, cx, cy)) return true;
            }

            if (_openCard != null && _openCard.Visibility == Visibility.Visible &&
                HitsElement(_openCard, cx, cy))
                return true;

            return false;
        }

        private static bool HitsElement(FrameworkElement el, double cx, double cy)
        {
            double l = Canvas.GetLeft(el), t = Canvas.GetTop(el);
            if (double.IsNaN(l) || double.IsNaN(t)) return false;
            double w = el.ActualWidth > 0 ? el.ActualWidth : el.Width;
            double h = el.ActualHeight > 0 ? el.ActualHeight : el.Height;
            return cx >= l && cx <= l + w && cy >= t && cy <= t + h;
        }

        private void SetClickThrough(bool on)
        {
            if (_hwnd == IntPtr.Zero || on == _clickThrough)
                return;

            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            int nw = on ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            if (nw != ex)
                SetWindowLong(_hwnd, GWL_EXSTYLE, nw);
            _clickThrough = on;
        }

        // ── AutoCAD view → screen projection ───────────────────────────────────

        private static Matrix3d WorldToEye(ViewTableRecord view)
        {
            var eyeToWorld =
                Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                Matrix3d.Displacement(view.Target - Point3d.Origin) *
                Matrix3d.PlaneToWorld(view.ViewDirection);
            return eyeToWorld.Inverse();
        }

        // ── Win32: drawing-area rect, DPI, cursor, window styles ───────────────

        private bool TryGetDrawingAreaRect(out RECT rect)
        {
            rect = default;
            try
            {
                IntPtr main = SafeMainWindowHandle();
                if (main == IntPtr.Zero)
                    return false;

                // Primary: AutoCAD's SCREENSIZE sysvar gives the graphics area pixel size.
                // Find the child window whose CLIENT size matches it — that's the drawing
                // canvas, regardless of its (version-specific) MFC window class name.
                int sx = 0, sy = 0;
                try
                {
                    var ss = (Point2d)AcadApp.GetSystemVariable("SCREENSIZE");
                    sx = (int)Math.Round(ss.X);
                    sy = (int)Math.Round(ss.Y);
                }
                catch { /* SCREENSIZE unavailable — fall through */ }

                if (sx > 40 && sy > 40)
                {
                    IntPtr best = IntPtr.Zero;
                    long bestDiff = long.MaxValue;
                    EnumChildWindows(main, (h, l) =>
                    {
                        if (!IsWindowVisible(h) || !GetClientRect(h, out var cr))
                            return true;
                        int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
                        if (cw < 40 || ch < 40) return true;
                        long diff = Math.Abs(cw - sx) + Math.Abs(ch - sy);
                        if (diff < bestDiff) { bestDiff = diff; best = h; }
                        return true;
                    }, IntPtr.Zero);

                    if (best != IntPtr.Zero && bestDiff <= 12)
                    {
                        var p = new POINT { X = 0, Y = 0 };
                        ClientToScreen(best, ref p);
                        rect = new RECT { Left = p.X, Top = p.Y, Right = p.X + sx, Bottom = p.Y + sy };
                        return true;
                    }
                }

                // Fallback: largest 'AfxFrameOrView*' child window.
                IntPtr view = FindDrawingViewByClass(main);
                if (view != IntPtr.Zero && GetWindowRect(view, out rect))
                    return true;

                // Last resort: the whole main-frame client area (includes ribbon/palettes —
                // pins may be offset, but the overlay still appears).
                if (GetClientRect(main, out var mcr))
                {
                    var p = new POINT { X = mcr.Left, Y = mcr.Top };
                    ClientToScreen(main, ref p);
                    rect = new RECT
                    {
                        Left = p.X,
                        Top = p.Y,
                        Right = p.X + (mcr.Right - mcr.Left),
                        Bottom = p.Y + (mcr.Bottom - mcr.Top),
                    };
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr FindDrawingViewByClass(IntPtr parent)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            try
            {
                EnumChildWindows(parent, (h, l) =>
                {
                    var sb = new StringBuilder(256);
                    GetClassName(h, sb, sb.Capacity);
                    string cls = sb.ToString();
                    if ((cls.IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         cls.IndexOf("OpenGL", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        IsWindowVisible(h) && GetWindowRect(h, out var r))
                    {
                        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                        if (area > bestArea)
                        {
                            bestArea = area;
                            best = h;
                        }
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch { /* fall through to caller's fallback */ }
            return best;
        }

        private static IntPtr SafeMainWindowHandle()
        {
            try { return AcadApp.MainWindow?.Handle ?? IntPtr.Zero; }
            catch { return IntPtr.Zero; }
        }

        private uint GetDpi()
        {
            try
            {
                // Prefer the monitor AutoCAD's drawing is on (the overlay may momentarily
                // sit at its 0,0 placeholder on a different-DPI monitor on first tick).
                IntPtr main = SafeMainWindowHandle();
                if (main != IntPtr.Zero)
                {
                    uint d = GetDpiForWindow(main);
                    if (d >= 48) return d;
                }
                if (_hwnd != IntPtr.Zero)
                {
                    uint d = GetDpiForWindow(_hwnd);
                    if (d >= 48) return d;
                }
            }
            catch { /* default below */ }
            return 96;
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private static readonly MediaColor FixedGreen = MediaColor.FromRgb(0x16, 0xA3, 0x4A);
        private static readonly MediaColor FailedRed = MediaColor.FromRgb(0xDC, 0x26, 0x26);
        private static readonly MediaColor PendingYellow = MediaColor.FromRgb(0xF5, 0x9E, 0x0B); // amber

        private static MediaColor SeverityColor(string sev) => sev switch
        {
            "critical" => MediaColor.FromRgb(0xDC, 0x26, 0x26), // red
            "minor" => MediaColor.FromRgb(0x25, 0x63, 0xEB),    // blue
            _ => MediaColor.FromRgb(0xEA, 0x58, 0x0C),          // orange (important)
        };

        /// <summary>
        /// Pin/card color for a marker. Every analysis pin stays on the drawing through the
        /// whole fix flow; only its color changes:
        /// <list type="bullet">
        /// <item>fixable / needs-manual-work → yellow;</item>
        /// <item>applied → green;</item>
        /// <item>errored → red;</item>
        /// <item>otherwise (fresh finding, or one this plan can't touch) → severity color.</item>
        /// </list>
        /// </summary>
        private static MediaColor PinColor(ProblemMarker m) => m.FixState switch
        {
            MarkerFixState.Fixed => FixedGreen,
            MarkerFixState.Failed => FailedRed,
            MarkerFixState.VerifyFailed => FailedRed,
            MarkerFixState.Fixable => PendingYellow,
            MarkerFixState.ManualRequired => PendingYellow,
            _ => SeverityColor(m.Severity),
        };

        /// <summary>
        /// Foreground for the pin number and card header text: near-black on the yellow
        /// (fixable / manual) pins so the number stays legible — white everywhere else.
        /// </summary>
        private static MediaColor PinForeground(ProblemMarker m) =>
            m.FixState == MarkerFixState.Fixable || m.FixState == MarkerFixState.ManualRequired
                ? MediaColor.FromRgb(0x1F, 0x29, 0x37)   // slate-800 on amber
                : Colors.White;
    }
}
