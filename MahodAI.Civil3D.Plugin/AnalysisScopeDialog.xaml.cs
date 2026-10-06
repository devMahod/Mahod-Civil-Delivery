using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfGrid = System.Windows.Controls.Grid;
using WpfColumnDefinition = System.Windows.Controls.ColumnDefinition;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfBorder = System.Windows.Controls.Border;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfStackPanel = System.Windows.Controls.StackPanel;

namespace MahodAI.Civil3D.Plugin
{
    /// <summary>
    /// Analysis scope selection dialog. Lets users pick which drawing aspects to analyze.
    /// </summary>
    public partial class AnalysisScopeDialog : Window
    {
        /// <summary>
        /// Selected focus areas (null = full analysis).
        /// </summary>
        public List<string>? SelectedFocusAreas { get; private set; }

        /// <summary>
        /// Selected alignment names (null = all alignments).
        /// Only set when user deselects specific alignments.
        /// </summary>
        public List<string>? SelectedAlignmentNames { get; private set; }

        /// <summary>
        /// Road type overrides per alignment (only non-default entries).
        /// Collected for both "Full" and "Selected" modes.
        /// </summary>
        public Dictionary<string, string>? RoadTypeOverrides { get; private set; }

        /// <summary>
        /// Road classification overrides per alignment (only non-default entries).
        /// Classification is only meaningful for interurban/urban road types.
        /// </summary>
        public Dictionary<string, string>? RoadClassificationOverrides { get; private set; }

        /// <summary>
        /// Cross-section overrides per alignment (only non-default entries).
        /// One of CROSS_SECTION_TYPES keys recognised by the agent
        /// (single_carriageway / dual_carriageway_2lane / dual_carriageway_4lane / urban_arterial).
        /// Added 2026-05-04 per engineer feedback on dual-carriageway false positives.
        /// </summary>
        public Dictionary<string, string>? CrossSectionOverrides { get; private set; }

        /// <summary>
        /// Project-wide topography (mountainous / hilly / flat). Affects max longitudinal slope.
        /// </summary>
        public string? Topography { get; private set; }

        private readonly List<CategoryRow> _rows = new();
        private readonly List<AlignmentRow> _alignmentRows = new();
        private WpfCheckBox? _horizontalCheckBox;
        private bool _suppressParentSync;

        private static readonly (string Value, string Label)[] RoadTypes = new[]
        {
            ("", "לא צוין"),
            ("interurban", "דרך בינעירונית"),
            ("urban", "דרך עירונית"),
            ("agricultural", "דרך חקלאית"),
            ("general", "דרך כללית"),
        };

        // Cross-section options (added 2026-05-04). Keys must match
        // CROSS_SECTION_TYPES in ai_agent/src/data/standards_israel.py.
        private static readonly (string Value, string Label)[] CrossSectionTypes = new[]
        {
            ("", "לא צוין"),
            ("single_carriageway", "חד-מסלולית"),
            ("dual_carriageway_2lane", "דו-מסלולית, 2 נתיבים בכל כיוון"),
            ("dual_carriageway_4lane", "דו-מסלולית, 3+ נתיבים בכל כיוון"),
            ("urban_arterial", "עורק עירוני"),
        };

        // Classification options per road type. Pending final review by Itamar.
        // Empty list = picker is hidden for that road type.
        private static readonly Dictionary<string, (string Value, string Label)[]> ClassificationsByRoadType = new()
        {
            ["interurban"] = new[]
            {
                ("highway", "דרך מהירה"),
                ("primary", "דרך ראשית"),
                ("regional", "דרך אזורית"),
                ("local", "דרך מקומית"),
                ("interchange_ramp", "רמפות במחלף"),
            },
            ["urban"] = new[]
            {
                ("fast_arterial", "דרך עורקית מהירה"),
                ("longitudinal", "דרך אורכית"),
                ("collector_l1", "דרך מאספת רמה 1"),
                ("collector_l2", "דרך מאספת רמה 2"),
                ("local", "דרך מקומית"),
            },
        };

        public AnalysisScopeDialog(EntityCounts counts)
        {
            InitializeComponent();

            AddCategory("גאומטריה אופקית", "horizontal_geometry", counts.Alignments, "צירים", isHorizontal: true);

            // Add alignment sub-list under horizontal geometry
            if (counts.AlignmentDetails != null && counts.AlignmentDetails.Count > 0)
            {
                AddAlignmentSubList(counts.AlignmentDetails);
            }

            AddCategory("גאומטריה אנכית", "vertical_geometry", counts.DesignProfiles, "פרופילים תכנוניים");
            AddCategory("תמרורים וסימון", "signs_markings", counts.Signs + counts.Markings, "תמרורים/סימונים");
            AddCategory("חתכים רוחביים", "cross_sections", counts.Corridors, "מסדרונות");
            AddCategory("רמפות", "ramps", counts.Ramps, "רמפות");
            AddCategory("ניקוז", "drainage", counts.PipeNetworks, "רשתות ניקוז");

            UpdateAnalyzeSelectedButton();
        }

