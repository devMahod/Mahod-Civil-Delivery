using System.Text.RegularExpressions;

namespace MahodAI.CivilDelivery.Estimate.Infrastructure;

public sealed record InfrastructureLine(string Source, string Handle, string Layer, string EntityType,
    double LengthMetres, bool Closed, string Topic, string Evidence, string OutputLayer);

public sealed class InfrastructureGroup
{
    public required string Source { get; init; }
    public required string Layer { get; init; }
    private string _topic = InfrastructureBoq.Unknown, _workStatus = "טרם נבדק", _specification = "";
    public required string Topic { get => _topic; set { if (_topic != value) Withdraw(); _topic = value; } }
    public required int Count { get; init; }
    public required double LengthMetres { get; init; }
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
    public bool Approved => Code != null && ApprovedBy != null;
    public decimal? Total => Approved && Price is { } p ? (decimal)LengthMetres * p : null;
    public string Status => Approved ? Price == null ? "שויך — חסר מחיר" : "שויך ואושר" : "נדרשת בדיקת מפרט וסעיף";
    public void Approve(CatalogSnapshot catalog, string code, string approver)
    {
        if (!InfrastructureBoq.Topics.Contains(Topic) || Topic == InfrastructureBoq.Unknown || WorkStatus != "תכנון לביצוע" ||
            string.IsNullOrWhiteSpace(Specification) || string.IsNullOrWhiteSpace(approver))
            throw new InvalidOperationException("יש לאשר סוג תשתית, מצב תכנון לביצוע, מפרט ושם מאשר לפני שיוך");
        if (!double.IsFinite(LengthMetres) || LengthMetres <= 0 ||
            !catalog.Items.TryGetValue(code, out var item) || !item.Unit.SameUnit(Units.Parse("מטר")))
            throw new InvalidOperationException("הסעיף חייב להיות קיים במחירון ויחידתו מטר; אין המרת אורך לכמות אחרת");
        Code = item.Code; Description = item.Description;
        Price = catalog.Prices.TryGetValue(code, out var price) ? price.Price : null;
        CatalogHash = catalog.FileHash; ApprovedBy = approver.Trim(); ApprovedAtUtc = DateTime.UtcNow;
    }
    public void Withdraw()
    { Code = Description = CatalogHash = ApprovedBy = null; Price = null; ApprovedAtUtc = null; }
}

