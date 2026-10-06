using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace MahodAI.Core.Tests.Estimate.BoqRulesV2;

/// <summary>
/// Test-only reader of an xlsx's cells WITH their formulas, and a small evaluator for the formula subset the BoQ workbook
/// uses (+ - * / ^ =, cell and cross-sheet references, ranges in SUM, ROUND, PI, SQRT, CHOOSE, SUM, IF, N). It proves the
/// written formulas compute the engine's numbers; it does not replace opening the file in Excel.
/// </summary>
internal sealed class XlsxFormulaReader
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PkgRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private sealed record Cell(string? Formula, string? Value, string Type, string? Text);

    private readonly Dictionary<string, Dictionary<string, Cell>> _sheets = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), object> _memo = new();
    public List<string> SheetNames { get; } = new();

    public static XlsxFormulaReader Load(string path)
    {
        var reader = new XlsxFormulaReader();
        using var zip = ZipFile.OpenRead(path);
        XDocument Doc(string name)
        {
            using var s = zip.GetEntry(name)!.Open();
            return XDocument.Load(s);
        }
        var rels = Doc("xl/_rels/workbook.xml.rels").Root!.Elements(PkgRel + "Relationship")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => "xl/" + ((string)e.Attribute("Target")!).TrimStart('/'));
        foreach (var sheet in Doc("xl/workbook.xml").Root!.Element(Main + "sheets")!.Elements(Main + "sheet"))
        {
            var name = (string)sheet.Attribute("name")!;
            reader.SheetNames.Add(name);
            var cells = new Dictionary<string, Cell>(StringComparer.Ordinal);
            foreach (var c in Doc(rels[(string)sheet.Attribute(Rel + "id")!]).Descendants(Main + "c"))
            {
                var type = (string?)c.Attribute("t") ?? "";
                var text = type == "inlineStr" ? string.Concat(c.Descendants(Main + "t").Select(t => t.Value)) : null;
                cells[(string)c.Attribute("r")!] = new Cell(c.Element(Main + "f")?.Value, c.Element(Main + "v")?.Value, type, text);
            }
            reader._sheets[name] = cells;
        }
        return reader;
    }

    /// <summary>Row number of the first text cell in <paramref name="column"/> satisfying the predicate.</summary>
    public int? FindRow(string sheet, string column, Func<string, bool> predicate)
    {
        foreach (var (reference, cell) in _sheets[sheet])
        {
            var letters = new string(reference.TakeWhile(char.IsLetter).ToArray());
            if (letters == column && cell.Text != null && predicate(cell.Text))
                return int.Parse(reference[letters.Length..], CultureInfo.InvariantCulture);
        }
        return null;
    }

    public double Evaluate(string sheet, string reference) => Number(Value(sheet, reference));

    /// <summary>The formula text of a cell (without "="), or null when the cell has no formula.</summary>
    public string? Formula(string sheet, string reference) =>
        _sheets[sheet].TryGetValue(reference.Replace("$", ""), out var cell) ? cell.Formula : null;

    /// <summary>The inline text of a cell, or null when it is not a text cell.</summary>
    public string? Text(string sheet, string reference) =>
        _sheets[sheet].TryGetValue(reference.Replace("$", ""), out var cell) ? cell.Text : null;

    /// <summary>Every inline text of one column of a sheet.</summary>
    public IReadOnlyList<string> ColumnTexts(string sheet, string column) =>
        _sheets[sheet].Where(pair => new string(pair.Key.TakeWhile(char.IsLetter).ToArray()) == column && pair.Value.Text != null)
            .Select(pair => pair.Value.Text!).ToList();

    private object Value(string sheet, string reference)
    {
        reference = reference.Replace("$", "");
        if (_memo.TryGetValue((sheet, reference), out var cached)) return cached;
        object result;
        if (!_sheets[sheet].TryGetValue(reference, out var cell)) result = "";
        else if (cell.Formula != null) result = new Parser(this, sheet, cell.Formula).Run();
        else if (cell.Text != null) result = cell.Text;
        else result = double.TryParse(cell.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (object)"";
        _memo[(sheet, reference)] = result;
        return result;
    }

    private static double Number(object value) => value switch
    {
        double d => d,
        bool b => b ? 1 : 0,
        string s when s.Length == 0 => 0,
        string s => throw new InvalidDataException($"text '{s}' used as a number"),
        _ => 0,
    };

    private sealed class Parser
    {
        private readonly XlsxFormulaReader _book;
        private readonly string _sheet;
        private readonly string _text;
        private int _pos;

        public Parser(XlsxFormulaReader book, string sheet, string text) { _book = book; _sheet = sheet; _text = text; }

        public object Run()
        {
            var value = Comparison();
            Skip();
            if (_pos != _text.Length) throw new InvalidDataException($"unparsed formula tail at {_pos}: {_text}");
            return value;
        }

        private void Skip() { while (_pos < _text.Length && _text[_pos] == ' ') _pos++; }
        private bool Eat(char c) { Skip(); if (_pos < _text.Length && _text[_pos] == c) { _pos++; return true; } return false; }

        private object Comparison()
        {
            var left = Additive();
            if (Eat('='))
            {
                var right = Additive();
                return left is string || right is string
                    ? string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal)
                    : Math.Abs(Number(left) - Number(right)) < 1e-12;
            }
            return left;
        }

        private object Additive()
        {
            var value = Term();
            while (true)
            {
                if (Eat('+')) value = Number(value) + Number(Term());
                else if (Eat('-')) value = Number(value) - Number(Term());
                else return value;
            }
        }

        private object Term()
        {
            var value = Power();
            while (true)
            {
                if (Eat('*')) value = Number(value) * Number(Power());
                else if (Eat('/')) value = Number(value) / Number(Power());
                else return value;
            }
        }

        private object Power()
        {
            var value = Unary();
            while (Eat('^')) value = Math.Pow(Number(value), Number(Unary()));
            return value;
        }

        private object Unary() => Eat('-') ? -Number(Unary()) : Primary();

        private object Primary()
        {
            Skip();
            if (Eat('(')) { var inner = Comparison(); Expect(')'); return inner; }
            var c = _text[_pos];
            if (c == '"')
            {
                var sb = new StringBuilder();
                _pos++;
                while (true)
                {
                    if (_text[_pos] == '"')
                    {
                        if (_pos + 1 < _text.Length && _text[_pos + 1] == '"') { sb.Append('"'); _pos += 2; continue; }
                        _pos++;
                        return sb.ToString();
                    }
                    sb.Append(_text[_pos++]);
                }
            }
            if (char.IsDigit(c) || c == '.')
            {
                var start = _pos;
                while (_pos < _text.Length && (char.IsDigit(_text[_pos]) || _text[_pos] is '.' or 'E' or 'e' ||
                       ((_text[_pos] is '+' or '-') && _pos > start && _text[_pos - 1] is 'E' or 'e'))) _pos++;
                return double.Parse(_text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            if (c == '\'')
            {
                var end = _text.IndexOf('\'', _pos + 1);
                var sheet = _text[(_pos + 1)..end];
                _pos = end + 1;
                Expect('!');
                return _book.Value(sheet, ReadReference());
            }
            var word = ReadWord();
            Skip();
            if (_pos < _text.Length && _text[_pos] == '(')
                return Function(word);
            // A local reference, possibly a range (only inside SUM, handled there).
            return _book.Value(_sheet, word);
        }

        private string ReadWord()
        {
            var start = _pos;
            while (_pos < _text.Length && (char.IsLetterOrDigit(_text[_pos]) || _text[_pos] == '$')) _pos++;
            if (start == _pos) throw new InvalidDataException($"unexpected '{_text[_pos]}' in {_text}");
            return _text[start.._pos];
        }

        private string ReadReference() => ReadWord();

        private void Expect(char c)
        {
            if (!Eat(c)) throw new InvalidDataException($"expected '{c}' at {_pos} in {_text}");
        }

        private object Function(string name)
        {
            Expect('(');
            switch (name.ToUpperInvariant())
            {
                case "PI":
                    Expect(')');
                    return Math.PI;
                case "SQRT":
                {
                    var x = Number(Comparison());
                    Expect(')');
                    return Math.Sqrt(x);
                }
                case "N":
                {
                    // Excel N(): numbers stay, TRUE/FALSE → 1/0, text ("לא נמדד") and empty → 0 (BoQ v4 crossings sheet).
                    var value = Comparison();
                    Expect(')');
                    return value switch { double d => d, bool b => b ? 1.0 : 0.0, _ => 0.0 };
                }
                case "ROUND":
                {
                    var x = Number(Comparison());
                    Expect(',');
                    var digits = (int)Number(Comparison());
                    Expect(')');
                    return Math.Round(x, digits, MidpointRounding.AwayFromZero);
                }
                case "CHOOSE":
                {
                    var index = (int)Number(Comparison());
                    var options = new List<object>();
                    while (Eat(',')) options.Add(Comparison());
                    Expect(')');
                    return options[index - 1];
                }
                case "IF":
                {
                    var condition = Comparison();
                    Expect(',');
                    var whenTrue = Comparison();
                    Expect(',');
                    var whenFalse = Comparison();
                    Expect(')');
                    var truth = condition is bool b ? b : Number(condition) != 0;
                    return truth ? whenTrue : whenFalse;
                }
                case "SUM":
                {
                    var total = 0.0;
                    do
                    {
                        Skip();
                        var save = _pos;
                        var first = _text[_pos] == '\'' ? null : ReadWord();
                        if (first != null && _pos < _text.Length && _text[_pos] == ':')
                        {
                            _pos++;
                            var last = ReadWord();
                            foreach (var reference in Range(first, last))
                                if (_book.Value(_sheet, reference) is double d) total += d;
                        }
                        else
                        {
                            _pos = save;
                            var value = Comparison();
                            if (value is double d) total += d;
                        }
                    }
                    while (Eat(','));
                    Expect(')');
                    return total;
                }
                default:
                    throw new InvalidDataException($"unsupported function {name} in {_text}");
            }
        }

        private static IEnumerable<string> Range(string first, string last)
        {
            (string Col, int Row) Split(string r)
            {
                r = r.Replace("$", "");
                var letters = new string(r.TakeWhile(char.IsLetter).ToArray());
                return (letters, int.Parse(r[letters.Length..], CultureInfo.InvariantCulture));
            }
            var a = Split(first);
            var b = Split(last);
            if (a.Col != b.Col) throw new InvalidDataException("only single-column ranges are used by the BoQ workbook");
            for (var row = Math.Min(a.Row, b.Row); row <= Math.Max(a.Row, b.Row); row++) yield return a.Col + row.ToString(CultureInfo.InvariantCulture);
        }
    }
}
