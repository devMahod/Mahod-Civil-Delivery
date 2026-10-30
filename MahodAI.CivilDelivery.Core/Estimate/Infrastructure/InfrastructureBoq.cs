namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public sealed record InfrastructureLine(string Source, string Handle, string Layer, string EntityType,
    double LengthMetres, bool Closed, string Topic, string Evidence, string OutputLayer)
{
    public double AreaSquareMetres { get; init; }
    public double VolumeCubicMetres { get; init; }
    public double CountUnits { get; init; }
    public string GroupPart { get; init; } = "";
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
    public bool Approved => Code != null && CatalogHash != null;
    public string ShortLayer => Layer.Split('|').Last() + (GroupPart.Length > 0 ? " · " + GroupPart : "");
    public string MappingDisplay => Code ?? "בחר סעיף";
    public decimal? Total => Approved && Price is { } p ? (decimal)Quantity * p : null;
    public string Status => Approved ? Price == null ? "שויך — חסר מחיר" : "שויך" : "טרם שויך";
    /// <summary>Selecting an item is the mapping decision. No name or unrelated form is required.</summary>
    public void Assign(CatalogSnapshot catalog, string code)
    {
        if (!double.IsFinite(Quantity) || Quantity <= 0 ||
            !catalog.Items.TryGetValue(code, out var item) || !item.Unit.SameUnit(Units.Parse(MeasurementUnit)))
            throw new InvalidOperationException("יש לבחור סעיף ביחידה התואמת לכמות. שטח, אורך, נפח וספירה אינם מתחלפים זה בזה");
        Code = item.Code; Description = item.Description;
        Price = catalog.Prices.TryGetValue(code, out var price) ? price.Price : null;
        CatalogHash = catalog.FileHash; ApprovedBy = null; ApprovedAtUtc = DateTime.UtcNow;
    }
    // Compatibility for callers with historical named decisions; never manufacture a person's identity.
    public void Approve(CatalogSnapshot catalog, string code, string approver)
    { Assign(catalog, code); ApprovedBy = string.IsNullOrWhiteSpace(approver) ? null : approver.Trim(); }
    public void Withdraw()
    { Code = Description = CatalogHash = ApprovedBy = null; Price = null; ApprovedAtUtc = null; }
}

public static class InfrastructureBoq
{
    public const string Unknown = "לא מסווג";
    public static readonly string[] Topics = { "מים", "ביוב", "ניקוז", "גז", "חשמל", "תאורה", "תקשורת", "רמזורים", "השקיה", "עבודות עפר", "בטון", "איטום", "פירוקים והריסות", "ריצופים", "גינון", "כבישים ואספלט", "אבני שפה", "קירות תומכים", "קירות ומבנים", "מסגרות וגדרות", "סימון ושילוט", Unknown };
    public static (string Topic, string Evidence, string Code) Classify(string layer, string source, string? description = null)
        => InfrastructureLayerNames.Classify(layer, source, description);
    public static IReadOnlyList<InfrastructureGroup> Group(IEnumerable<InfrastructureLine> lines) => lines
        .GroupBy(l=>(l.Source,l.Layer,l.Topic,l.GroupPart))
        .Select(g=>new InfrastructureGroup {Source=g.Key.Source,Layer=g.Key.Layer,Topic=g.Key.Topic,Count=g.Count(),
            LengthMetres=g.Sum(l=>l.LengthMetres),AreaSquareMetres=g.Sum(l=>l.AreaSquareMetres),VolumeCubicMetres=g.Sum(l=>l.VolumeCubicMetres),
            CountUnits=g.Sum(l=>l.CountUnits),GroupPart=g.Key.GroupPart,ClosedCount=g.Count(l=>l.Closed),Evidence=g.First().Evidence,
            MeasurementUnit=g.Any(l=>l.CountUnits>0)?"יח׳":g.Any(l=>l.VolumeCubicMetres>0)?"מ״ק":
                g.Any(l=>l.AreaSquareMetres>0) && (g.Key.GroupPart=="שטחים" || g.Key.Topic is "ריצופים" or "גינון" or "כבישים ואספלט" or "איטום")?"מ״ר":"מטר"})
        .OrderBy(g=>Array.IndexOf(Topics,g.Topic)).ThenByDescending(g=>g.LengthMetres).ToArray();

