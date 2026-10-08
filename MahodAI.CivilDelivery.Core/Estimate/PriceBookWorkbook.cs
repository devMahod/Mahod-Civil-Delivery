using System.Globalization;
using System.Text;
using ExcelDataReader;

namespace MahodAI.CivilDelivery.Estimate;

/// <summary>Reads price books from immutable bytes, preserving original identity and cell types.</summary>
public static class PriceBookWorkbook
{
    private static bool IsZip(byte[] bytes) => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4b;

    private static IExcelDataReader Open(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return ExcelReaderFactory.CreateReader(new MemoryStream(bytes, writable: false),
            new ExcelReaderConfiguration { FallbackEncoding = Encoding.GetEncoding(1255) });
    }

    public static IReadOnlyList<string> ReadSheetNames(byte[] bytes)
    {
        if (IsZip(bytes)) return MiniXlsx.ReadSheetNames(bytes);
        using var reader = Open(bytes);
        var names = new List<string>();
        do { names.Add(reader.Name); } while (reader.NextResult());
        return names;
    }

    public static List<List<MiniXlsx.CellText>> ReadSheet(byte[] bytes, string? sheetName = null)
    {
        if (IsZip(bytes)) return sheetName == null ? MiniXlsx.ReadFirstSheet(bytes) : MiniXlsx.ReadSheet(bytes, sheetName);
        using var reader = Open(bytes);
        do
        {
            if (sheetName != null && !string.Equals(reader.Name, sheetName, StringComparison.Ordinal)) continue;
            var rows = new List<List<MiniXlsx.CellText>>();
            int rowNumber = 0;
            while (reader.Read())
            {
                rowNumber++;
                var cells = new List<MiniXlsx.CellText>();
                for (int column = 0; column < reader.FieldCount; column++)
                {
                    var value = reader.GetValue(column);
                    if (value == null || value is DBNull) continue;
                    bool numeric = value is double or float or decimal or int or long or short;
                    var text = value is bool b ? (b ? "1" : "0") : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                    cells.Add(new MiniXlsx.CellText(ColumnName(column), rowNumber, text,
                        numeric ? "" : value is bool ? "b" : "s"));
                }
                rows.Add(cells);
            }
            return rows;
        } while (reader.NextResult());
        throw new InvalidOperationException($"Worksheet '{sheetName}' was not found.");
    }

    private static string ColumnName(int index)
    {
        var name = "";
        for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
        return name;
    }
}
