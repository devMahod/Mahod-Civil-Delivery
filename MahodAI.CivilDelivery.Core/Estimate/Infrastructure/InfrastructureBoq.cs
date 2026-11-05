namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public sealed record InfrastructureLine(string Source, string Handle, string Layer, string EntityType,
    double LengthMetres, bool Closed, string Topic, string Evidence, string OutputLayer)
{
    public double AreaSquareMetres { get; init; }
    public double VolumeCubicMetres { get; init; }
    public double CountUnits { get; init; }
    public string GroupPart { get; init; } = "";
    /// <summary>Detected from the layer/source name (ASMADE, -EX, KAYAM, CNCL…); the engineer may change it per group.</summary>
    public InfrastructureDisposition Disposition { get; init; } = InfrastructureDisposition.New;
    public string ConditionEvidence { get; init; } = "";
    public string Element { get; init; } = "";
    public RoadSurfaceKind Surface { get; init; } = RoadSurfaceKind.None;
    public LayerRole Role { get; init; } = LayerRole.Quantity;
}

public sealed class InfrastructureGroup
{
    public required string Source { get; init; }
    public required string Layer { get; init; }
    private string _topic = InfrastructureBoq.Unknown, _workStatus = "טרם נבדק", _specification = "";
    public required string Topic { get => _topic; set { if (_topic != value) Withdraw(); _topic = value; } }
    public required int Count { get; init; }
    public required double LengthMetres { get; init; }
    public double AreaSquareMetres { get; init; }
    public double VolumeCubicMetres { get; init; }
    public double CountUnits { get; init; }
    public string GroupPart { get; init; } = "";
    private string _measurementUnit = "מטר";
    private double? _manualQuantity;
    public string MeasurementUnit { get => _measurementUnit; set { if (!Units.Parse(value).SameUnit(Units.Parse(_measurementUnit))) { Withdraw(); _manualQuantity = null; } _measurementUnit = value; } }
    public double? ManualQuantity { get => _manualQuantity; set { if (value is { } n && (!double.IsFinite(n) || n <= 0)) throw new ArgumentOutOfRangeException(nameof(value), "יש להזין כמות חיובית"); if (_manualQuantity != value) Withdraw(); _manualQuantity = value; } }
    public double MeasuredQuantity => Units.Parse(MeasurementUnit).Canonical switch
    { "m" => LengthMetres, "m2" => AreaSquareMetres, "dunam" => AreaSquareMetres / 1000d, "m3" => VolumeCubicMetres, "unit" => CountUnits, _ => 0 };
    public double Quantity => ManualQuantity ?? MeasuredQuantity;
    public string QuantityOrigin => ManualQuantity != null ? "כמות שהוזנה ידנית" : MeasuredQuantity > 0 ? "מדידה מהשרטוט" : "לא נמדדה כמות ביחידה זו";
    public required string Evidence { get; init; }
    public int ClosedCount { get; init; }
    public string WorkStatus { get => _workStatus; set { if (_workStatus != value) Withdraw(); _workStatus = value; } }
    public string Specification { get => _specification; set { if (_specification != value) Withdraw(); _specification = value; } }
    public string? Code { get; private set; }
    public string? Description { get; private set; }
    public decimal? Price { get; private set; }
    public string? CatalogHash { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public bool AutomaticallyAssigned { get; private set; }
    internal long AssignmentRevision { get; private set; }
    public bool Approved => Code != null && CatalogHash != null;
    public InfrastructureDisposition DetectedDisposition { get; init; } = InfrastructureDisposition.New;
    private InfrastructureDisposition? _disposition;
    /// <summary>New work, existing that stays (never priced), or existing for demolition (priced on its own sheet).</summary>
    public InfrastructureDisposition Disposition
    {
        get => _disposition ?? DetectedDisposition;
        set { if (Disposition != value) Withdraw(); _disposition = value == DetectedDisposition ? null : value; }
    }
    public bool DispositionChangedByEngineer => _disposition != null;
    public string DispositionLabel => InfrastructureCondition.Label(Disposition);
    public string DispositionShort => Disposition switch { InfrastructureDisposition.ExistingKeep => "נשאר", InfrastructureDisposition.ExistingDemolish => "לפירוק", _ => "חדש" };
    public string ConditionEvidence { get; init; } = "";
    public string Element { get; init; } = "";
    /// <summary>Which road surface this area belongs to (roads screen); the engineer may change or clear it.</summary>
    public RoadSurfaceKind Surface { get; set; } = RoadSurfaceKind.None;
    public RoadSurfaceKind DetectedSurface { get; init; } = RoadSurfaceKind.None;
    public string SurfaceName => FirmLayerConventions.SurfaceName(Surface);
    public LayerRole Role { get; init; } = LayerRole.Quantity;
    public bool IsDrafting => Role == LayerRole.Annotation;
    public string ElementDisplay => Element.Length > 0 ? Element : Topic;
    /// <summary>The source layer itself, without the XREF namespace or the measurement part.</summary>
    public string LayerName => Layer.Split('|').Last();
    public string ShortLayer => Layer.Split('|').Last() + (GroupPart.Length > 0 ? " · " + GroupPart : "");
    public string MappingDisplay => Code ?? (Disposition == InfrastructureDisposition.ExistingKeep ? "לא מתומחר" : Role == LayerRole.Quantity ? "בחר סעיף" : "—");
    public decimal? Total => Approved && Price is { } p ? (decimal)Quantity * p : null;
    public string Status => Approved ? (AutomaticallyAssigned ? "שויך אוטומטית" : "שויך") + (Price == null ? " — חסר מחיר" : "") : "טרם שויך";
    /// <summary>Selecting an item is the mapping decision. No name or unrelated form is required.</summary>
    public void Assign(CatalogSnapshot catalog, string code, bool automatic = false)
    {
        if (!double.IsFinite(Quantity) || Quantity <= 0 ||
            !catalog.Items.TryGetValue(code, out var item) || !item.Unit.SameUnit(Units.Parse(MeasurementUnit)))
            throw new InvalidOperationException("יש לבחור סעיף ביחידה התואמת לכמות. שטח, אורך, נפח וספירה אינם מתחלפים זה בזה");
        Code = item.Code; Description = item.Description;
        Price = catalog.Prices.TryGetValue(code, out var price) ? price.Price : null;
        CatalogHash = catalog.FileHash; ApprovedBy = null; ApprovedAtUtc = DateTime.UtcNow; AutomaticallyAssigned = automatic; AssignmentRevision++;
    }
    // Compatibility for callers with historical named decisions; never manufacture a person's identity.
    public void Approve(CatalogSnapshot catalog, string code, string approver)
    { Assign(catalog, code); ApprovedBy = string.IsNullOrWhiteSpace(approver) ? null : approver.Trim(); }
    public void Withdraw()
    { Code = Description = CatalogHash = ApprovedBy = null; Price = null; ApprovedAtUtc = null; AutomaticallyAssigned = false; AssignmentRevision++; }
}

public static class InfrastructureBoq
{
    public const string Unknown = "לא מסווג";
    public static readonly string[] Topics = { "מים", "ביוב", "ניקוז", "גז", "חשמל", "תאורה", "תקשורת", "רמזורים", "השקיה", "עבודות עפר", "בטון", "איטום", "פירוקים והריסות", "ריצופים", "גינון", "כבישים ואספלט", "אבני שפה", "קירות תומכים", "קירות ומבנים", "מסגרות וגדרות", "סימון ושילוט", Unknown };
    /// <summary>The firm's layer convention (HW-ROAD-HA, UT-W-…, DR-MNHL…) first; otherwise the alias vocabulary.</summary>
    public static (string Topic, string Evidence, string Code) Classify(string layer, string source, string? description = null)
    {
        var firm = FirmLayerConventions.Match(layer);
        if (firm is { Role: not LayerRole.Annotation } && firm.Topic.Length > 0)
            return (firm.Topic, $"מוסכמת שכבות: {FirmLayerConventions.Canonical(layer)} ← {firm.Element}", InfrastructureLayerNames.CodeFor(firm.Topic) ?? "UNCLASSIFIED");
        return InfrastructureLayerNames.Classify(layer, source, description);
    }

