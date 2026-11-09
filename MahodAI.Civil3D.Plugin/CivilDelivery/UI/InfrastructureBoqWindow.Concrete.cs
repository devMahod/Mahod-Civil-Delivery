using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Infrastructure;
using CheckBox = System.Windows.Controls.CheckBox;
using DataGrid = System.Windows.Controls.DataGrid;
using Grid = System.Windows.Controls.Grid;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Binding = System.Windows.Data.Binding;
using Key = System.Windows.Input.Key;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using Theme = MahodAI.Civil3D.Plugin.CivilDelivery.UI.CivilDeliveryTheme;
using Visibility = System.Windows.Visibility;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Concrete in the bill of quantities (the task the owner parked on 2026-11-06): every wall, footing, pile, head beam, slab
/// and column on the plan gets its concrete, lean concrete, pile metres and rebar as BOQ rows. Its section is what the
/// engineer typed here, else what the drawing shows, else a typical value — marked "ברירת מחדל" on the screen, on every row
/// and in the Excel, like the pavement defaults.
/// </summary>
internal sealed partial class InfrastructureBoqWindow
{
    internal sealed record KindChoice(ConcreteKind Kind, string Label);
    private static readonly KindChoice[] KindChoices = Enum.GetValues<ConcreteKind>().Select(k => new KindChoice(k, StructureConcrete.KindName(k))).ToArray();

    /// <summary>One line of the concrete table: an element, or a structures layer that is not (yet) marked as concrete.</summary>
    internal sealed class ConcreteRow
    {
        public required InfrastructureGroup Group { get; init; }
        public StructureConcrete.Element? Element { get; init; }
        public ConcreteKind Kind => Element?.Kind ?? ConcreteKind.None;
        public string KindName => StructureConcrete.KindName(Kind);
        public string LayerName => Group.ShortLayer;
        public string Basis => Element is { } e ? $"{e.BasisValue:N2} {StructureConcrete.BasisUnit(e.Kind)}" : Plain(Group);
        public string Origin => Element?.Origin ?? "—";
        public double ConcreteM3 => Element?.ConcreteM3 ?? 0;
        public double RebarTon => Element?.RebarTon ?? 0;
        private static string Plain(InfrastructureGroup g) => g.CountUnits > 0 ? $"{g.CountUnits:N0} יח׳" : g.AreaSquareMetres > 0 ? $"{g.AreaSquareMetres:N2} מ״ר" : $"{g.LengthMetres:N2} מטר";
    }

