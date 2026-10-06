using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;

/// <summary>
/// The pure state behind the "טען מחירון" sheet/column choice (b15): which columns a header row offers, the explicit
/// mapping built from the engineer's choice, and the Hebrew summary of an inspection. The reader decides nothing here:
/// every number comes from <see cref="PriceBookXlsxLoader.Inspect(string, PriceBookXlsxLoader.ColumnMapping)"/>.
/// </summary>
internal static class PriceBookMappingSelection
{
    /// <summary>A column the header row actually holds, shown as "C · יחידה".</summary>
    internal sealed record ColumnOption(string Column, string Header)
    {
        public override string ToString() => Header.Length == 0 ? Bidi.Ltr(Column) : $"{Bidi.Ltr(Column)} · {Header}";
    }

    /// <summary>"Leave the description out": an explicit choice, never an invented description.</summary>
    internal static readonly ColumnOption NoDescription = new("", "ללא עמודת תיאור");

    /// <summary>Spreadsheet column order (A &lt; Z &lt; AA), 0 for anything that is not 1–3 letters.</summary>
    internal static int ColumnIndex(string column)
    {
        if (string.IsNullOrEmpty(column) || column.Length > 3 || !column.All(c => c is >= 'A' and <= 'Z')) return 0;
        return column.Aggregate(0, (index, c) => index * 26 + c - 'A' + 1);
    }