    /// <summary>Disposition, element, road surface and role of a source layer — the parts that are not the topic.</summary>
    public static InfrastructureLine Describe(InfrastructureLine line)
    {
        var firm = FirmLayerConventions.Match(line.Layer);
        var condition = InfrastructureCondition.Detect(line.Layer, line.Source);
        // A layer that names demolition as its subject (PIRUK…) is demolition of something existing.
        if (condition.Disposition == InfrastructureDisposition.New && line.Topic == "פירוקים והריסות")
            condition = (InfrastructureDisposition.ExistingDemolish, "נושא השכבה: פירוקים והריסות");
        // A counted element (manholes, inlets, signs) drawn as a closed outline is one unit per outline.
        var counted = firm is { Role: LayerRole.Quantity, Unit: "יח׳" } && line.Closed && line.CountUnits == 0 && line.VolumeCubicMetres == 0;
        return line with { Disposition = condition.Disposition, ConditionEvidence = condition.Evidence,
            CountUnits = counted ? 1 : line.CountUnits,
            Element = firm?.Element ?? "", Surface = firm?.Surface ?? RoadSurfaceKind.None,
            Role = firm?.Role ?? (InfrastructureLayerNames.IsAuxiliaryLayer(line.Layer) ? LayerRole.Annotation : LayerRole.Quantity) };
    }
    public static IReadOnlyList<InfrastructureGroup> Group(IEnumerable<InfrastructureLine> lines) => lines
        .GroupBy(l=>(l.Source,l.Layer,l.Topic,l.GroupPart))
        .Select(g=>new InfrastructureGroup {Source=g.Key.Source,Layer=g.Key.Layer,Topic=g.Key.Topic,Count=g.Count(),
            LengthMetres=g.Sum(l=>l.LengthMetres),AreaSquareMetres=g.Sum(l=>l.AreaSquareMetres),VolumeCubicMetres=g.Sum(l=>l.VolumeCubicMetres),
            CountUnits=g.Sum(l=>l.CountUnits),GroupPart=g.Key.GroupPart,ClosedCount=g.Count(l=>l.Closed),Evidence=g.First().Evidence,
            DetectedDisposition=g.First().Disposition,ConditionEvidence=g.First().ConditionEvidence,Element=g.First().Element,
            Surface=g.First().Surface,DetectedSurface=g.First().Surface,Role=g.First().Role,
            MeasurementUnit=g.Any(l=>l.CountUnits>0)?"יח׳":g.Any(l=>l.VolumeCubicMetres>0)?"מ״ק":
                g.Any(l=>l.AreaSquareMetres>0) && (g.Key.GroupPart=="שטחים" || g.Key.Topic is "ריצופים" or "גינון" or "כבישים ואספלט" or "איטום" || g.First().Surface != RoadSurfaceKind.None)?"מ״ר":"מטר"})
        .OrderBy(g=>Array.IndexOf(Topics,g.Topic)).ThenByDescending(g=>g.LengthMetres).ToArray();