        private void AddCategory(string label, string focusArea, int count, string entityLabel, bool isHorizontal = false)
        {
            var isEnabled = count > 0;

            var checkBox = new WpfCheckBox
            {
                IsChecked = isEnabled,
                IsEnabled = isEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            checkBox.Checked += (_, _) => UpdateAnalyzeSelectedButton();
            checkBox.Unchecked += (_, _) => UpdateAnalyzeSelectedButton();

            if (isHorizontal)
            {
                _horizontalCheckBox = checkBox;
                checkBox.Checked += (_, _) => SyncChildCheckboxes(true);
                checkBox.Unchecked += (_, _) => SyncChildCheckboxes(false);
            }

            var row = new CategoryRow { FocusArea = focusArea, CheckBox = checkBox };

            // Row container
            var grid = new WpfGrid { Margin = new Thickness(4, 6, 4, 6) };
            grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto });

            WpfGrid.SetColumn(checkBox, 0);
            grid.Children.Add(checkBox);

            var labelBlock = new WpfTextBlock
            {
                Text = label,
                FontSize = 14,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = isEnabled
                    ? new WpfSolidColorBrush(WpfColor.FromRgb(0x1E, 0x29, 0x3B))
                    : new WpfSolidColorBrush(WpfColor.FromRgb(0xA0, 0xAE, 0xC0)),
            };
            WpfGrid.SetColumn(labelBlock, 1);
            grid.Children.Add(labelBlock);

            // Badge: entity count
            var badge = new WpfBorder
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = isEnabled
                    ? new WpfSolidColorBrush(WpfColor.FromRgb(0xE8, 0xF5, 0xE9))
                    : new WpfSolidColorBrush(WpfColor.FromRgb(0xF1, 0xF5, 0xF9)),
                Child = new WpfTextBlock
                {
                    Text = $"{count} {entityLabel}",
                    FontSize = 11,
                    Foreground = isEnabled
                        ? new WpfSolidColorBrush(WpfColor.FromRgb(0x2E, 0x95, 0x35))
                        : new WpfSolidColorBrush(WpfColor.FromRgb(0xA0, 0xAE, 0xC0)),
                },
            };
            WpfGrid.SetColumn(badge, 2);
            grid.Children.Add(badge);

            CategoriesPanel.Children.Add(grid);