    /// <summary>The non-empty cells of one header row, in sheet order; a header text is cut to 40 characters for display.</summary>
    internal static IReadOnlyList<ColumnOption> HeaderOptions(IEnumerable<MiniXlsx.CellText> headerRow) =>
        headerRow.Where(cell => ColumnIndex(cell.Column) > 0 && cell.Text.Trim().Length > 0)
            .GroupBy(cell => cell.Column, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(cell => ColumnIndex(cell.Column))
            .Select(cell => new ColumnOption(cell.Column, Clip(cell.Text.Trim(), 40)))
            .ToList();

    /// <summary>
    /// The explicit mapping for the engineer's choice, bound to the exact workbook hash of the preview. Null with a
    /// Hebrew reason while the choice is incomplete; the reader itself still validates every field again.
    /// </summary>
    internal static (PriceBookXlsxLoader.ColumnMapping? Mapping, string? Error) Build(
        string fileHash, string? sheetName, string? headerRowText,
        string? codeColumn, string? descriptionColumn, string? unitColumn, string? priceColumn)
    {
        if (string.IsNullOrEmpty(sheetName)) return (null, "יש לבחור גיליון.");
        if (!int.TryParse(headerRowText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var headerRow) ||
            headerRow < 1)
            return (null, "יש לבחור שורת כותרת (מספר שורה בגיליון).");
        if (string.IsNullOrEmpty(codeColumn)) return (null, "יש לבחור עמודת סעיף.");
        if (string.IsNullOrEmpty(unitColumn)) return (null, "יש לבחור עמודת יחידה.");
        if (string.IsNullOrEmpty(priceColumn)) return (null, "יש לבחור עמודת מחיר.");
        var description = string.IsNullOrEmpty(descriptionColumn) ? null : descriptionColumn;
        var chosen = new[] { codeColumn, description, unitColumn, priceColumn }.Where(c => c != null).ToList();
        if (chosen.Distinct(StringComparer.Ordinal).Count() != chosen.Count)
            return (null, "כל תפקיד צריך עמודה אחרת: אותה עמודה נבחרה פעמיים.");
        return (new PriceBookXlsxLoader.ColumnMapping(
            fileHash, sheetName, headerRow, codeColumn, description, unitColumn, priceColumn), null);
    }

    /// <summary>
    /// True when the explicit choice reads exactly what the automatic reading already applied (same bytes, sheet, header
    /// row and columns). Registration then stays automatic, so an unchanged profile keeps its exact bytes.
    /// </summary>
    internal static bool SameAsAutomatic(PriceBookXlsxLoader.Inspection automatic, PriceBookXlsxLoader.ColumnMapping chosen) =>
        automatic.IsUsable && automatic.AppliedMapping is { } applied &&
        string.Equals(applied.ExpectedFileHash, chosen.ExpectedFileHash, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(applied.SheetName, chosen.SheetName, StringComparison.Ordinal) &&
        applied.HeaderRow == chosen.HeaderRow &&
        string.Equals(applied.CodeColumn, chosen.CodeColumn, StringComparison.Ordinal) &&
        string.Equals(applied.DescriptionColumn, chosen.DescriptionColumn, StringComparison.Ordinal) &&
        string.Equals(applied.UnitColumn, chosen.UnitColumn, StringComparison.Ordinal) &&
        string.Equals(applied.PriceColumn, chosen.PriceColumn, StringComparison.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["imported"] = "נקלט",
        ["missing_price"] = "נקלט ללא מחיר",
        ["invalid_price"] = "נקלט, מחיר לא קריא",
        ["unknown_unit"] = "יחידה לא מוכרת (נשמרה כמו שהיא)",
        ["identical_duplicate"] = "כפילות זהה",
        ["conflicting_duplicate"] = "כפילות סותרת",
        ["missing_item_code"] = "שורת סעיף בלי קוד",
        ["unsupported_item_code"] = "קוד בפורמט לא נתמך",
        ["blank_row"] = "שורה ריקה",
        ["repeated_header"] = "כותרת חוזרת",
        ["note_unit"] = "שורת הערה",
        ["chapter_heading"] = "כותרת פרק",
        ["text_or_heading"] = "טקסט או כותרת",
    };

    /// <summary>Hebrew name of a reader reason code; an unknown code is shown as it is, never hidden.</summary>
    internal static string Reason(string code) => Reasons.TryGetValue(code, out var text) ? text : code;

    /// <summary>What the engineer approves: the reader's own counts, columns, problems and rejected-row samples.</summary>
    internal static string Summary(PriceBookXlsxLoader.Inspection inspection)
    {
        var nl = Environment.NewLine;
        var text = new StringBuilder();
        text.Append("גיליון: ").Append(inspection.SheetName ?? "—")
            .Append(" · שורת כותרת: ").Append(inspection.HeaderRow > 0 ? inspection.HeaderRow.ToString(CultureInfo.InvariantCulture) : "—").Append(nl);
        text.Append("מפרסם: ").Append(inspection.Publisher ?? "לא זוהה")
            .Append(" · מהדורה: ").Append(inspection.EditionNote ?? "—").Append(nl);
        text.Append("עמודות: סעיף=").Append(Col(inspection.CodeColumn))
            .Append(" תיאור=").Append(Col(inspection.DescriptionColumn))
            .Append(" יחידה=").Append(Col(inspection.UnitColumn))
            .Append(" מחיר=").Append(Col(inspection.PriceColumn)).Append(nl);
        text.Append($"סעיפים: {inspection.ItemCount:N0} · ללא מחיר: {inspection.MissingPriceCount:N0}").Append(nl);
        var coverage = inspection.Coverage;
        if (coverage.Evaluated)
            text.Append($"שורות מתחת לכותרת: {coverage.DataRows:N0} = נקלטו {coverage.ImportedRows:N0} + כפולות {coverage.DuplicateRows:N0}" +
                        $" + נדחו {coverage.RejectedRows:N0} + לא-סעיף {coverage.NonItemRows:N0}").Append(nl);
        else
            text.Append("הכיסוי לא נבדק: הקריאה נעצרה לפני שורות הנתונים.").Append(nl);
        if (inspection.Chapters.Count > 0)
            text.Append("פרקים: ").Append(string.Join(", ", inspection.Chapters.Take(20).Select(Bidi.Ltr)))
                .Append(inspection.Chapters.Count > 20 ? " …" : "").Append(nl);
        if (inspection.Problems.Count > 0)
        {
            text.Append(nl).Append("בעיות:").Append(nl);
            foreach (var problem in inspection.Problems.Take(6)) text.Append("• ").Append(problem).Append(nl);
            if (inspection.Problems.Count > 6) text.Append($"• ועוד {inspection.Problems.Count - 6:N0}").Append(nl);
        }
        var rejected = inspection.RowDiagnostics.Where(d => d.Outcome == PriceBookXlsxLoader.RowOutcome.Rejected).Take(5).ToList();
        if (rejected.Count > 0)
        {
            text.Append(nl).Append("דוגמאות לשורות שנדחו:").Append(nl);
            foreach (var row in rejected)
                text.Append($"• שורה {row.Row}: {Reason(row.ReasonCode)} — ").Append(row.Code.Length == 0 ? "(ריק)" : Bidi.Ltr(row.Code)).Append(nl);
        }
        return text.ToString().TrimEnd();
    }

    private static string Col(string? column) => column == null ? "—" : Bidi.Ltr(column);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