    /// <summary>Apply reviewed categories consistently to the workbook, DWG and source receipt.</summary>
    public static InfrastructureLine ReviewedLine(InfrastructureLine line, InfrastructureGroup group)
    {
        if (line.Source != group.Source || line.Layer != group.Layer)
            throw new ArgumentException("The review belongs to a different source layer");
        var code = InfrastructureLayerNames.CodeFor(group.Topic) ?? "UNCLASSIFIED";
        var suffix = line.OutputLayer.IndexOf("__", StringComparison.Ordinal);
        return line with { Topic = group.Topic, OutputLayer = "BOQ_" + code +
            (suffix >= 0 ? line.OutputLayer[suffix..] : "__" + line.OutputLayer) };
    }

    public const string NewWorkSheet = "כתב כמויות";
    public const string DemolitionSheet = "פירוקים והריסות";
    public const string ExistingSheet = "קיים — נשאר";
    public const string RoadsSheet = "כבישים — שטח ונפח";

    public static void Export(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<InfrastructureLine> lines,
        CatalogSnapshot catalog, IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine)
        => Export(path, groups, lines, new[] { catalog }, findings, drawing, drawingHash, measurementEngine);

    /// <summary>
    /// New work on the first sheet; demolition of existing elements on its own priced sheet; existing that stays
    /// listed without prices; the road area/volume calculation; then the source measurements and provenance.
    /// </summary>
    public static void Export(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<InfrastructureLine> lines,
        IReadOnlyList<CatalogSnapshot> catalogs, IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine,
        RoadPavement.Report? roads = null)
    {
        var (books, usedBooks) = Books(groups, catalogs);
        var fresh = groups.Where(g => g.Disposition == InfrastructureDisposition.New).ToArray();
        var demolition = groups.Where(g => g.Disposition == InfrastructureDisposition.ExistingDemolish).ToArray();
        var keep = groups.Where(g => g.Disposition == InfrastructureDisposition.ExistingKeep).ToArray();
        var w = NewWorkbook(NewWorkSheet);
        var freshTotal = PricedSheet(w, "כתב כמויות — עבודות חדשות — טיוטה לבדיקה", fresh, books, usedBooks);
        if (demolition.Length > 0)
        {
            var d = Sheet(DemolitionSheet);
            var demolitionTotal = PricedSheet(d, "כתב כמויות פירוקים והריסות — תשתיות קיימות לפירוק", demolition, books, usedBooks);
            w.AdditionalSheets.Add(d);
            w.Rows.Add(new MiniXlsx.OutRow(freshTotal + 1).Text("D", "סכום פירוקים והריסות — מגיליון \"" + DemolitionSheet + "\"", 2)
                .Formula("H", $"'{DemolitionSheet}'!H{demolitionTotal}", 4));
            w.Rows.Add(new MiniXlsx.OutRow(freshTotal + 2).Text("D", "סה״כ עבודות חדשות ופירוקים — ללא מע״מ", 2)
                .Formula("H", $"H{freshTotal}+H{freshTotal + 1}", 4));
        }
        if (keep.Length > 0) w.AdditionalSheets.Add(ExistingKeepSheet(keep));
        if (roads != null && (roads.Surfaces.Count > 0 || roads.Corridor.Count > 0)) w.AdditionalSheets.Add(RoadSheet(roads));
        w.AdditionalSheets.Add(TraceSheet(lines));
        w.AdditionalSheets.Add(ChecksSheet(lines.Count, groups.Count, findings, drawing, drawingHash, measurementEngine, usedBooks,
            $"{fresh.Length} שורות עבודה חדשה · {demolition.Length} לפירוק · {keep.Length} קיים שנשאר (ללא מחיר)"));
        MiniXlsx.Write(w, path);
    }