    public static void Export(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<InfrastructureLine> lines,
        CatalogSnapshot catalog, IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine)
        => Export(path, groups, lines, new[] { catalog }, findings, drawing, drawingHash, measurementEngine);
    public static void Export(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<InfrastructureLine> lines,
        IReadOnlyList<CatalogSnapshot> catalogs, IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine)
    {
        var books = catalogs.GroupBy(c => c.FileHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var used = groups.Where(g => g.Approved).Select(g => g.CatalogHash!).Distinct().ToArray();
        if (used.Any(h => !books.ContainsKey(h))) throw new InvalidOperationException("חסרה מהדורת מחירון לשורה משויכת — טען אותה או בחר סעיף מחדש");
        var usedBooks = used.Length > 0 ? used.Select(h => books[h]).ToArray() : catalogs.Take(1).ToArray();
        var w=new MiniXlsx.Workbook{SheetName="כתב כמויות",RightToLeft=true,FreezeTopRows=5,PrintTitleRows=5,PrintLandscapeFitToWidth=true,ShowGridLines=false};
        w.Styles.Add(new MiniXlsx.Style(Bold:true,FontSize:16,FillRgb:"DCECE7",WrapText:true));
        w.Styles.Add(new MiniXlsx.Style(Bold:true,FillRgb:"DCECE7",WrapText:true,ThinTopBottomBorder:true));
        w.Styles.Add(new MiniXlsx.Style(WrapText:true,AlignTop:true));
        w.Styles.Add(new MiniXlsx.Style(NumFmtId:MiniXlsx.NumFmtThousands2));
        w.Styles.Add(new MiniXlsx.Style(FillRgb:"FFF2CC",WrapText:true));
        foreach(var width in new[]{("A",6d),("B",14d),("C",18d),("D",48d),("E",9d),("F",16d),("G",16d),("H",18d),("I",24d),("J",34d),("K",36d),("L",38d)})
            w.ColumnWidths.Add((width.Item1[0]-'A'+1,width.Item2));
        w.Rows.Add(new MiniXlsx.OutRow(1){HeightPoints=28}.Text("A","כתב כמויות — טיוטה לבדיקה",1));
        w.SingleRowMerges.Add("A1:L1");
        w.Rows.Add(new MiniXlsx.OutRow(2){HeightPoints=42}.Text("A","שורות ללא שיוך נשארות ללא מחיר וסכום. בחירת סעיף אינה אישור המדידה או המפרט. אין להסיק קוטר, חומר, עומק או תכנון לביצוע משם שכבה בלבד.",5));
        w.SingleRowMerges.Add("A2:L2");
        w.Rows.Add(new MiniXlsx.OutRow(3).Text("A","מחירונים: "+string.Join(" | ",usedBooks.Select(c => c.SnapshotId)),3)); w.SingleRowMerges.Add("A3:L3");
        var headers=new[]{"מס׳","נושא","סעיף","תיאור הסעיף / המפרט","יחידה","כמות","מחיר יחידה ₪","סכום ₪","מצב","שכבת מקור","קובץ מקור","מחירון ומהדורה"};
        var header=new MiniXlsx.OutRow(5){HeightPoints=28};
        for(var i=0;i<headers.Length;i++) header.Text(((char)('A'+i)).ToString(),headers[i],2);
        w.Rows.Add(header);
        var n=6;
        foreach(var g in groups)
        {
            var row=new MiniXlsx.OutRow(n){HeightPoints=42};
            row.Number("A",(double)(n-5),4).Text("B",g.Topic,3).Text("C",g.Code??"",3)
                .Text("D",g.Description??(g.Specification.Length>0?g.Specification:"טרם נבחר סעיף"),3)
                .Text("E",g.MeasurementUnit,3).Number("F",g.Quantity,4).Text("I",g.Status+" · "+g.QuantityOrigin,5)
                .Text("J",g.Layer,3).Text("K",g.Source,3).Text("L",g.CatalogHash != null && books.TryGetValue(g.CatalogHash,out var sourceBook) ? sourceBook.SnapshotId : "",3);
            if(g.Approved && g.Price is { } price) row.Number("G",price,4).Formula("H",$"F{n}*G{n}",4);
            w.Rows.Add(row); n++;
        }
        w.Rows.Add(new MiniXlsx.OutRow(n+1).Text("D","סכום חלקי של שורות משויכות עם מחיר בלבד — ללא מע״מ",2)
            .Formula("H",n>6?$"SUM(H6:H{n-1})":"0",4));
        var trace=new MiniXlsx.Worksheet{SheetName="מדידות מקור",RightToLeft=true};
        foreach(var c in new[]{"A","B","C","D","E","F","G"})trace.ColumnWidths.Add((c[0]-'A'+1,c=="A"||c=="C"?38:22));
        trace.Rows.Add(new MiniXlsx.OutRow(1).Text("A","מקור",2).Text("B","Handle",2).Text("C","שכבה",2).Text("D","סוג עצם",2)
            .Text("E","אורך במטרים",2).Text("F","נושא מוצע",2).Text("G","סגור",2).Text("H","שטח במ״ר",2).Text("I","נפח במ״ק",2).Text("J","ספירת בלוקים",2).Text("K","קבוצה",2));
        for(var i=0;i<lines.Count;i++){var l=lines[i];trace.Rows.Add(new MiniXlsx.OutRow(i+2).Text("A",l.Source,3).Text("B",l.Handle,3)
            .Text("C",l.Layer,3).Text("D",l.EntityType,3).Number("E",l.LengthMetres,4).Text("F",l.Topic,3).Text("G",l.Closed?"כן — לבדיקה":"לא",3)
            .Number("H",l.AreaSquareMetres,4).Number("I",l.VolumeCubicMetres,4).Number("J",l.CountUnits,4).Text("K",l.GroupPart,3));}
        w.AdditionalSheets.Add(trace);
        var checks=new MiniXlsx.Worksheet{SheetName="בדיקות ומקור המחירון",RightToLeft=true}; checks.ColumnWidths.Add((1,30)); checks.ColumnWidths.Add((2,100));
        var notes=new List<(string,string)> {("שרטוט",drawing),("SHA-256 מקור",drawingHash),
            ("מנוע מדידה",measurementEngine),("תחום הסריקה","כל Model Space והמקורות הטעונים; מקרא הוחרג. לא הוחל תחום פוליגון במסלול זה."),
            ("אורך","אורך גאומטרי תלת־ממדי של הקווים, במטרים; לא כולל תוספות ביצוע, חפירה, אביזרים או שוחות"),
            ("ביקורת",$"{lines.Count} קווים, {groups.Count} קבוצות; מחיר מופיע רק לשורות שנבחר עבורן סעיף. שיוך אינו אישור מדידה או מפרט."),
            ("אזהרה","זו אינה הוכחה שכל קו הוא צינור לביצוע. נדרש לבדוק קווים מקבילים, כפילויות, מצב קיים, מידות ומפרט.")};
        notes.AddRange(findings.Select(f=>("ממצא",f)));
        foreach (var book in usedBooks) { notes.Add(("מחירון ומהדורה",book.SnapshotId+" | "+book.PublicationNote)); notes.Add(("SHA-256 מחירון",book.FileHash)); }
        for(var i=0;i<notes.Count;i++)checks.Rows.Add(new MiniXlsx.OutRow(i+1){HeightPoints=38}.Text("A",notes[i].Item1,2).Text("B",notes[i].Item2,3));
        w.AdditionalSheets.Add(checks);
        MiniXlsx.Write(w,path);
    }
}