    private sealed record SizeField(string Name, string Label, Func<ConcreteSizes, double?> Get, Func<ConcreteSizes, double?, ConcreteSizes> Set);
    private static readonly SizeField Basis = new("", "כמות בתכנית", s => s.Basis, (s, v) => s with { Basis = v });
    private static readonly Dictionary<ConcreteKind, SizeField[]> SizeFields = new()
    {
        [ConcreteKind.RetainingWall] = new SizeField[]
        {
            new("גובה", "גובה (מ׳)", s => s.HeightM, (s, v) => s with { HeightM = v }),
            new("עובי גזע", "עובי גזע (ס״מ)", s => s.ThicknessCm, (s, v) => s with { ThicknessCm = v }),
            new("רוחב רגל", "רוחב רגל (מ׳, 0 = בלי)", s => s.FootingWidthM, (s, v) => s with { FootingWidthM = v }),
            new("עובי רגל", "עובי רגל (ס״מ)", s => s.FootingDepthCm, (s, v) => s with { FootingDepthCm = v }),
            new("בטון רזה", "בטון רזה (ס״מ)", s => s.LeanCm, (s, v) => s with { LeanCm = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
        [ConcreteKind.Footing] = new SizeField[]
        {
            new("רוחב", "רוחב (מ׳, ליסוד בקו)", s => s.WidthM, (s, v) => s with { WidthM = v }),
            new("עובי", "עובי (ס״מ)", s => s.ThicknessCm, (s, v) => s with { ThicknessCm = v }),
            new("בטון רזה", "בטון רזה (ס״מ)", s => s.LeanCm, (s, v) => s with { LeanCm = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
        [ConcreteKind.Pile] = new SizeField[]
        {
            new("אורך", "אורך כלונס (מ׳)", s => s.LengthM, (s, v) => s with { LengthM = v }),
            new("קוטר", "קוטר (ס״מ)", s => s.DiameterCm, (s, v) => s with { DiameterCm = v }),
            new("מרווח", "מרווח (מ׳, לקו כלונסאות)", s => s.SpacingM, (s, v) => s with { SpacingM = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
        [ConcreteKind.HeadBeam] = new SizeField[]
        {
            new("רוחב", "רוחב (מ׳)", s => s.WidthM, (s, v) => s with { WidthM = v }),
            new("גובה", "גובה (מ׳)", s => s.HeightM, (s, v) => s with { HeightM = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
        [ConcreteKind.Slab] = new SizeField[]
        {
            new("עובי", "עובי (ס״מ)", s => s.ThicknessCm, (s, v) => s with { ThicknessCm = v }),
            new("בטון רזה", "בטון רזה (ס״מ)", s => s.LeanCm, (s, v) => s with { LeanCm = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
        [ConcreteKind.Column] = new SizeField[]
        {
            new("גובה", "גובה (מ׳)", s => s.HeightM, (s, v) => s with { HeightM = v }),
            new("קוטר", "קוטר (ס״מ)", s => s.DiameterCm, (s, v) => s with { DiameterCm = v }),
            new("זיון", "זיון (ק״ג למ״ק)", s => s.RebarKgPerM3, (s, v) => s with { RebarKgPerM3 = v }),
        },
    };

    private string _concretePath = "";
    private StructureConcrete.Settings _concreteSettings = new();
    private StructureConcrete.Plan _concretePlan = new(Array.Empty<StructureConcrete.Element>());
    private List<InfrastructureGroup> _concreteDerived = new();
    private readonly HashSet<InfrastructureGroup> _facesRead = new();
    private UIElement _concreteBody = null!;
    private readonly DataGrid _concreteGrid = Table(), _concreteRowsGrid = Table();
    private readonly ComboBox _concreteKind = new() { DisplayMemberPath = "Label", MinWidth = 170, Padding = new Thickness(6) };
    private readonly Grid _sizeEditor = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _concreteSubject = Text(""), _concreteSummary = Text(""), _concreteBannerText = Text("");
    private readonly CheckBox _concreteDefaults = new() { Content = "חשב לפי מידות טיפוסיות כשלא הוזנו מידות (מסומן \"ברירת מחדל\")", Margin = new Thickness(0, 6, 0, 4), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Border _concreteBanner = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8) };

    /// <summary>One settings file per drawing; a marked area uses the same sections (a wall is as high in part of the plan as in all of it).</summary>
    private void InitConcrete(string wholeReview)
    {
        _concretePath = Path.ChangeExtension(wholeReview, ".concrete.json");
        _concreteSettings = StructureConcrete.Load(_concretePath);
    }

    /// <summary>Re-derives the concrete rows from the active plan layers and the sections; keeps their price choices.</summary>
    private void RebuildConcrete()
    {
        var old = _concreteDerived.GroupBy(g => g.Layer).ToDictionary(g => g.Key, g => g.First());
        _groups.RemoveAll(g => g.Source == StructureConcrete.DerivedSource);
        // Walls, footings and beams drawn as two faces: read the pairs once per layer, so the axis is not counted twice.
        foreach (var g in Active.Where(g => !_facesRead.Contains(g) && g.Count > g.ClosedCount && g.LengthMetres > 0))
        {
            var kind = _concreteSettings.KindFor(g);
            if (kind is not (ConcreteKind.RetainingWall or ConcreteKind.HeadBeam or ConcreteKind.Footing)) continue;
            _facesRead.Add(g);
            g.Faces = _scan.Faces(g, kind == ConcreteKind.Footing ? 6 : 1.5);
        }
        _concretePlan = StructureConcrete.Build(Active, _concreteSettings);
        StructureConcrete.MarkPricedByConcrete(_groups, _concretePlan);
        _concreteDerived = StructureConcrete.DerivedGroups(_concretePlan).GroupBy(g => g.Layer).Select(g => g.First()).ToList();
        foreach (var d in _concreteDerived)
        {
            if (!old.TryGetValue(d.Layer, out var o)) continue;
            if (_excluded.Remove(o)) _excluded.Add(d);
            if (!o.Approved) continue;
            var book = _books.FirstOrDefault(b => b.Catalog.FileHash == o.CatalogHash);
            if (book != null && d.Quantity > 0 && book.Catalog.Items.TryGetValue(o.Code!, out var item) && item.Unit.SameUnit(Units.Parse(d.MeasurementUnit)))
                d.Assign(book.Catalog, o.Code!, o.AutomaticallyAssigned);
        }
        foreach (var gone in old.Values) _excluded.Remove(gone);
        _groups.AddRange(_concreteDerived);
        if (_view == View.Concrete) RefreshConcrete();
    }

    private UIElement ConcreteBody()
    {
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) });

        // Elements
        // One scrolling column: the table keeps its height and the section editor sits under it (live 2026-11-09: in a
        // dock layout the editor squeezed the table out of sight).
        var left = new StackPanel();
        var head = new StackPanel(); left.Children.Add(head);
        head.Children.Add(Heading("1  אלמנטי בטון בשרטוט"));
        head.Children.Add(Text("קירות, יסודות, כלונסאות, קורות ראש, מרצפים ועמודים — מזוהים משם השכבה (S-/ST-/HW-WALL, FOUND, PILE…) או מבלוק כלונס. " +
                               "שכבת קונסטרוקציה אחרת — בחר אותה ובחר סוג. הקו עצמו אינו מתומחר לפי אורכו: הבטון, הכלונסאות והזיון שלו הם השורות."));
        var editor = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        editor.Children.Add(Heading("2  מידות האלמנט", 14));
        _concreteSubject.FontWeight = FontWeights.SemiBold; editor.Children.Add(_concreteSubject);
        var kindRow = new DockPanel { Margin = new Thickness(0, 4, 0, 2) };
        var kindLabel = Text("סוג:"); kindLabel.Margin = new Thickness(0, 4, 8, 4); kindLabel.VerticalAlignment = VerticalAlignment.Center;
        kindRow.Children.Add(kindLabel); _concreteKind.ItemsSource = KindChoices; kindRow.Children.Add(_concreteKind);
        _concreteKind.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-kind");
        _concreteKind.SelectionChanged += (_, _) =>
        {
            if (_loading || _concreteGrid.SelectedItem is not ConcreteRow r || _concreteKind.SelectedItem is not KindChoice c || c.Kind == r.Kind) return;
            UiGuard.Run("סוג אלמנט בטון", () => { _concreteSettings.SetKind(r.Group, c.Kind); SaveConcrete($"{r.LayerName} סומן כ{c.Label}"); });
        };
        editor.Children.Add(kindRow);
        editor.Children.Add(Text("שדה ריק = המידה מהשרטוט או ערך טיפוסי (מוצג משמאל). Enter או יציאה מהשדה — שומר לשרטוט זה."));
        editor.Children.Add(_sizeEditor);
        var clear = ActionButton("נקה מידות שהוזנו", "QuietButton"); clear.HorizontalAlignment = HorizontalAlignment.Left; clear.Margin = new Thickness(0, 6, 0, 0);
        clear.Click += (_, _) => UiGuard.Run("ניקוי מידות", () =>
        {
            if (_concreteGrid.SelectedItem is not ConcreteRow r) return;
            _concreteSettings.SetSizes(r.Group, new ConcreteSizes());
            SaveConcrete($"{r.LayerName}: המידות שהוזנו נוקו");
        });
        editor.Children.Add(clear);
        DispositionChipColumn(_concreteGrid);
        Column(_concreteGrid, "אלמנט", "KindName", 100); Column(_concreteGrid, "שכבה", "LayerName", new DataGridLength(1, DataGridLengthUnitType.Star), ltr: true);
        Column(_concreteGrid, "כמות בתכנית", "Basis", 105); Column(_concreteGrid, "מקור", "Origin", 90);
        Column(_concreteGrid, "בטון מ״ק", "ConcreteM3", 75, "N1"); Column(_concreteGrid, "זיון טון", "RebarTon", 70, "N2");
        _concreteGrid.Columns[2].MinWidth = 150;
        _concreteGrid.RowHeight = 34; _concreteGrid.Height = 250; PassWheelToPage(_concreteGrid);
        _concreteGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-elements");
        _concreteGrid.SelectionChanged += (_, _) => UiGuard.Run("אלמנט בטון", LoadSizeEditor);
        left.Children.Add(_concreteGrid);
        left.Children.Add(editor);
        body.Children.Add(Theme.Card(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 14));

        // Banner, results
        var right = new StackPanel();
        _concreteBannerText.TextWrapping = TextWrapping.Wrap; _concreteBannerText.Foreground = Theme.Ink;
        _concreteBannerText.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-banner-text");
        _concreteBanner.Child = _concreteBannerText;
        right.Children.Add(_concreteBanner);
        _concreteDefaults.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-defaults");
        _concreteDefaults.Click += (_, _) => UiGuard.Run("ערכי ברירת מחדל לבטון", () =>
        {
            _concreteSettings.UseDefaults = _concreteDefaults.IsChecked == true;
            SaveConcrete(_concreteSettings.UseDefaults ? "בטון: חישוב לפי מידות טיפוסיות כשלא הוזנו מידות" : "בטון: רק מידות שהוזנו או מהשרטוט — השאר לא מתומחר");
        });
        right.Children.Add(_concreteDefaults);
        right.Children.Add(Heading("3  כמויות בטון — שורות בכתב הכמויות", 14));
        Column(_concreteRowsGrid, "שורה", "Layer", new DataGridLength(1, DataGridLengthUnitType.Star));
        Column(_concreteRowsGrid, "כמות", "Quantity", 90, "N2"); Column(_concreteRowsGrid, "יחידה", "MeasurementUnit", 55);
        Column(_concreteRowsGrid, "סעיף", "MappingDisplay", 95, ltr: true);
        _concreteRowsGrid.RowHeight = 34; PassWheelToPage(_concreteRowsGrid);
        _concreteRowsGrid.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-rows");
        right.Children.Add(_concreteRowsGrid);
        _concreteSummary.FontWeight = FontWeights.SemiBold;
        _concreteSummary.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-summary");
        right.Children.Add(_concreteSummary);
        var source = Text("ערכים טיפוסיים: " + StructureConcrete.DefaultsSource); source.FontSize = 11.5; source.Margin = new Thickness(0, 8, 0, 0);
        right.Children.Add(source);
        var rightCard = Theme.Card(new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, 14);
        rightCard.Margin = new Thickness(12, 0, 0, 0); Grid.SetColumn(rightCard, 1); body.Children.Add(rightCard);
        return body;
    }

    private void RefreshConcrete()
    {
        var selected = (_concreteGrid.SelectedItem as ConcreteRow)?.Group;
        var elements = _concretePlan.Elements.ToDictionary(e => e.Group);
        var rows = Active.Where(g => g.Disposition == InfrastructureDisposition.New && StructureConcrete.MaybeConcrete(g) || elements.ContainsKey(g))
            .Select(g => new ConcreteRow { Group = g, Element = elements.GetValueOrDefault(g) })
            .OrderBy(r => r.Kind == ConcreteKind.None).ThenBy(r => (int)r.Kind).ThenBy(r => r.LayerName, StringComparer.Ordinal).ToArray();
        _loading = true;
        try
        {
            _concreteGrid.ItemsSource = rows;
            _concreteGrid.SelectedItem = rows.FirstOrDefault(r => r.Group == selected) ?? rows.FirstOrDefault();
            _concreteDefaults.IsChecked = _concreteSettings.UseDefaults;
        }
        finally { _loading = false; }
        LoadSizeEditor();
        _concreteRowsGrid.ItemsSource = _concreteDerived.Where(g => !_excluded.Contains(g)).ToArray();
        var plan = _concretePlan;
        _concreteSummary.Text = plan.Elements.Count == 0
            ? "לא נמצאו אלמנטי בטון בשרטוט. שכבת קונסטרוקציה ברשימה — בחר אותה וסמן סוג."
            : $"{plan.Elements.Count} אלמנטים · בטון {plan.ConcreteM3:N1} מ״ק · זיון {plan.RebarTon:N2} טון · {_concreteDerived.Count} שורות בכתב הכמויות (תחת \"עבודות חדשות\")" +
              (plan.Defaults > 0 ? $" · ⚠ {plan.Defaults} לפי ברירת מחדל" : "") + (plan.Unpriced > 0 ? $" · {plan.Unpriced} לא תומחרו" : "");
        var warn = plan.Defaults > 0 || plan.Unpriced > 0;
        _concreteBannerText.Text = plan.Elements.Count == 0 ? "אין אלמנטי בטון מזוהים — כתב הכמויות אינו כולל בטון."
            : plan.Defaults > 0
                ? $"⚠ ערכי ברירת מחדל: {plan.Defaults} מתוך {plan.Elements.Count} אלמנטי הבטון חושבו בחלקם לפי מידות טיפוסיות ולא לפי תוכניות הקונסטרוקציה " +
                  $"({string.Join(" · ", plan.Elements.Where(e => !e.Unpriced && e.UsesDefaults).Select(e => $"{e.KindName} {e.LayerName}: {e.DefaultFields}"))}). " +
                  "הזן את המידות מהתכנון, או אמת מול הקונסטרוקטור לפני הגשה."
            : plan.Unpriced > 0 ? $"{plan.Unpriced} אלמנטי בטון לא תומחרו — חסרות מידות. הזן מידות, או הפעל חישוב לפי מידות טיפוסיות."
            : "כל אלמנטי הבטון חושבו לפי מידות שהוזנו או מהשרטוט.";
        _concreteBanner.Background = warn ? Theme.ReviewTint : Theme.AccentTint;
        _concreteBanner.BorderBrush = warn ? Theme.Review : Theme.Accent;
        UpdateSummary();
    }

    private void LoadSizeEditor()
    {
        _sizeEditor.Children.Clear(); _sizeEditor.RowDefinitions.Clear(); _sizeEditor.ColumnDefinitions.Clear();
        if (_concreteGrid.SelectedItem is not ConcreteRow r)
        {
            _concreteSubject.Text = "בחר אלמנט"; _concreteKind.IsEnabled = false; return;
        }
        _loading = true;
        try { _concreteKind.IsEnabled = true; _concreteKind.SelectedItem = KindChoices.First(c => c.Kind == r.Kind); }
        finally { _loading = false; }
        _concreteSubject.Text = Ltr(r.LayerName) + " · " + r.KindName + (r.Element?.Plan.Note is { Length: > 0 } note ? " · " + note : "");
        if (r.Element is not { } e || !SizeFields.TryGetValue(e.Kind, out var fields)) return;
        _sizeEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        _sizeEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        _sizeEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var typed = _concreteSettings.SizesFor(r.Group);
        var row = 0;
        void Add(SizeField f, string current)
        {
            _sizeEditor.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(f.Label); label.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(label, row); _sizeEditor.Children.Add(label);
            var box = Input(); box.Margin = new Thickness(0, 2, 8, 2); box.FlowDirection = System.Windows.FlowDirection.LeftToRight;
            box.Text = f.Get(typed)?.ToString("0.###", CultureInfo.InvariantCulture) ?? "";
            box.SetValue(System.Windows.Automation.AutomationProperties.AutomationIdProperty, "boq-concrete-size-" + row);
            box.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, f.Label);
            var group = r.Group; var field = f;
            void Commit()
            {
                var text = box.Text.Trim().Replace(',', '.');
                double? value = text.Length == 0 ? null
                    : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) && v >= 0 ? v
                    : throw new InvalidOperationException("יש להזין מספר אי־שלילי, או להשאיר ריק");
                var sizes = _concreteSettings.SizesFor(group);
                if (field.Get(sizes) == value) return;
                _concreteSettings.SetSizes(group, field.Set(sizes, value));
                SaveConcrete($"{group.ShortLayer}: {field.Label} = " + (value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "ריק (שרטוט / ברירת מחדל)"));
            }
            box.LostFocus += (_, _) => UiGuard.Run("מידת אלמנט בטון", Commit);
            box.KeyDown += (_, k) => { if (k.Key == Key.Enter) UiGuard.Run("מידת אלמנט בטון", Commit); };
            Grid.SetRow(box, row); Grid.SetColumn(box, 1); _sizeEditor.Children.Add(box);
            var now = Text(current); now.VerticalAlignment = VerticalAlignment.Center; Grid.SetRow(now, row); Grid.SetColumn(now, 2); _sizeEditor.Children.Add(now);
            row++;
        }
        Add(Basis, e.BasisDisplay);
        foreach (var f in fields)
        {
            var resolved = e.Fields.FirstOrDefault(x => x.Name == f.Name);
            if (resolved == null) continue;   // not used for this element (e.g. spacing when the piles are counted)
            Add(f, $"{resolved.Value.ToString("0.##", CultureInfo.InvariantCulture)} {resolved.Unit}" + (resolved.Origin.Length > 0 ? " · " + resolved.Origin : " · הוזן"));
        }
    }

    private void SaveConcrete(string message)
    {
        StructureConcrete.Save(_concretePath, _concreteSettings);
        RebuildConcrete(); Persist(); RefreshConcrete();
        _message.Text = message;
    }
}