    /// <summary>The demolition quote on its own: a workbook that holds only the existing elements marked for demolition.</summary>
    public static void ExportDemolition(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<CatalogSnapshot> catalogs,
        IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine)
    {
        var demolition = groups.Where(g => g.Disposition == InfrastructureDisposition.ExistingDemolish).ToArray();
        if (demolition.Length == 0) throw new InvalidOperationException("אין שורות שסומנו \"קיים — לפירוק\"");
        var (books, usedBooks) = Books(demolition, catalogs);
        var w = NewWorkbook(DemolitionSheet);
        PricedSheet(w, "כתב כמויות פירוקים והריסות — תשתיות קיימות לפירוק", demolition, books, usedBooks);
        w.AdditionalSheets.Add(ChecksSheet(0, demolition.Length, findings, drawing, drawingHash, measurementEngine, usedBooks,
            $"{demolition.Length} שורות לפירוק. המקור: שכבות ASMADE / מצב קיים שסומנו לפירוק."));
        MiniXlsx.Write(w, path);
    }

    private static (Dictionary<string, CatalogSnapshot> Books, CatalogSnapshot[] Used) Books(IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<CatalogSnapshot> catalogs)
    {
        var books = catalogs.GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var used = groups.Where(g => g.Approved).Select(g => g.CatalogHash!).Distinct().ToArray();
        if (used.Any(h => !books.ContainsKey(h))) throw new InvalidOperationException("חסרה מהדורת מחירון לשורה משויכת — טען אותה או בחר סעיף מחדש");
        return (books, used.Length > 0 ? used.Select(h => books[h]).ToArray() : catalogs.Take(1).ToArray());
    }

