using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Xml.Linq;
using MahodAI.CivilDelivery.Estimate;
using Xunit;
using Xunit.Abstractions;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// Cached formula values (Natali, 29.09.2026: Teams preview / Protected View / phones showed EMPTY quantities because the
/// workbook carried formulas without values). The oracle is the v7 reference workbook recalculated by Microsoft Excel
/// itself (Fixtures/boq_v2/v7_workbook_cells_excel_oracle.json); the unit cases pin the Excel semantics the writers rely
/// on and the "no value rather than a wrong value" rule.
/// </summary>
public sealed class XlsxFormulaEvaluatorTests : IDisposable
{
    // v4 (30.09.2026): 537 formulas in the reference workbook (dump of כתב_כמויות_6422_v7.xlsx: "formulas 537") — the total
    // formula on every BoQ line row, N() in the crossings sheet, count_of / line_sum rows. Re-check after the oracle fixture
    // is regenerated (rules 2.2: 498).
    // v4.5 (frozen-d, 30.09.2026): 539 — 12 of them are IF(E="","",…) totals of unpriced rows whose Excel value is "".
    // b3 (rules 2.8, 01.10.2026): 540 — D7 has a second part (southern koltan3 blocks), one more detail-row formula.
    private const int OracleFormulaCount = 540;
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "Mahod-cached-values-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public XlsxFormulaEvaluatorTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string Output(string name = "book.xlsx")
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    // Same location rule as BoqRulesV2GoldenAcceptanceTests (the standalone test project links these sources and the
    // Fixtures folder); falls back to the copy next to the test assembly.
    private static string FixturePath(string name, [CallerFilePath] string here = "")
    {
        var besideSource = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here) ?? ".", "..", "Fixtures", "boq_v2", name));
        return File.Exists(besideSource) ? besideSource : Path.Combine(AppContext.BaseDirectory, "Fixtures", "boq_v2", name);
    }

    private static JsonDocument Oracle() => JsonDocument.Parse(File.ReadAllText(FixturePath("v7_workbook_cells_excel_oracle.json")));

    private static XDocument Xml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }

    private static bool SameNumber(double actual, double expected) =>
        Math.Abs(actual - expected) <= 1e-9 * Math.Max(1, Math.Abs(expected));

    // ------------------------------------------------------------------ Excel oracle

    [Fact]
    public void EveryFormulaOfTheV7ReferenceWorkbookEvaluatesToTheValueExcelComputed()
    {
        using var oracle = Oracle();
        var source = new XlsxDictionaryCellSource();
        var formulas = 0;
        foreach (var sheet in oracle.RootElement.GetProperty("sheets").EnumerateObject())
        {
            source.AddSheet(sheet.Name);
            foreach (var cell in sheet.Value.EnumerateObject())
            {
                if (cell.Value.TryGetProperty("f", out var formula))
                {
                    source.Set(sheet.Name, cell.Name, XlsxSourceCell.FromFormula(formula.GetString()!));
                    formulas++;
                }
                else if (cell.Value.TryGetProperty("n", out var number))
                    source.Set(sheet.Name, cell.Name, XlsxSourceCell.FromNumber(number.GetDouble()));
                else
                    source.Set(sheet.Name, cell.Name, XlsxSourceCell.FromText(cell.Value.GetProperty("s").GetString()!));
            }
        }

        var result = XlsxFormulaEvaluator.Evaluate(source);

        Assert.Equal(OracleFormulaCount, formulas);
        Assert.Equal(OracleFormulaCount, result.FormulaCount);
        Assert.True(result.Unevaluable.Count == 0, "Not evaluable: " + string.Join("; ",
            result.Unevaluable.Take(20).Select(u => $"'{u.Sheet}'!{u.Reference}: {u.Reason}")));
        var failures = new List<string>();
        var compared = 0;
        foreach (var sheet in oracle.RootElement.GetProperty("excel_values").EnumerateObject())
            foreach (var cell in sheet.Value.EnumerateObject())
            {
                compared++;
                if (!result.TryGetValue(sheet.Name, cell.Name, out var actual))
                {
                    failures.Add($"'{sheet.Name}'!{cell.Name}: no value");
                    continue;
                }
                switch (cell.Value.ValueKind)
                {
                    case JsonValueKind.Number:
                        var excel = cell.Value.GetDouble();
                        if (actual.Kind != XlsxValueKind.Number || !SameNumber(actual.Number, excel))
                            failures.Add($"'{sheet.Name}'!{cell.Name}: {actual.Kind} {actual.Number:R} vs Excel {excel:R}");
                        break;
                    case JsonValueKind.String:
                        if (actual.Kind != XlsxValueKind.Text || actual.Text != cell.Value.GetString())
                            failures.Add($"'{sheet.Name}'!{cell.Name}: {actual.Kind} '{actual.Text}' vs Excel '{cell.Value.GetString()}'");
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        if (actual.Kind != XlsxValueKind.Boolean || actual.IsTrue != cell.Value.GetBoolean())
                            failures.Add($"'{sheet.Name}'!{cell.Name}: {actual.Kind} vs Excel {cell.Value.GetBoolean()}");
                        break;
                    default:
                        failures.Add($"'{sheet.Name}'!{cell.Name}: unexpected oracle value {cell.Value.ValueKind}");
                        break;
                }
            }
        foreach (var line in failures.Take(40)) _output.WriteLine(line);
        Assert.Equal(OracleFormulaCount, compared); // Excel's value exists for every formula, and only for formulas
        Assert.Equal(OracleFormulaCount, result.Values.Count);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
    }

    [Fact]
    public void WritingTheV7ReferenceWorkbookStoresExcelsValueInEveryFormulaCell()
    {
        using var oracle = Oracle();
        var workbook = OracleWorkbook(oracle.RootElement.GetProperty("sheets"));
        var path = Output("v7-cached.xlsx");

        MiniXlsx.Write(workbook, path);

        var expected = oracle.RootElement.GetProperty("excel_values");
        using var zip = ZipFile.OpenRead(path);
        var names = Xml(zip, "xl/workbook.xml").Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!).ToList();
        var formulas = 0;
        var failures = new List<string>();
        for (var index = 0; index < names.Count; index++)
        {
            var values = expected.GetProperty(names[index]);
            foreach (var cell in Xml(zip, $"xl/worksheets/sheet{index + 1}.xml").Descendants(Main + "c").Where(c => c.Element(Main + "f") != null))
            {
                formulas++;
                var reference = (string)cell.Attribute("r")!;
                var cachedValue = cell.Element(Main + "v");
                var type = (string?)cell.Attribute("t");
                if (cachedValue == null || !values.TryGetProperty(reference, out var excel))
                {
                    failures.Add($"'{names[index]}'!{reference}: no cached value");
                    continue;
                }
                if (excel.ValueKind == JsonValueKind.String)
                {
                    if (type != "str" || cachedValue.Value != excel.GetString())
                        failures.Add($"'{names[index]}'!{reference}: t={type} '{cachedValue.Value}' vs Excel '{excel.GetString()}'");
                }
                else if (type != null || !SameNumber(double.Parse(cachedValue.Value, NumberStyles.Float, CultureInfo.InvariantCulture), excel.GetDouble()))
                {
                    failures.Add($"'{names[index]}'!{reference}: t={type} {cachedValue.Value} vs Excel {excel.GetDouble():R}");
                }
            }
        }
        Assert.Equal(OracleFormulaCount, formulas);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
        // Excel still recalculates everything on open.
        Assert.Equal("1", (string?)Xml(zip, "xl/workbook.xml").Root!.Element(Main + "calcPr")!.Attribute("fullCalcOnLoad"));
    }

    private static MiniXlsx.Workbook OracleWorkbook(JsonElement sheets)
    {
        MiniXlsx.Workbook? workbook = null;
        foreach (var sheet in sheets.EnumerateObject())
        {
            MiniXlsx.Worksheet target;
            if (workbook == null)
            {
                workbook = new MiniXlsx.Workbook { SheetName = sheet.Name, RightToLeft = true };
                target = workbook;
            }
            else
            {
                target = new MiniXlsx.Worksheet { SheetName = sheet.Name, RightToLeft = true };
                workbook.AdditionalSheets.Add(target);
            }
            var rows = new Dictionary<int, MiniXlsx.OutRow>();
            foreach (var cell in sheet.Value.EnumerateObject())
            {
                Assert.True(XlsxFormulaEvaluator.TryParseReference(cell.Name, out var row, out var column), cell.Name);
                if (!rows.TryGetValue(row, out var outRow))
                {
                    outRow = new MiniXlsx.OutRow(row);
                    rows[row] = outRow;
                    target.Rows.Add(outRow);
                }
                var letters = XlsxFormulaEvaluator.ColumnName(column);
                if (cell.Value.TryGetProperty("f", out var formula)) outRow.Formula(letters, formula.GetString()!, 0);
                else if (cell.Value.TryGetProperty("n", out var number)) outRow.Number(letters, number.GetDouble(), 0);
                else outRow.Text(letters, cell.Value.GetProperty("s").GetString()!, 0);
            }
        }
        return workbook!;
    }

    // --------------------------------------------------------------- Excel semantics

    /// <summary>Sheet "S": A1..A4 = 1, 2, "x", 4; B1..B4 = 10..40; C1..C4 = labels; Z99 empty. Plus two referenced sheets.</summary>
    private static XlsxDictionaryCellSource Standard()
    {
        var source = new XlsxDictionaryCellSource();
        source.Set("S", "A1", XlsxSourceCell.FromNumber(1)).Set("S", "A2", XlsxSourceCell.FromNumber(2))
            .Set("S", "A3", XlsxSourceCell.FromText("x")).Set("S", "A4", XlsxSourceCell.FromNumber(4))
            .Set("S", "B1", XlsxSourceCell.FromNumber(10)).Set("S", "B2", XlsxSourceCell.FromNumber(20))
            .Set("S", "B3", XlsxSourceCell.FromNumber(30)).Set("S", "B4", XlsxSourceCell.FromNumber(40))
            .Set("S", "C1", XlsxSourceCell.FromText("ישיר")).Set("S", "C2", XlsxSourceCell.FromText("  אחר "))
            .Set("S", "C3", XlsxSourceCell.FromText("ישיר")).Set("S", "C4", XlsxSourceCell.FromText("—"))
            .Set("Data O'Brien", "B2", XlsxSourceCell.FromNumber(12.3456))
            .Set("Plain", "A1", XlsxSourceCell.FromNumber(7));
        return source;
    }

    private static XlsxCachedValue? One(string formula)
    {
        var source = Standard().Set("S", "Z1", XlsxSourceCell.FromFormula(formula));
        return XlsxFormulaEvaluator.Evaluate(source).TryGetValue("S", "Z1", out var value) ? value : null;
    }

    [Theory]
    [InlineData("ROUND(2.675,2)", 2.68)]
    [InlineData("ROUND(1.005,2)", 1.01)]
    [InlineData("ROUND(-2.5,0)", -3d)]
    [InlineData("ROUND(2.5,0)", 3d)]
    [InlineData("ROUND(-0.125,2)", -0.13)]
    [InlineData("ROUND(1234.5678,-2)", 1200d)]
    [InlineData("ROUND(82.469*0.5,3)", 41.235)]
    [InlineData("ROUND(72.753*0.5,3)", 36.377)]
    [InlineData("ROUND(2.5,0.9)", 3d)]
    [InlineData("1+2*3", 7d)]
    [InlineData("(1+2)*3", 9d)]
    [InlineData("-2^2", 4d)]
    [InlineData("2^3^2", 64d)]
    [InlineData("2^-1", 0.5)]
    [InlineData("10/4-1", 1.5)]
    [InlineData("--TRUE", 1d)]
    [InlineData("+5", 5d)]
    [InlineData("-A1", -1d)]
    [InlineData("TRUE+1", 2d)]
    [InlineData("Z99+1", 1d)]
    [InlineData("Z99", 0d)]
    [InlineData("$A$1+A$2+$A2", 5d)]
    [InlineData("'Data O''Brien'!B2*2", 24.6912)]
    [InlineData("'data o''brien'!$B$2", 12.3456)]
    [InlineData("Plain!A1+1", 8d)]
    [InlineData("CHOOSE(2,10,20,30)", 20d)]
    [InlineData("CHOOSE(A2,10,20,30)", 20d)]
    [InlineData("ROUND(PI()*(50/200)^2,4)", 0.1963)]
    [InlineData("ROUND(SQRT(3)/4*(65/100)^2,4)", 0.1829)]
    [InlineData("SQRT(16)", 4d)]
    [InlineData("ABS(-3.5)", 3.5)]
    [InlineData("SUM(A1:A4)", 7d)]
    [InlineData("SUM(A1,A3,A4)", 5d)]
    [InlineData("SUM(A1:B2)", 33d)]
    [InlineData("SUM(1,TRUE)", 2d)]
    [InlineData("SUMPRODUCT(A1:A2,B1:B2)", 50d)]
    [InlineData("SUMPRODUCT(A1:A4,B1:B4)", 210d)]
    [InlineData("SUMPRODUCT(--NOT(ISNUMBER(A1:A4)))", 1d)]
    [InlineData("SUMPRODUCT(--ISNUMBER(A1:A4))", 3d)]
    [InlineData("SUMIF(C1:C4,\"ישיר\",B1:B4)", 40d)]
    [InlineData("COUNTIF(C1:C4,\"ישיר\")", 2d)]
    [InlineData("MIN(A1:A4,B1)", 1d)]
    [InlineData("MAX(A1:A4)", 4d)]
    [InlineData("LEN(TRIM(\"  a   b \"))", 3d)]
    [InlineData("LEN(Z99)", 0d)]
    [InlineData("IF(TRUE,1,1/0)", 1d)]
    [InlineData("IF(A1=1,B1,B2)", 10d)]
    [InlineData("IF(ISNUMBER(C4),ROUND(A1*C4,2),0)", 0d)]
    [InlineData("IF(AND(ISNUMBER(A1),A1>=0,A1<=5),A1,NA())", 1d)]
    [InlineData("1+2+3+4+5", 15d)]
    [InlineData("  1 +  2 ", 3d)]
    [InlineData("1E3+.5", 1000.5)]
    [InlineData("16.0", 16d)]
    public void NumericResultsFollowExcel(string formula, double expected)
    {
        var value = One(formula);
        Assert.True(value.HasValue, formula + " should be evaluable");
        Assert.Equal(XlsxValueKind.Number, value!.Value.Kind);
        Assert.Equal(expected, value.Value.Number, 12);
    }

    [Theory]
    [InlineData("\"a\"\"b\"", "a\"b")]
    [InlineData("\"x\"&A1", "x1")]
    [InlineData("COUNTIF(C1:C4,\"ישיר\")&\" מתוך 4\"", "2 מתוך 4")]
    [InlineData("CHOOSE(1,\"50×60\",\"x\")", "50×60")]
    [InlineData("IF(A1=1,\"51.32.1852\",\"51.32.1862\")", "51.32.1852")]
    [InlineData("TRIM(C2)&Z99", "אחר")]
    [InlineData("IF(Z99=\"\",\"\",1)", "")]
    [InlineData("IF(OR(LEN(TRIM(C4))=0,C4=\"—\"),\"חסר מק\"\"ט\",\"הושלם\")", "חסר מק\"ט")]
    public void TextResultsFollowExcel(string formula, string expected)
    {
        var value = One(formula);
        Assert.True(value.HasValue, formula + " should be evaluable");
        Assert.Equal(XlsxValueKind.Text, value!.Value.Kind);
        Assert.Equal(expected, value.Value.Text);
    }

    [Theory]
    [InlineData("0.1+0.2=0.3", true)] // numbers compare at 15 significant digits
    [InlineData("\"a\"=\"A\"", true)] // text equality ignores case
    [InlineData("1=\"1\"", false)]
    [InlineData("\"a\">5", true)] // numbers < text < logical values
    [InlineData("Z99=0", true)] // empty = 0 ...
    [InlineData("Z99=\"\"", true)] // ... and = ""
    [InlineData("ISNUMBER(Z99)", false)]
    [InlineData("ISNUMBER(A3)", false)]
    [InlineData("NOT(Z99)", true)]
    [InlineData("2<>2", false)]
    [InlineData("3>=3", true)]
    [InlineData("AND(A1=1,B1>5)", true)]
    [InlineData("OR(A1=2,FALSE)", false)]
    [InlineData("AND(A1:A4)", true)]
    [InlineData("IF(FALSE,1)", false)]
    [InlineData("C4<>\"—\"", false)]
    public void LogicalResultsFollowExcel(string formula, bool expected)
    {
        var value = One(formula);
        Assert.True(value.HasValue, formula + " should be evaluable");
        Assert.Equal(XlsxValueKind.Boolean, value!.Value.Kind);
        Assert.Equal(expected, value.Value.IsTrue);
    }

    [Theory]
    [InlineData("1/0")]
    [InlineData("NA()")]
    [InlineData("IF(FALSE,1,NA())")]
    [InlineData("VLOOKUP(1,A1:B2,2,FALSE)")]
    [InlineData("A1:A2")]
    [InlineData("A1:A2*2")]
    [InlineData("SUM(A1:A2*2)")]
    [InlineData("A3+1")]
    [InlineData("\"a\"&TRUE")]
    [InlineData("\"a\"&0.5")]
    [InlineData("Nope!A1")]
    [InlineData("'Nope'!A1")]
    [InlineData("XFE1")]
    [InlineData("A0")]
    [InlineData("SomeName")]
    [InlineData("1+")]
    [InlineData("(1")]
    [InlineData("{1,2}")]
    [InlineData("A1 B1")]
    [InlineData("5%")]
    [InlineData("SQRT(-1)")]
    [InlineData("0^0")]
    [InlineData("ROUND(1)")]
    [InlineData("SUM(1,)")]
    [InlineData("CHOOSE(4,1,2,3)")]
    [InlineData("IF(\"x\",1,2)")]
    [InlineData("\"b\"<\"a\"")]
    [InlineData("COUNTIF(C1:C4,\">1\")")]
    [InlineData("COUNTIF(C1:C4,\"יש*\")")]
    [InlineData("SUMPRODUCT(A1:A2,B1:B3)")]
    [InlineData("#N/A")]
    [InlineData("=1")]
    [InlineData("")]
    public void ErrorsAndUnsupportedFormulasGetNoValueAndNeverThrow(string formula)
    {
        var source = Standard().Set("S", "Z1", XlsxSourceCell.FromFormula(formula));

        var result = XlsxFormulaEvaluator.Evaluate(source);

        Assert.False(result.TryGetValue("S", "Z1", out _));
        var entry = Assert.Single(result.Unevaluable);
        Assert.Equal(("S", "Z1"), (entry.Sheet, entry.Reference));
        Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        Assert.Equal(1, result.FormulaCount);
    }

    [Fact]
    public void FormulasReadingAnUnevaluableCellAreUnevaluableButAnUntakenBranchIsNot()
    {
        var source = Standard()
            .Set("S", "E1", XlsxSourceCell.FromFormula("1/0"))
            .Set("S", "E2", XlsxSourceCell.FromFormula("E1+1"))
            .Set("S", "E3", XlsxSourceCell.FromFormula("SUM(E1:E2)"))
            .Set("S", "E4", XlsxSourceCell.FromFormula("IF(A1=1,5,E1)"))
            .Set("S", "E5", XlsxSourceCell.FromFormula("E4*2"));

        var result = XlsxFormulaEvaluator.Evaluate(source);

        Assert.Equal(new[] { "E1", "E2", "E3" }, result.Unevaluable.Select(u => u.Reference));
        Assert.Contains("#DIV/0!", result.Unevaluable[0].Reason);
        Assert.Contains("E1", result.Unevaluable[1].Reason);
        Assert.True(result.TryGetValue("S", "E4", out var e4));
        Assert.Equal(5, e4.Number);
        Assert.True(result.TryGetValue("S", "E5", out var e5));
        Assert.Equal(10, e5.Number);
    }

    [Fact]
    public void CircularReferencesAndEveryFormulaDependingOnThemGetNoValue()
    {
        var source = Standard()
            .Set("S", "F1", XlsxSourceCell.FromFormula("F1+1"))
            .Set("S", "F2", XlsxSourceCell.FromFormula("F3+1"))
            .Set("S", "F3", XlsxSourceCell.FromFormula("F2+1"))
            .Set("S", "F4", XlsxSourceCell.FromFormula("IF(TRUE,1,F2)"))
            .Set("S", "F5", XlsxSourceCell.FromFormula("A1+1"));

        var result = XlsxFormulaEvaluator.Evaluate(source);

        Assert.Equal(new[] { "F1", "F2", "F3", "F4" }, result.Unevaluable.Select(u => u.Reference));
        Assert.All(result.Unevaluable, u => Assert.Contains("circular", u.Reason));
        Assert.True(result.TryGetValue("S", "F5", out var f5));
        Assert.Equal(2, f5.Number);
    }

    [Fact]
    public void LongDependencyChainsAreEvaluatedIterativelyWithoutDeepRecursion()
    {
        // Listed last-to-first, so the dependency walk has to go 20,000 cells deep before the first value exists.
        var source = new XlsxDictionaryCellSource().Set("S", "A1", XlsxSourceCell.FromNumber(1));
        for (var row = 20000; row >= 2; row--)
            source.Set("S", "A" + row.ToString(CultureInfo.InvariantCulture), XlsxSourceCell.FromFormula($"A{row - 1}+1"));

        var result = XlsxFormulaEvaluator.Evaluate(source);

        Assert.Empty(result.Unevaluable);
        Assert.Equal(19999, result.FormulaCount);
        Assert.True(result.TryGetValue("S", "A20000", out var last));
        Assert.Equal(20000, last.Number);
    }

    [Fact]
    public void ExcessiveNestingIsRefusedInsteadOfExhaustingTheStack()
    {
        Assert.Null(One(new string('(', 5000) + "1" + new string(')', 5000)));
        Assert.Equal(1, One(new string('(', 20) + "1" + new string(')', 20))!.Value.Number);
    }

    [Fact]
    public void TheSupportedFunctionsAreExactlyWhatTheWorkbookWritersUse()
    {
        // N: the BoQ v4 crossings sheet (G = ROUND(E*fill+N(F),3), F may be the text "לא נמדד").
        Assert.Equal(
            new[] { "ABS", "AND", "CHOOSE", "COUNTIF", "IF", "ISNUMBER", "LEN", "MAX", "MIN", "N", "NA", "NOT", "OR", "PI", "ROUND", "SQRT", "SUM", "SUMIF", "SUMPRODUCT", "TRIM" },
            XlsxFormulaEvaluator.SupportedFunctions.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void NTurnsTextAndEmptyIntoZeroAndKeepsNumbers()
    {
        // Excel N(): the v4 crossings sheet adds a measured hatch area (a number) or nothing ("לא נמדד", text).
        var source = new XlsxDictionaryCellSource();
        source.AddSheet("S");
        source.Set("S", "F2", XlsxSourceCell.FromNumber(2.9365));
        source.Set("S", "F3", XlsxSourceCell.FromText("לא נמדד"));
        source.Set("S", "E2", XlsxSourceCell.FromNumber(18.75));
        source.Set("S", "E3", XlsxSourceCell.FromNumber(18.75));
        source.Set("S", "G2", XlsxSourceCell.FromFormula("ROUND(E2*0.5+N(F2),3)"));
        source.Set("S", "G3", XlsxSourceCell.FromFormula("ROUND(E3*0.5+N(F3),3)"));
        source.Set("S", "G4", XlsxSourceCell.FromFormula("ROUND(N(F4),3)"));
        source.Set("S", "G5", XlsxSourceCell.FromFormula("N(TRUE)+N(FALSE)"));
        var result = XlsxFormulaEvaluator.Evaluate(source);
        Assert.Empty(result.Unevaluable);
        Assert.True(result.TryGetValue("S", "G2", out var g2));
        Assert.Equal(12.312, g2.Number, 9);
        Assert.True(result.TryGetValue("S", "G3", out var g3));
        Assert.Equal(9.375, g3.Number, 9);
        Assert.True(result.TryGetValue("S", "G4", out var g4));
        Assert.Equal(0, g4.Number);
        Assert.True(result.TryGetValue("S", "G5", out var g5));
        Assert.Equal(1, g5.Number);
    }

    // ------------------------------------------------------------------ MiniXlsx.Write

    private static MiniXlsx.Workbook CachedValuesWorkbook()
    {
        var wb = new MiniXlsx.Workbook { SheetName = "כתב כמויות", RightToLeft = true };
        wb.Styles.Add(new MiniXlsx.Style(NumFmtId: MiniXlsx.NumFmtThousands2));
        wb.Rows.Add(new MiniXlsx.OutRow(1)
            .Number("A", 2.675, 1).Number("B", 4m, 1).Text("C", "ישיר", 0)
            .Formula("D", "ROUND(A1,2)", 1)
            .Formula("E", "IF(B1=\"\",\"\",ROUND(A1*B1,2))", 1)
            .Formula("F", "IF('פרמטרים'!$C$2=1,\"51.32.1852\",\"51.32.1862\")", 0)
            .Formula("G", "C1=\"ישיר\"", 0)
            .Formula("H", "VLOOKUP(1,A1:B1,2,FALSE)", 1)
            .Formula("I", "H1+1", 1)
            .Formula("J", "'פרמטרים'!C2&\" < & > \"\"q\"\"\"", 0));
        wb.Rows.Add(new MiniXlsx.OutRow(2).Formula("A", "SUM(D1:E1)", 1).Formula("B", "A2/0", 1));
        var parameters = new MiniXlsx.Worksheet { SheetName = "פרמטרים", RightToLeft = true };
        parameters.Rows.Add(new MiniXlsx.OutRow(2).Number("C", 1m, 0));
        wb.AdditionalSheets.Add(parameters);
        return wb;
    }

    [Fact]
    public void WriteStoresEveryEvaluableFormulasValueWithItsExcelType()
    {
        var wb = CachedValuesWorkbook();
        var path = Output();

        MiniXlsx.Write(wb, path); // an unsupported formula (H1) and an error (B2) do not stop the write

        using var zip = ZipFile.OpenRead(path);
        var cells = Xml(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "c").ToDictionary(c => (string)c.Attribute("r")!);
        Assert.Equal("2.68", cells["D1"].Element(Main + "v")!.Value);
        Assert.Null(cells["D1"].Attribute("t"));
        Assert.Equal("1", (string?)cells["D1"].Attribute("s"));
        Assert.Equal("10.7", cells["E1"].Element(Main + "v")!.Value);
        Assert.Equal(13.38, double.Parse(cells["A2"].Element(Main + "v")!.Value, NumberStyles.Float, CultureInfo.InvariantCulture), 10);
        Assert.Equal("str", (string?)cells["F1"].Attribute("t"));
        Assert.Equal("51.32.1852", cells["F1"].Element(Main + "v")!.Value);
        Assert.Equal("b", (string?)cells["G1"].Attribute("t"));
        Assert.Equal("1", cells["G1"].Element(Main + "v")!.Value);
        Assert.Equal("str", (string?)cells["J1"].Attribute("t"));
        Assert.Equal("1 < & > \"q\"", cells["J1"].Element(Main + "v")!.Value);
        // No value rather than a wrong value: the formula alone, exactly as before.
        foreach (var reference in new[] { "H1", "I1", "B2" })
        {
            Assert.Null(cells[reference].Element(Main + "v"));
            Assert.Null(cells[reference].Attribute("t"));
        }
        Assert.Equal("VLOOKUP(1,A1:B1,2,FALSE)", cells["H1"].Element(Main + "f")!.Value);
        Assert.Equal("IF('פרמטרים'!$C$2=1,\"51.32.1852\",\"51.32.1862\")", cells["F1"].Element(Main + "f")!.Value);
        Assert.All(cells.Values.Where(c => c.Element(Main + "f") != null && !new[] { "H1", "I1", "B2" }.Contains((string)c.Attribute("r")!)),
            c => Assert.NotNull(c.Element(Main + "v")));
        Assert.Equal("1", (string?)Xml(zip, "xl/workbook.xml").Root!.Element(Main + "calcPr")!.Attribute("fullCalcOnLoad"));

        // The reader returns what Excel shows.
        var first = MiniXlsx.ReadAllSheets(path)[0];
        Assert.Equal("2.68", first[0].Single(c => c.Column == "D").Text);
        Assert.True(first[0].Single(c => c.Column == "D").IsNumeric);
        Assert.Equal("51.32.1852", first[0].Single(c => c.Column == "F").Text);
        Assert.False(first[0].Single(c => c.Column == "F").IsNumeric);
        Assert.Equal("TRUE", first[0].Single(c => c.Column == "G").Text);
        Assert.Equal("", first[0].Single(c => c.Column == "H").Text);
    }

    [Fact]
    public void TheEvaluationWriteUsesIsAvailableToCallers()
    {
        var evaluation = MiniXlsx.EvaluateFormulas(CachedValuesWorkbook());

        Assert.Equal(9, evaluation.FormulaCount);
        Assert.Null(evaluation.Failure);
        Assert.Equal(new[] { "H1", "I1", "B2" }, evaluation.Unevaluable.Select(u => u.Reference));
        Assert.Contains("VLOOKUP", evaluation.Unevaluable[0].Reason);
        Assert.Contains("H1", evaluation.Unevaluable[1].Reason);
        Assert.Contains("#DIV/0!", evaluation.Unevaluable[2].Reason);
        Assert.True(evaluation.TryGetValue("כתב כמויות", "E1", out var e1));
        Assert.Equal(XlsxValueKind.Number, e1.Kind);
        Assert.Equal(10.7, e1.Number);
        Assert.Equal(6, evaluation.Values.Count);
    }

    [Fact]
    public void WorkbooksWithCachedValuesStayByteIdenticalAcrossWritesAndCultures()
    {
        var first = Output("first.xlsx");
        var second = Output("second.xlsx");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            MiniXlsx.Write(CachedValuesWorkbook(), first);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            MiniXlsx.Write(CachedValuesWorkbook(), second);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));
        using var zip = ZipFile.OpenRead(second);
        var e1 = Xml(zip, "xl/worksheets/sheet1.xml").Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "E1");
        Assert.Equal("10.7", e1.Element(Main + "v")!.Value);
    }

    [Fact]
    public void AWorkbookWithoutFormulasIsWrittenAsBefore()
    {
        var wb = new MiniXlsx.Workbook { SheetName = "Old" };
        wb.Rows.Add(new MiniXlsx.OutRow(1).Text("A", "=not a formula", 0).Number("B", 7m, 0));
        var path = Output();

        MiniXlsx.Write(wb, path);

        var evaluation = MiniXlsx.EvaluateFormulas(wb);
        Assert.Equal(0, evaluation.FormulaCount);
        Assert.Empty(evaluation.Unevaluable);
        using var zip = ZipFile.OpenRead(path);
        var sheet = Xml(zip, "xl/worksheets/sheet1.xml");
        Assert.Empty(sheet.Descendants(Main + "f"));
        Assert.Equal("inlineStr", (string?)sheet.Descendants(Main + "c").Single(c => (string?)c.Attribute("r") == "A1").Attribute("t"));
    }
}