public static class InfrastructureBoq
{
    public const string Unknown = "לא מסווג";
    public static readonly string[] Topics = { "מים", "ביוב", "ניקוז", "גז", "חשמל", "תאורה", "תקשורת", "רמזורים", Unknown };
    private static readonly (string Topic, string Code, string Pattern)[] Signals =
    {
        ("מים","WATER",@"WATER|WTR|WAT|WT|MAIM|מים"), ("ביוב","SEWER",@"SEWER|SEW|SWR|SW|BIUV|ביוב"),
        ("ניקוז","DRAINAGE",@"DRAINAGE|DRAIN|DR|NIKUZ|ניקוז"), ("גז","GAS",@"GAS|GAZ|גז"),
        ("חשמל","ELECTRIC",@"ELECTRIC|ELECTRICAL|ELEC|EL|HASHMAL|חשמל"), ("תאורה","LIGHTING",@"LIGHTING|TEURA|תאורה"),
        ("תקשורת","TELECOM",@"TELECOM|COMMUNICATION|COMM|TC|BEZEQ|תקשורת|בזק"),
        ("רמזורים","SIGNALS",@"TRAFFIC.?SIGNAL|RAMZOR|רמזורים"),
    };
    public static (string Topic, string Evidence, string Code) Classify(string layer, string source)
    {
        string[] Matches(string text) => Signals.Where(s => Regex.IsMatch(text,
            @"(?<![\p{L}\p{N}])(?:"+s.Pattern+@")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
            .Select(s=>s.Topic).Distinct().ToArray();
        var fromLayer=Matches(layer); var fromSource=Matches(source);
        var hits=fromLayer.Length>0?fromLayer:fromSource;
        if(hits.Length!=1) return (Unknown,hits.Length>1?"סימנים סותרים — לבדיקה":"ללא זיהוי מוכח", "UNCLASSIFIED");
        return (hits[0],fromLayer.Length>0?"הצעה לפי שם השכבה — טעון אישור":"הצעה לפי מקור השרטוט — טעון אישור",
            Signals.Single(s=>s.Topic==hits[0]).Code);
    }
    public static IReadOnlyList<InfrastructureGroup> Group(IEnumerable<InfrastructureLine> lines) => lines
        .GroupBy(l=>(l.Source,l.Layer,l.Topic))
        .Select(g=>new InfrastructureGroup {Source=g.Key.Source,Layer=g.Key.Layer,Topic=g.Key.Topic,Count=g.Count(),
            LengthMetres=g.Sum(l=>l.LengthMetres),ClosedCount=g.Count(l=>l.Closed),Evidence=g.First().Evidence})
        .OrderBy(g=>Array.IndexOf(Topics,g.Topic)).ThenByDescending(g=>g.LengthMetres).ToArray();

    /// <summary>Apply reviewed categories consistently to the workbook, DWG and source receipt.</summary>
    public static InfrastructureLine ReviewedLine(InfrastructureLine line, InfrastructureGroup group)
    {
        if (line.Source != group.Source || line.Layer != group.Layer)
            throw new ArgumentException("The review belongs to a different source layer");
        var code = Signals.FirstOrDefault(s => s.Topic == group.Topic).Code ?? "UNCLASSIFIED";
        var suffix = line.OutputLayer.IndexOf("__", StringComparison.Ordinal);
        return line with { Topic = group.Topic, OutputLayer = "BOQ_" + code +
            (suffix >= 0 ? line.OutputLayer[suffix..] : "__" + line.OutputLayer) };
    }

    public static void Export(string path, IReadOnlyList<InfrastructureGroup> groups, IReadOnlyList<InfrastructureLine> lines,
        CatalogSnapshot catalog, IReadOnlyList<string> findings, string drawing, string drawingHash, string measurementEngine)
    {
        var w=new MiniXlsx.Workbook{SheetName="כתב כמויות תשתיות",RightToLeft=true,FreezeTopRows=5,PrintTitleRows=5,PrintLandscapeFitToWidth=true,ShowGridLines=false};
        w.Styles.Add(new MiniXlsx.Style(Bold:true,FontSize:16,FillRgb:"DCECE7",WrapText:true));
        w.Styles.Add(new MiniXlsx.Style(Bold:true,FillRgb:"DCECE7",WrapText:true,ThinTopBottomBorder:true));
        w.Styles.Add(new MiniXlsx.Style(WrapText:true,AlignTop:true));
        w.Styles.Add(new MiniXlsx.Style(NumFmtId:MiniXlsx.NumFmtThousands2));
        w.Styles.Add(new MiniXlsx.Style(FillRgb:"FFF2CC",WrapText:true));
        foreach(var width in new[]{("A",6d),("B",14d),("C",18d),("D",48d),("E",9d),("F",16d),("G",16d),("H",18d),("I",24d),("J",34d),("K",36d)})
            w.ColumnWidths.Add((width.Item1[0]-'A'+1,width.Item2));
        w.Rows.Add(new MiniXlsx.OutRow(1){HeightPoints=28}.Text("A","כתב כמויות לתשתיות — טיוטה לבדיקה",1));
        w.SingleRowMerges.Add("A1:K1");
        w.Rows.Add(new MiniXlsx.OutRow(2){HeightPoints=42}.Text("A","שורות ללא שיוך מאושר נשארות ללא מחיר וסכום. אין להסיק קוטר, חומר, עומק או תכנון לביצוע משם שכבה בלבד. צורות סגורות וסימוני עזר דורשים בדיקה.",5));
        w.SingleRowMerges.Add("A2:K2");
        w.Rows.Add(new MiniXlsx.OutRow(3).Text("A","מחירון: "+catalog.SnapshotId+" | "+catalog.PublicationNote,3)); w.SingleRowMerges.Add("A3:K3");
        var headers=new[]{"מס׳","נושא","סעיף","תיאור הסעיף / המפרט","יחידה","אורך נמדד","מחיר יחידה ₪","סכום ₪","מצב","שכבת מקור","קובץ מקור"};
        var header=new MiniXlsx.OutRow(5){HeightPoints=28};
        for(var i=0;i<headers.Length;i++) header.Text(((char)('A'+i)).ToString(),headers[i],2);
        w.Rows.Add(header);
        var n=6;
        foreach(var g in groups)
        {
            var row=new MiniXlsx.OutRow(n){HeightPoints=42};
            row.Number("A",(double)(n-5),4).Text("B",g.Topic,3).Text("C",g.Code??"",3)
                .Text("D",g.Description??(g.Specification.Length>0?g.Specification:"נדרש מפרט ושיוך לסעיף"),3)
                .Text("E","מטר",3).Number("F",g.LengthMetres,4).Text("I",g.Status+(g.ClosedCount>0?$" · {g.ClosedCount} קווים סגורים":""),5)
                .Text("J",g.Layer,3).Text("K",g.Source,3);
            if(g.Approved && g.CatalogHash!=catalog.FileHash) throw new InvalidOperationException("המחירון השתנה — יש לאשר את השיוך מחדש");
            if(g.Approved && g.Price is { } price) row.Number("G",price,4).Formula("H",$"F{n}*G{n}",4);
            w.Rows.Add(row); n++;
        }
        w.Rows.Add(new MiniXlsx.OutRow(n+1).Text("D","סכום חלקי של שורות מאושרות עם מחיר בלבד — ללא מע״מ",2)
            .Formula("H",n>6?$"SUM(H6:H{n-1})":"0",4));
        var trace=new MiniXlsx.Worksheet{SheetName="מדידות מקור",RightToLeft=true};
        foreach(var c in new[]{"A","B","C","D","E","F","G"})trace.ColumnWidths.Add((c[0]-'A'+1,c=="A"||c=="C"?38:22));
        trace.Rows.Add(new MiniXlsx.OutRow(1).Text("A","מקור",2).Text("B","Handle",2).Text("C","שכבה",2).Text("D","סוג עצם",2)
            .Text("E","אורך במטרים",2).Text("F","נושא מוצע",2).Text("G","סגור",2));
        for(var i=0;i<lines.Count;i++){var l=lines[i];trace.Rows.Add(new MiniXlsx.OutRow(i+2).Text("A",l.Source,3).Text("B",l.Handle,3)
            .Text("C",l.Layer,3).Text("D",l.EntityType,3).Number("E",l.LengthMetres,4).Text("F",l.Topic,3).Text("G",l.Closed?"כן — לבדיקה":"לא",3));}
        w.AdditionalSheets.Add(trace);
        var checks=new MiniXlsx.Worksheet{SheetName="בדיקות ומקור המחירון",RightToLeft=true}; checks.ColumnWidths.Add((1,30)); checks.ColumnWidths.Add((2,100));
        var notes=new List<(string,string)> {("שרטוט",drawing),("SHA-256 מקור",drawingHash),("מחירון",catalog.SnapshotId),
            ("SHA-256 מחירון",catalog.FileHash),("מנוע מדידה",measurementEngine),("תחום הסריקה","כל Model Space והמקורות הטעונים; מקרא הוחרג. לא הוחל תחום פוליגון במסלול התשתיות."),
            ("אורך","אורך גאומטרי תלת־ממדי של הקווים, במטרים; לא כולל תוספות ביצוע, חפירה, אביזרים או שוחות"),
            ("ביקורת",$"{lines.Count} קווים, {groups.Count} קבוצות; שורות מתומחרות רק לאחר אישור מפורש"),
            ("אזהרה","זו אינה הוכחה שכל קו הוא צינור לביצוע. נדרש לבדוק קווים מקבילים, כפילויות, מצב קיים, מידות ומפרט.")};
        notes.AddRange(findings.Select(f=>("ממצא",f)));
        for(var i=0;i<notes.Count;i++)checks.Rows.Add(new MiniXlsx.OutRow(i+1){HeightPoints=38}.Text("A",notes[i].Item1,2).Text("B",notes[i].Item2,3));
        w.AdditionalSheets.Add(checks);
        MiniXlsx.Write(w,path);
    }
}