    private static MiniXlsx.Workbook NewWorkbook(string name)
    {
        var w = new MiniXlsx.Workbook { SheetName = name, RightToLeft = true, FreezeTopRows = 5, PrintTitleRows = 5, PrintLandscapeFitToWidth = true, ShowGridLines = false };
        w.Styles.Add(new MiniXlsx.Style(Bold: true, FontSize: 16, FillRgb: "DCECE7", WrapText: true));   // 1 title
        w.Styles.Add(new MiniXlsx.Style(Bold: true, FillRgb: "DCECE7", WrapText: true, ThinTopBottomBorder: true)); // 2 header
        w.Styles.Add(new MiniXlsx.Style(WrapText: true, AlignTop: true));                                    // 3 text
        w.Styles.Add(new MiniXlsx.Style(NumFmtId: MiniXlsx.NumFmtThousands2));                               // 4 number
        w.Styles.Add(new MiniXlsx.Style(FillRgb: "FFF2CC", WrapText: true));                                 // 5 note
        return w;
    }
    private static MiniXlsx.Worksheet Sheet(string name) => new()
    { SheetName = name, RightToLeft = true, FreezeTopRows = 5, PrintTitleRows = 5, PrintLandscapeFitToWidth = true, ShowGridLines = false };

    /// <summary>A priced BOQ sheet; returns the row of its subtotal.</summary>
    private static int PricedSheet(MiniXlsx.Worksheet w, string title, IReadOnlyList<InfrastructureGroup> groups,
        IReadOnlyDictionary<string, CatalogSnapshot> books, IReadOnlyList<CatalogSnapshot> usedBooks)
    {
        foreach (var width in new[] { ("A", 6d), ("B", 14d), ("C", 18d), ("D", 48d), ("E", 9d), ("F", 16d), ("G", 16d), ("H", 18d), ("I", 24d), ("J", 34d), ("K", 36d), ("L", 38d) })
            w.ColumnWidths.Add((width.Item1[0] - 'A' + 1, width.Item2));
        w.Rows.Add(new MiniXlsx.OutRow(1) { HeightPoints = 28 }.Text("A", title, 1));
        w.SingleRowMerges.Add("A1:L1");
        w.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 42 }.Text("A", "שורות ללא שיוך נשארות ללא מחיר וסכום. בחירת סעיף אינה אישור המדידה או המפרט. אין להסיק קוטר, חומר, עומק או תכנון לביצוע משם שכבה בלבד.", 5));
        w.SingleRowMerges.Add("A2:L2");
        w.Rows.Add(new MiniXlsx.OutRow(3).Text("A", "מחירונים: " + string.Join(" | ", usedBooks.Select(c => c.SnapshotId)), 3)); w.SingleRowMerges.Add("A3:L3");
        var headers = new[] { "מס׳", "נושא", "סעיף", "תיאור הסעיף / המפרט", "יחידה", "כמות", "מחיר יחידה ₪", "סכום ₪", "מצב", "שכבת מקור", "קובץ מקור", "מחירון ומהדורה" };
        var header = new MiniXlsx.OutRow(5) { HeightPoints = 28 };
        for (var i = 0; i < headers.Length; i++) header.Text(((char)('A' + i)).ToString(), headers[i], 2);
        w.Rows.Add(header);
        var n = 6;
        foreach (var g in groups)
        {
            var row = new MiniXlsx.OutRow(n) { HeightPoints = 42 };
            row.Number("A", (double)(n - 5), 4).Text("B", g.Topic, 3).Text("C", g.Code ?? "", 3)
                .Text("D", g.Description ?? (g.Specification.Length > 0 ? g.Specification : "טרם נבחר סעיף"), 3)
                .Text("E", g.MeasurementUnit, 3).Number("F", g.Quantity, 4).Text("I", g.Status + " · " + g.QuantityOrigin, 5)
                .Text("J", g.Layer, 3).Text("K", g.Source, 3).Text("L", g.CatalogHash != null && books.TryGetValue(g.CatalogHash, out var sourceBook) ? sourceBook.SnapshotId : "", 3);
            if (g.Approved && g.Price is { } price) row.Number("G", price, 4).Formula("H", $"F{n}*G{n}", 4);
            w.Rows.Add(row); n++;
        }
        w.Rows.Add(new MiniXlsx.OutRow(n + 1).Text("D", "סכום חלקי של שורות משויכות עם מחיר בלבד — ללא מע״מ", 2)
            .Formula("H", n > 6 ? $"SUM(H6:H{n - 1})" : "0", 4));
        return n + 1;
    }

    private static MiniXlsx.Worksheet ExistingKeepSheet(IReadOnlyList<InfrastructureGroup> keep)
    {
        var s = Sheet(ExistingSheet);
        foreach (var (c, width) in new[] { ("A", 6d), ("B", 16d), ("C", 26d), ("D", 9d), ("E", 16d), ("F", 34d), ("G", 36d), ("H", 44d) })
            s.ColumnWidths.Add((c[0] - 'A' + 1, width));
        s.Rows.Add(new MiniXlsx.OutRow(1) { HeightPoints = 28 }.Text("A", "תשתיות קיימות שנשארות — לידיעה, ללא תמחור", 1)); s.SingleRowMerges.Add("A1:H1");
        s.Rows.Add(new MiniXlsx.OutRow(2) { HeightPoints = 36 }.Text("A", "שכבות ASMADE / מצב קיים שלא סומנו לפירוק. אם אלמנט נהרס, העתק או מוגן — סמן אותו בכלי והוא יעבור לגיליון המתאים.", 5)); s.SingleRowMerges.Add("A2:H2");
        var header = new MiniXlsx.OutRow(5) { HeightPoints = 28 };
        var titles = new[] { "מס׳", "נושא", "אלמנט", "יחידה", "כמות", "שכבת מקור", "קובץ מקור", "למה זוהה כקיים" };
        for (var i = 0; i < titles.Length; i++) header.Text(((char)('A' + i)).ToString(), titles[i], 2);
        s.Rows.Add(header);
        for (var i = 0; i < keep.Count; i++)
        {
            var g = keep[i];
            s.Rows.Add(new MiniXlsx.OutRow(i + 6) { HeightPoints = 32 }.Number("A", (double)(i + 1), 4).Text("B", g.Topic, 3).Text("C", g.ElementDisplay, 3)
                .Text("D", g.MeasurementUnit, 3).Number("E", g.Quantity, 4).Text("F", g.Layer, 3).Text("G", g.Source, 3).Text("H", g.ConditionEvidence, 3));
        }
        return s;
    }

    private static MiniXlsx.Worksheet RoadSheet(RoadPavement.Report roads)
    {
        var s = Sheet(RoadsSheet); s.FreezeTopRows = 0; s.PrintTitleRows = 0;
        foreach (var (c, width) in new[] { ("A", 22d), ("B", 30d), ("C", 14d), ("D", 18d), ("E", 18d), ("F", 12d), ("G", 18d), ("H", 50d) })
            s.ColumnWidths.Add((c[0] - 'A' + 1, width));
        var r = 1;
        s.Rows.Add(new MiniXlsx.OutRow(r++) { HeightPoints = 28 }.Text("A", "כבישים — שטח ונפח", 1)); s.SingleRowMerges.Add("A1:H1");
        s.Rows.Add(new MiniXlsx.OutRow(r++) { HeightPoints = 36 }.Text("A", "שטח = Hatch וגבולות סגורים בשכבות המשטח. נפח = שטח × עובי השכבה. מבנה המיסעה הוא ברירת מחדל לעריכה עד לקבלת שיטת התמחור של המשרד — אינו תכנון מאושר.", 5)); s.SingleRowMerges.Add("A2:H2");
        r++;
        s.Rows.Add(new MiniXlsx.OutRow(r++).Text("A", "1. שטחים לפי סוג משטח", 2));
        var h1 = new MiniXlsx.OutRow(r++);
        foreach (var (c, t) in new[] { ("A", "סוג משטח"), ("B", "מצב"), ("C", "שטח מ״ר"), ("D", "שכבות מקור") }) h1.Text(c, t, 2);
        s.Rows.Add(h1); s.SingleRowMerges.Add($"D{r - 1}:H{r - 1}");
        foreach (var t in roads.Surfaces)
        { s.Rows.Add(new MiniXlsx.OutRow(r).Text("A", t.SurfaceName, 3).Text("B", t.DispositionLabel, 3).Number("C", t.AreaSquareMetres, 4).Text("D", t.LayersDisplay, 3)); s.SingleRowMerges.Add($"D{r}:H{r}"); r++; }
        r++;
        if (roads.Layers.Count > 0)
        {
            s.Rows.Add(new MiniXlsx.OutRow(r++).Text("A", "2. מבנה מיסעה — עבודה חדשה", 2));
            var h2 = new MiniXlsx.OutRow(r++);
            foreach (var (c, t) in new[] { ("A", "סוג משטח"), ("B", "שכבה"), ("C", "עובי ס״מ"), ("D", "שטח מ״ר"), ("E", "נפח מ״ק"), ("F", "יחידת תשלום"), ("G", "כמות לתשלום") }) h2.Text(c, t, 2);
            s.Rows.Add(h2);
            foreach (var q in roads.Layers)
            {
                s.Rows.Add(new MiniXlsx.OutRow(r).Text("A", q.SurfaceName, 3).Text("B", q.Layer.Name, 3).Number("C", q.Layer.ThicknessCm, 4)
                    .Number("D", q.AreaSquareMetres, 4).Formula("E", $"D{r}*C{r}/100", 4).Text("F", q.Unit, 3)
                    .Formula("G", q.Layer.ByVolume ? $"E{r}" : $"D{r}", 4));
                r++;
            }
            r++;
        }
        if (roads.Corridor.Count > 0)
        {
            s.Rows.Add(new MiniXlsx.OutRow(r++).Text("A", "3. נפחים מקורידורים (Civil 3D)", 2));
            var h3 = new MiniXlsx.OutRow(r++);
            foreach (var (c, t) in new[] { ("A", "קוד / חומר"), ("B", "מקור"), ("C", "נפח מ״ק"), ("D", "שיטה") }) h3.Text(c, t, 2);
            s.Rows.Add(h3);
            foreach (var g in roads.Corridor)
            { s.Rows.Add(new MiniXlsx.OutRow(r).Text("A", g.ShortLayer, 3).Text("B", g.Source, 3).Number("C", g.VolumeCubicMetres, 4).Text("D", g.Evidence, 3)); r++; }
        }
        return s;
    }

    private static MiniXlsx.Worksheet TraceSheet(IReadOnlyList<InfrastructureLine> lines)
    {
        var trace = new MiniXlsx.Worksheet { SheetName = "מדידות מקור", RightToLeft = true };
        foreach (var c in new[] { "A", "B", "C", "D", "E", "F", "G" }) trace.ColumnWidths.Add((c[0] - 'A' + 1, c == "A" || c == "C" ? 38 : 22));
        trace.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "מקור", 2).Text("B", "Handle", 2).Text("C", "שכבה", 2).Text("D", "סוג עצם", 2)
            .Text("E", "אורך במטרים", 2).Text("F", "נושא מוצע", 2).Text("G", "סגור", 2).Text("H", "שטח במ״ר", 2).Text("I", "נפח במ״ק", 2).Text("J", "ספירת בלוקים", 2).Text("K", "קבוצה", 2)
            .Text("L", "מצב", 2).Text("M", "סיבת המצב", 2));
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            trace.Rows.Add(new MiniXlsx.OutRow(i + 2).Text("A", l.Source, 3).Text("B", l.Handle, 3)
                .Text("C", l.Layer, 3).Text("D", l.EntityType, 3).Number("E", l.LengthMetres, 4).Text("F", l.Topic, 3).Text("G", l.Closed ? "כן — לבדיקה" : "לא", 3)
                .Number("H", l.AreaSquareMetres, 4).Number("I", l.VolumeCubicMetres, 4).Number("J", l.CountUnits, 4).Text("K", l.GroupPart, 3)
                .Text("L", InfrastructureCondition.Label(l.Disposition), 3).Text("M", l.ConditionEvidence, 3));
        }
        return trace;
    }

    private static MiniXlsx.Worksheet ChecksSheet(int lineCount, int groupCount, IReadOnlyList<string> findings, string drawing, string drawingHash,
        string measurementEngine, IReadOnlyList<CatalogSnapshot> usedBooks, string split)
    {
        var checks = new MiniXlsx.Worksheet { SheetName = "בדיקות ומקור המחירון", RightToLeft = true }; checks.ColumnWidths.Add((1, 30)); checks.ColumnWidths.Add((2, 100));
        var notes = new List<(string, string)> {("שרטוט",drawing),("SHA-256 מקור",drawingHash),
            ("מנוע מדידה",measurementEngine),("תחום הסריקה","כל Model Space והמקורות הטעונים; מקרא הוחרג. לא הוחל תחום פוליגון במסלול זה."),
            ("אורך","אורך גאומטרי תלת־ממדי של הקווים, במטרים; לא כולל תוספות ביצוע, חפירה, אביזרים או שוחות"),
            ("ביקורת",$"{lineCount} קווים, {groupCount} קבוצות; מחיר מופיע רק לשורות שנבחר עבורן סעיף. שיוך אינו אישור מדידה או מפרט."),
            ("חלוקה", split),
            ("מצב קיים","ASMADE / AS-MADE, ‎-EX, EXST, KAYAM וקבצי מדידה (SR-) מזוהים כקיים ונשאר כברירת מחדל; CNCL / BITUL / PIRUK / DEMO — קיים לפירוק. ההחלטה הסופית של המהנדס, לכל שכבה."),
            ("אזהרה","זו אינה הוכחה שכל קו הוא צינור לביצוע. נדרש לבדוק קווים מקבילים, כפילויות, מצב קיים, מידות ומפרט.")};
        notes.AddRange(findings.Select(f => ("ממצא", f)));
        foreach (var book in usedBooks) { notes.Add(("מחירון ומהדורה", book.SnapshotId + " | " + book.PublicationNote)); notes.Add(("SHA-256 מחירון", book.FileHash)); }
        for (var i = 0; i < notes.Count; i++) checks.Rows.Add(new MiniXlsx.OutRow(i + 1) { HeightPoints = 38 }.Text("A", notes[i].Item1, 2).Text("B", notes[i].Item2, 3));
        return checks;
    }
}