            // Separator (skip after last item — we have 6 categories)
            if (_rows.Count < 5)
            {
                CategoriesPanel.Children.Add(new WpfBorder
                {
                    Height = 1,
                    Background = new WpfSolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }

            _rows.Add(row);
        }

        private void AddAlignmentSubList(List<AlignmentScopeInfo> alignments)
        {
            foreach (var alignment in alignments)
            {
                // Alignment row: indent + checkbox + name + length + road type + classification + cross section
                var grid = new WpfGrid { Margin = new Thickness(28, 3, 4, 3) };
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto }); // checkbox
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // name
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto }); // length
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto }); // road type
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto }); // classification
                grid.ColumnDefinitions.Add(new WpfColumnDefinition { Width = GridLength.Auto }); // cross section

                var cb = new WpfCheckBox
                {
                    IsChecked = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                };
                cb.Checked += (_, _) => OnAlignmentCheckChanged();
                cb.Unchecked += (_, _) => OnAlignmentCheckChanged();
                WpfGrid.SetColumn(cb, 0);
                grid.Children.Add(cb);

                var nameBlock = new WpfTextBlock
                {
                    Text = alignment.Name,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0x33, 0x40, 0x55)),
                };
                WpfGrid.SetColumn(nameBlock, 1);
                grid.Children.Add(nameBlock);

                var lengthBlock = new WpfTextBlock
                {
                    Text = alignment.Length > 0 ? $"{alignment.Length:F0}m" : "",
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new WpfSolidColorBrush(WpfColor.FromRgb(0x94, 0xA3, 0xB8)),
                    Margin = new Thickness(8, 0, 8, 0),
                };
                WpfGrid.SetColumn(lengthBlock, 2);
                grid.Children.Add(lengthBlock);

                var combo = new WpfComboBox
                {
                    FontSize = 11,
                    Width = 110,
                    VerticalAlignment = VerticalAlignment.Center,
                    SelectedIndex = 0,
                };
                foreach (var (value, label) in RoadTypes)
                {
                    combo.Items.Add(new WpfComboBoxItem { Content = label, Tag = value });
                }
                WpfGrid.SetColumn(combo, 3);
                grid.Children.Add(combo);

                var classificationCombo = new WpfComboBox
                {
                    FontSize = 11,
                    Width = 130,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    Visibility = Visibility.Collapsed,
                };
                WpfGrid.SetColumn(classificationCombo, 4);
                grid.Children.Add(classificationCombo);

                combo.SelectionChanged += (_, _) => RefreshClassificationCombo(combo, classificationCombo);

                // Cross-section dropdown (added 2026-05-04 per engineer feedback).
                // Lets the user mark dual-carriageway alignments so the agent
                // applies the correct threshold variant from Tables 5.x / 6.3.
                var crossSectionCombo = new WpfComboBox
                {
                    FontSize = 11,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0),
                    SelectedIndex = 0,
                    ToolTip = "סוג חתך לרוחב הדרך (חד/דו-מסלולית, עירונית) — משפיע על ערכי הסף",
                };
                foreach (var (value, label) in CrossSectionTypes)
                {
                    crossSectionCombo.Items.Add(new WpfComboBoxItem { Content = label, Tag = value });
                }
                WpfGrid.SetColumn(crossSectionCombo, 5);
                grid.Children.Add(crossSectionCombo);

                CategoriesPanel.Children.Add(grid);

                _alignmentRows.Add(new AlignmentRow
                {
                    Name = alignment.Name,
                    CheckBox = cb,
                    RoadTypeCombo = combo,
                    ClassificationCombo = classificationCombo,
                    CrossSectionCombo = crossSectionCombo,
                });
            }

            // Separator after alignment sub-list
            CategoriesPanel.Children.Add(new WpfBorder
            {
                Height = 1,
                Background = new WpfSolidColorBrush(WpfColor.FromRgb(0xE2, 0xE8, 0xF0)),
                Margin = new Thickness(0, 2, 0, 2),
            });
        }

        private void SyncChildCheckboxes(bool isChecked)
        {
            if (_suppressParentSync) return;
            foreach (var ar in _alignmentRows)
            {
                ar.CheckBox.IsChecked = isChecked;
            }
        }

        private void OnAlignmentCheckChanged()
        {
            if (_horizontalCheckBox == null || _alignmentRows.Count == 0) return;

            _suppressParentSync = true;
            var checkedCount = _alignmentRows.Count(r => r.CheckBox.IsChecked == true);
            if (checkedCount == 0)
                _horizontalCheckBox.IsChecked = false;
            else if (checkedCount == _alignmentRows.Count)
                _horizontalCheckBox.IsChecked = true;
            else
                _horizontalCheckBox.IsChecked = null; // indeterminate
            _suppressParentSync = false;

            UpdateAnalyzeSelectedButton();
        }

        private void UpdateAnalyzeSelectedButton()
        {
            var checkedCount = _rows.Count(r => r.CheckBox.IsChecked == true);
            // Also count indeterminate (some alignments selected) as checked for horizontal
            if (_horizontalCheckBox?.IsChecked == null)
                checkedCount++; // indeterminate = partially selected = enabled
            BtnAnalyzeSelected.IsEnabled = checkedCount > 0;
        }

        private Dictionary<string, string>? CollectRoadTypeOverrides()
        {
            var overrides = new Dictionary<string, string>();
            foreach (var ar in _alignmentRows)
            {
                if (ar.RoadTypeCombo.SelectedItem is WpfComboBoxItem item)
                {
                    var value = item.Tag as string ?? "";
                    if (!string.IsNullOrEmpty(value))
                        overrides[ar.Name] = value;
                }
            }
            return overrides.Count > 0 ? overrides : null;
        }

        private Dictionary<string, string>? CollectClassificationOverrides()
        {
            var overrides = new Dictionary<string, string>();
            foreach (var ar in _alignmentRows)
            {
                if (ar.ClassificationCombo.Visibility != Visibility.Visible) continue;
                if (ar.ClassificationCombo.SelectedItem is WpfComboBoxItem item)
                {
                    var value = item.Tag as string ?? "";
                    if (!string.IsNullOrEmpty(value))
                        overrides[ar.Name] = value;
                }
            }
            return overrides.Count > 0 ? overrides : null;
        }

        private Dictionary<string, string>? CollectCrossSectionOverrides()
        {
            var overrides = new Dictionary<string, string>();
            foreach (var ar in _alignmentRows)
            {
                if (ar.CrossSectionCombo.SelectedItem is WpfComboBoxItem item)
                {
                    var value = item.Tag as string ?? "";
                    if (!string.IsNullOrEmpty(value))
                        overrides[ar.Name] = value;
                }
            }
            return overrides.Count > 0 ? overrides : null;
        }

        private static void RefreshClassificationCombo(WpfComboBox roadTypeCombo, WpfComboBox classificationCombo)
        {
            classificationCombo.Items.Clear();

            var roadTypeValue = (roadTypeCombo.SelectedItem as WpfComboBoxItem)?.Tag as string ?? "";
            if (!ClassificationsByRoadType.TryGetValue(roadTypeValue, out var options) || options.Length == 0)
            {
                classificationCombo.Visibility = Visibility.Collapsed;
                return;
            }

            classificationCombo.Items.Add(new WpfComboBoxItem { Content = "סיווג…", Tag = "" });
            foreach (var (value, label) in options)
            {
                classificationCombo.Items.Add(new WpfComboBoxItem { Content = label, Tag = value });
            }
            classificationCombo.SelectedIndex = 0;
            classificationCombo.Visibility = Visibility.Visible;
        }

        private List<string>? CollectSelectedAlignments()
        {
            if (_alignmentRows.Count == 0) return null;

            var allChecked = _alignmentRows.All(r => r.CheckBox.IsChecked == true);
            if (allChecked) return null; // all selected = same as no filter

            var selected = _alignmentRows
                .Where(r => r.CheckBox.IsChecked == true)
                .Select(r => r.Name)
                .ToList();
            return selected.Count > 0 ? selected : null;
        }

        private void BtnFullAnalysis_Click(object sender, RoutedEventArgs e)
        {
            SelectedFocusAreas = null; // null means full analysis
            SelectedAlignmentNames = null; // all alignments
            RoadTypeOverrides = CollectRoadTypeOverrides();
            RoadClassificationOverrides = CollectClassificationOverrides();
            CrossSectionOverrides = CollectCrossSectionOverrides();
            DialogResult = true;
        }

        private void BtnAnalyzeSelected_Click(object sender, RoutedEventArgs e)
        {
            SelectedFocusAreas = _rows
                .Where(r => r.CheckBox.IsChecked == true || r.CheckBox.IsChecked == null)
                .Select(r => r.FocusArea)
                .ToList();
            SelectedAlignmentNames = CollectSelectedAlignments();
            RoadTypeOverrides = CollectRoadTypeOverrides();
            RoadClassificationOverrides = CollectClassificationOverrides();
            CrossSectionOverrides = CollectCrossSectionOverrides();
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private class CategoryRow
        {
            public string FocusArea { get; set; } = "";
            public WpfCheckBox CheckBox { get; set; } = null!;
        }

        private class AlignmentRow
        {
            public string Name { get; set; } = "";
            public WpfCheckBox CheckBox { get; set; } = null!;
            public WpfComboBox RoadTypeCombo { get; set; } = null!;
            public WpfComboBox ClassificationCombo { get; set; } = null!;
            public WpfComboBox CrossSectionCombo { get; set; } = null!;
        }
    }

    /// <summary>
    /// Info about a single alignment for the scope dialog.
    /// </summary>
    public class AlignmentScopeInfo
    {
        public string Name { get; set; } = "";
        public double Length { get; set; }
    }

    /// <summary>
    /// Info about a single profile for the scope dialog.
    /// Lets the user pick which design/layout profiles to include in the
    /// vertical-geometry analysis. Without this, drawings with auxiliary or
    /// historical profiles get every one of them analyzed.
    /// </summary>
    public class ProfileScopeInfo
    {
        public string Name { get; set; } = "";
        public string AlignmentName { get; set; } = "";
    }

    /// <summary>
    /// Entity counts extracted from drawing summary JSON.
    /// </summary>
    public class EntityCounts
    {
        public int Alignments { get; set; }
        public int DesignProfiles { get; set; }
        public int Signs { get; set; }
        public int Markings { get; set; }
        public int Corridors { get; set; }
        public int Ramps { get; set; }
        public int PipeNetworks { get; set; }
        public List<AlignmentScopeInfo>? AlignmentDetails { get; set; }
        public List<ProfileScopeInfo>? ProfileDetails { get; set; }
    }
}
