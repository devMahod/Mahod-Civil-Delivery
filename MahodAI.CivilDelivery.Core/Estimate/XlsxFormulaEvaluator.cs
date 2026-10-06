using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>What a worksheet cell holds before calculation.</summary>
    public enum XlsxSourceKind { Empty, Number, Text, Formula }

    /// <summary>One source cell: empty, a number, a text, or a formula (its text without the leading '=').</summary>
    public readonly record struct XlsxSourceCell(XlsxSourceKind Kind, double Number, string? Text)
    {
        public static XlsxSourceCell Empty => default;
        public static XlsxSourceCell FromNumber(double value) => new(XlsxSourceKind.Number, value, null);
        public static XlsxSourceCell FromText(string text) => new(XlsxSourceKind.Text, 0, text ?? string.Empty);
        public static XlsxSourceCell FromFormula(string formula) => new(XlsxSourceKind.Formula, 0, formula ?? string.Empty);
    }

    /// <summary>A formula cell to evaluate: its sheet, 1-based row and column, and formula text (without '=').</summary>
    public readonly record struct XlsxFormulaCell(string Sheet, int Row, int Column, string Formula)
    {
        /// <summary>The A1 reference without '$' (for example "D12").</summary>
        public string Reference => Column is >= 1 and <= XlsxFormulaEvaluator.MaxColumn && Row is >= 1 and <= XlsxFormulaEvaluator.MaxRow
            ? XlsxFormulaEvaluator.ColumnName(Column) + Row.ToString(CultureInfo.InvariantCulture)
            : "R" + Row.ToString(CultureInfo.InvariantCulture) + "C" + Column.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The workbook the evaluator reads: sheet names in workbook order, every formula cell, and any cell by address.
    /// Implemented by <see cref="MiniXlsx"/>'s in-memory model and by <see cref="XlsxDictionaryCellSource"/>.
    /// </summary>
    public interface IXlsxCellSource
    {
        IReadOnlyList<string> SheetNames { get; }
        IEnumerable<XlsxFormulaCell> FormulaCells();
        /// <summary>The cell at a 1-based row and column; <see cref="XlsxSourceCell.Empty"/> when nothing is there.</summary>
        XlsxSourceCell GetCell(string sheet, int row, int column);
    }

    public enum XlsxValueKind { Number, Text, Boolean }

    /// <summary>The value Excel shows for a formula cell (what is written as the cell's cached &lt;v&gt;).</summary>
    public readonly record struct XlsxCachedValue(XlsxValueKind Kind, double Number, string Text, bool IsTrue)
    {
        public static XlsxCachedValue OfNumber(double value) => new(XlsxValueKind.Number, value, string.Empty, false);
        public static XlsxCachedValue OfText(string value) => new(XlsxValueKind.Text, 0, value ?? string.Empty, false);
        public static XlsxCachedValue OfBoolean(bool value) => new(XlsxValueKind.Boolean, value ? 1 : 0, string.Empty, value);
    }

    /// <summary>A formula cell that gets no cached value, and why (error, unsupported syntax/function, cycle, or a dependency).</summary>
    public sealed record XlsxUnevaluableCell(string Sheet, string Reference, string Reason);

    /// <summary>The result of evaluating every formula of a workbook once.</summary>
    public sealed class XlsxFormulaEvaluation
    {
        private readonly Dictionary<(string Sheet, string Reference), XlsxCachedValue> _values;

        internal XlsxFormulaEvaluation(Dictionary<(string Sheet, string Reference), XlsxCachedValue> values,
            IReadOnlyList<XlsxUnevaluableCell> unevaluable, int formulaCount, string? failure = null)
        {
            _values = values;
            Unevaluable = unevaluable;
            FormulaCount = formulaCount;
            Failure = failure;
        }

        /// <summary>No formulas: nothing to cache.</summary>
        public static XlsxFormulaEvaluation None { get; } =
            new(new Dictionary<(string Sheet, string Reference), XlsxCachedValue>(), Array.Empty<XlsxUnevaluableCell>(), 0);

        internal static XlsxFormulaEvaluation Aborted(IReadOnlyList<XlsxUnevaluableCell> formulas, string failure) =>
            new(new Dictionary<(string Sheet, string Reference), XlsxCachedValue>(), formulas, formulas.Count, failure);

        /// <summary>How many formula cells the workbook has.</summary>
        public int FormulaCount { get; }

        /// <summary>Cached value per (sheet, A1 reference without '$').</summary>
        public IReadOnlyDictionary<(string Sheet, string Reference), XlsxCachedValue> Values => _values;

        /// <summary>Formula cells without a cached value, in workbook order (sheet, row, column).</summary>
        public IReadOnlyList<XlsxUnevaluableCell> Unevaluable { get; }

        /// <summary>Set only when the evaluation as a whole stopped; every formula is then listed as unevaluable.</summary>
        public string? Failure { get; }

        public bool TryGetValue(string sheet, string reference, out XlsxCachedValue value) =>
            _values.TryGetValue((sheet, reference), out value);
    }

    /// <summary>A simple in-memory cell source (tests, oracles, callers without a MiniXlsx model).</summary>
    public sealed class XlsxDictionaryCellSource : IXlsxCellSource
    {
        private readonly List<string> _sheets = new();
        private readonly Dictionary<string, Dictionary<long, XlsxSourceCell>> _cells = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<long>> _order = new(StringComparer.Ordinal);

        public IReadOnlyList<string> SheetNames => _sheets;

        /// <summary>Adds an (empty) sheet at the end of the workbook; adding an existing name again does nothing.</summary>
        public XlsxDictionaryCellSource AddSheet(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A sheet needs a name.", nameof(name));
            if (_cells.ContainsKey(name)) return this;
            if (_sheets.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Sheet names are case-insensitive in formulas; '{name}' repeats another sheet.", nameof(name));
            _sheets.Add(name);
            _cells[name] = new Dictionary<long, XlsxSourceCell>();
            _order[name] = new List<long>();
            return this;
        }

        /// <summary>Sets one cell by A1 reference ('$' allowed); the sheet is added when new.</summary>
        public XlsxDictionaryCellSource Set(string sheet, string reference, XlsxSourceCell cell)
        {
            AddSheet(sheet);
            if (!XlsxFormulaEvaluator.TryParseReference(reference, out var row, out var column))
                throw new ArgumentException($"'{reference}' is not a cell reference.", nameof(reference));
            var key = XlsxFormulaEvaluator.Key(row, column);
            var cells = _cells[sheet];
            if (!cells.ContainsKey(key)) _order[sheet].Add(key);
            cells[key] = cell;
            return this;
        }

        public IEnumerable<XlsxFormulaCell> FormulaCells()
        {
            foreach (var sheet in _sheets)
            {
                var cells = _cells[sheet];
                foreach (var key in _order[sheet])
                {
                    var cell = cells[key];
                    if (cell.Kind == XlsxSourceKind.Formula)
                        yield return new XlsxFormulaCell(sheet, (int)(key >> 16), (int)(key & 0xFFFF), cell.Text ?? string.Empty);
                }
            }
        }

        public XlsxSourceCell GetCell(string sheet, int row, int column) =>
            _cells.TryGetValue(sheet, out var cells) && cells.TryGetValue(XlsxFormulaEvaluator.Key(row, column), out var cell)
                ? cell
                : XlsxSourceCell.Empty;
    }

    /// <summary>
    /// Computes, at write time, the value Excel would show for every formula a Mahod workbook writer produces, so the
    /// file carries cached values. Without them Teams preview, Excel Protected View and phone viewers show EMPTY
    /// quantities (Natali, 29.09.2026); Excel itself still recalculates on open (fullCalcOnLoad stays).
    ///
    /// Scope is exactly what the writers use (EstimateExcelWriter, EngineerBoqDraftExcelWriter, BoqRulesWorkbookWriter and
    /// the v7 reference workbook): numbers, strings ("" escapes), TRUE/FALSE, A1 references with '$' and an optional sheet
    /// ('quoted'! or plain!), rectangular ranges, + - * / ^ &amp; = &lt;&gt; &lt; &gt; &lt;= &gt;=, unary + / - (and --),
    /// parentheses; ROUND IF CHOOSE PI SQRT SUM SUMPRODUCT NOT ISNUMBER SUMIF COUNTIF ABS MIN MAX AND OR LEN TRIM NA N.
    /// Excel semantics: an empty cell is 0 in arithmetic and "" in comparison; ROUND goes through 15 significant digits
    /// and rounds half away from zero in decimal (ROUND(2.675,2) = 2.68); numbers compare at 15 significant digits;
    /// text compares case-insensitively; IF and CHOOSE evaluate only the chosen branch.
    ///
    /// Never guesses: an Excel error (#DIV/0!, #VALUE!, #N/A, #NUM!, #REF!, #NAME?), any unsupported syntax or function,
    /// a locale-dependent conversion (text to number, fraction to text, text ordering) or a circular reference makes that
    /// formula - and every formula that reads it - "not evaluable": it gets no cached value (Excel computes it on open).
    /// Evaluation is memoized, ordered by an iterative dependency walk (no recursion across cells), and never throws for
    /// a formula's content.
    /// </summary>
    public static class XlsxFormulaEvaluator
    {
        public const int MaxRow = 1048576;
        public const int MaxColumn = 16384;
        /// <summary>Largest range the evaluator materializes; larger ranges make the formula not evaluable.</summary>
        public const int MaxRangeCells = 1 << 20;
        /// <summary>Excel's own limit on nested functions; deeper nesting is refused before it can exhaust the stack.</summary>
        private const int MaxNesting = 64;

        private static readonly Dictionary<string, (int Min, int Max)> Functions = new(StringComparer.Ordinal)
        {
            ["ROUND"] = (2, 2), ["IF"] = (2, 3), ["CHOOSE"] = (2, 255), ["PI"] = (0, 0), ["SQRT"] = (1, 1),
            ["SUM"] = (1, 255), ["SUMPRODUCT"] = (1, 255), ["NOT"] = (1, 1), ["ISNUMBER"] = (1, 1),
            ["SUMIF"] = (2, 3), ["COUNTIF"] = (2, 2), ["ABS"] = (1, 1), ["MIN"] = (1, 255), ["MAX"] = (1, 255),
            ["AND"] = (1, 255), ["OR"] = (1, 255), ["LEN"] = (1, 1), ["TRIM"] = (1, 1), ["NA"] = (0, 0),
            ["N"] = (1, 1),
        };

        private static readonly char[] CriteriaSpecial = { '*', '?', '~' };

        /// <summary>Function names the evaluator understands (anything else makes a formula not evaluable).</summary>
        public static IReadOnlyCollection<string> SupportedFunctions => Functions.Keys;

        /// <summary>Evaluates every formula cell of <paramref name="source"/> once.</summary>
        public static XlsxFormulaEvaluation Evaluate(IXlsxCellSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return new Run(source).Execute();
        }

        /// <summary>Column letters of a 1-based column number (1 = A, 27 = AA).</summary>
        public static string ColumnName(int column)
        {
            if (column < 1 || column > MaxColumn) throw new ArgumentOutOfRangeException(nameof(column));
            var name = string.Empty;
            while (column > 0)
            {
                name = (char)('A' + (column - 1) % 26) + name;
                column = (column - 1) / 26;
            }
            return name;
        }

        /// <summary>Parses an A1 reference ('$' allowed, letters in any case) into a 1-based row and column within Excel's grid.</summary>
        public static bool TryParseReference(string? text, out int row, out int column)
        {
            row = 0;
            column = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var i = 0;
            if (text[i] == '$') i++;
            var letters = 0;
            var col = 0;
            while (i < text.Length && char.IsAsciiLetter(text[i]) && letters < 4)
            {
                col = col * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
                i++;
                letters++;
            }
            if (letters is 0 or > 3 || col > MaxColumn) return false;
            if (i < text.Length && text[i] == '$') i++;
            if (i >= text.Length || text[i] < '1' || text[i] > '9') return false;
            long r = 0;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                r = r * 10 + (text[i] - '0');
                if (r > MaxRow) return false;
                i++;
            }
            if (i != text.Length) return false;
            row = (int)r;
            column = col;
            return true;
        }

        internal static long Key(int row, int column) => ((long)row << 16) | (uint)column;

        private static NotEvaluableException Unsupported(string reason) => new(reason);

        private static double Finite(double value) =>
            double.IsFinite(value) ? value : throw Unsupported("#NUM! (the result is outside Excel's number range)");

        /// <summary>The value as Excel holds it for display and comparison: 15 significant digits.</summary>
        private static double Fifteen(double value) =>
            value == 0 || !double.IsFinite(value)
                ? value
                : double.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>
        /// Excel's ROUND: the number through 15 significant digits, then rounded half away from zero in decimal, so
        /// ROUND(2.675,2) = 2.68, ROUND(1.005,2) = 1.01 and ROUND(-2.5,0) = -3. The digit count is truncated toward zero.
        /// </summary>
        private static double ExcelRound(double value, double digits)
        {
            if (!double.IsFinite(value) || !double.IsFinite(digits)) throw Unsupported("#NUM! (ROUND of a non-finite number)");
            var places = Math.Truncate(digits);
            if (value == 0) return 0;
            var magnitude = Math.Abs(value);
            if (magnitude < 1e-14)
            {
                if (places <= 13) return 0;
                throw Unsupported("ROUND of a number below 1E-14 to more than 13 decimals");
            }
            if (places >= 0 && magnitude >= 1e15) return Fifteen(value);
            if (magnitude >= 1e27) throw Unsupported("ROUND of a number above 1E+27 to tens or more");
            var exact = decimal.Parse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            decimal rounded;
            if (places >= 28) rounded = exact;
            else if (places >= 0) rounded = Math.Round(exact, (int)places, MidpointRounding.AwayFromZero);
            else if (places < -27) return 0;
            else
            {
                var scale = 1m;
                for (var i = 0; i < (int)-places; i++) scale *= 10m;
                rounded = Math.Round(exact / scale, 0, MidpointRounding.AwayFromZero) * scale;
            }
            return double.Parse(rounded.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private sealed class NotEvaluableException : Exception
        {
            public NotEvaluableException(string reason) : base(reason) { }
        }

        // ------------------------------------------------------------------ values

        private enum K : byte { Empty, Number, Text, Logical, Matrix }

        /// <summary>A scalar (empty, number, text, logical) or a matrix of scalars (inside SUMPRODUCT only).</summary>
        private readonly struct Value
        {
            private readonly string? _text;
            private readonly Value[]? _items;

            private Value(K kind, double number, string? text, Value[]? items, int rows, int columns)
            {
                Kind = kind;
                Number = number;
                _text = text;
                _items = items;
                Rows = rows;
                Columns = columns;
            }

            public K Kind { get; }
            /// <summary>The number; 1 / 0 for a logical value.</summary>
            public double Number { get; }
            public string Text => _text ?? string.Empty;
            public Value[] Items => _items ?? System.Array.Empty<Value>();
            public int Rows { get; }
            public int Columns { get; }

            public static Value Empty => default;
            public static Value Num(double number) => new(K.Number, number, null, null, 0, 0);
            public static Value Str(string text) => new(K.Text, 0, text, null, 0, 0);
            public static Value Logical(bool value) => new(K.Logical, value ? 1 : 0, null, null, 0, 0);
            public static Value Matrix(Value[] items, int rows, int columns) => new(K.Matrix, 0, null, items, rows, columns);
        }

        // --------------------------------------------------------------------- AST

        private abstract record Expr;
        private sealed record NumberExpr(double Value) : Expr;
        private sealed record TextExpr(string Value) : Expr;
        private sealed record BoolExpr(bool Value) : Expr;
        private sealed record RefExpr(string Sheet, int Row, int Column) : Expr;
        private sealed record RangeExpr(string Sheet, int Row1, int Column1, int Row2, int Column2) : Expr
        {
            public int Rows => Row2 - Row1 + 1;
            public int Columns => Column2 - Column1 + 1;
            public long CellCount => (long)Rows * Columns;
        }
        private sealed record UnaryExpr(bool Negate, Expr Operand) : Expr;
        /// <summary>Left-associative operators of one precedence level, kept flat so long sums never recurse deeply.</summary>
        private sealed record ChainExpr(Expr First, List<(string Operator, Expr Operand)> Rest) : Expr;
        private sealed record CallExpr(string Name, List<Expr> Arguments) : Expr;

        // ------------------------------------------------------------------ parser

        /// <summary>Recursive descent in Excel's precedence: comparison &lt; &amp; &lt; + - &lt; * / &lt; ^ &lt; unary - &lt; : .</summary>
        private sealed class Parser
        {
            private readonly string _text;
            private readonly string _sheet;
            private readonly Func<string, string> _resolveSheet;
            private int _position;
            private int _depth;

            public Parser(string text, string sheet, Func<string, string> resolveSheet)
            {
                _text = text;
                _sheet = sheet;
                _resolveSheet = resolveSheet;
            }

            public Expr Parse()
            {
                if (string.IsNullOrWhiteSpace(_text)) throw Unsupported("empty formula");
                var expression = Comparison();
                SkipSpace();
                if (_position < _text.Length) throw Unsupported($"unsupported syntax at '{Excerpt()}'");
                return expression;
            }

            private string Excerpt() => _text.Substring(_position, Math.Min(24, _text.Length - _position));

            private void SkipSpace()
            {
                while (_position < _text.Length && _text[_position] is ' ' or '\t' or '\r' or '\n') _position++;
            }

            private void Enter()
            {
                if (++_depth > MaxNesting) throw Unsupported($"the formula nests deeper than {MaxNesting} levels");
            }

            private void Expect(char symbol)
            {
                SkipSpace();
                if (_position >= _text.Length || _text[_position] != symbol) throw Unsupported($"'{symbol}' expected");
                _position++;
            }

            private Expr Chain(Func<Expr> operand, Func<string?> nextOperator)
            {
                var first = operand();
                List<(string Operator, Expr Operand)>? rest = null;
                while (nextOperator() is { } symbol)
                    (rest ??= new List<(string Operator, Expr Operand)>()).Add((symbol, operand()));
                return rest == null ? first : new ChainExpr(first, rest);
            }

            private Expr Comparison() => Chain(Concatenation, ComparisonOperator);
            private Expr Concatenation() => Chain(Additive, () => Symbol('&', '&'));
            private Expr Additive() => Chain(Term, () => Symbol('+', '-'));
            private Expr Term() => Chain(Power, () => Symbol('*', '/'));
            private Expr Power() => Chain(Unary, () => Symbol('^', '^'));

            private string? Symbol(char first, char second)
            {
                SkipSpace();
                if (_position < _text.Length && (_text[_position] == first || _text[_position] == second))
                    return _text[_position++].ToString();
                return null;
            }

            private string? ComparisonOperator()
            {
                SkipSpace();
                if (_position >= _text.Length) return null;
                var c = _text[_position];
                var next = _position + 1 < _text.Length ? _text[_position + 1] : '\0';
                switch (c)
                {
                    case '=':
                        _position++;
                        return "=";
                    case '<':
                        if (next == '=') { _position += 2; return "<="; }
                        if (next == '>') { _position += 2; return "<>"; }
                        _position++;
                        return "<";
                    case '>':
                        if (next == '=') { _position += 2; return ">="; }
                        _position++;
                        return ">";
                    default:
                        return null;
                }
            }

            // Excel: negation binds tighter than '^' (-2^2 = 4).
            private Expr Unary()
            {
                SkipSpace();
                if (_position < _text.Length && _text[_position] is '-' or '+')
                {
                    var negate = _text[_position] == '-';
                    _position++;
                    Enter();
                    var operand = Unary();
                    _depth--;
                    return new UnaryExpr(negate, operand);
                }
                return Primary();
            }

            private Expr Primary()
            {
                SkipSpace();
                if (_position >= _text.Length) throw Unsupported("the formula ends where a value is expected");
                var c = _text[_position];
                if (c == '(')
                {
                    _position++;
                    Enter();
                    var inner = Comparison();
                    Expect(')');
                    _depth--;
                    return inner;
                }
                if (c == '"') return new TextExpr(ReadQuoted('"'));
                if (char.IsAsciiDigit(c) || c == '.') return new NumberExpr(ReadNumber());
                if (c == '\'')
                {
                    var sheet = _resolveSheet(ReadQuoted('\''));
                    if (_position >= _text.Length || _text[_position] != '!') throw Unsupported("a quoted sheet name without '!'");
                    _position++;
                    return Reference(sheet, ReadWord());
                }
                if (IsWordCharacter(c))
                {
                    var word = ReadWord();
                    if (_position < _text.Length && _text[_position] == '(') return Function(word);
                    if (_position < _text.Length && _text[_position] == '!')
                    {
                        if (word.Contains('$')) throw Unsupported($"'{word}' is not a sheet name");
                        _position++;
                        return Reference(_resolveSheet(word), ReadWord());
                    }
                    if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return new BoolExpr(true);
                    if (word.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return new BoolExpr(false);
                    return Reference(_sheet, word);
                }
                throw Unsupported($"unsupported syntax at '{Excerpt()}'");
            }

            private static bool IsWordCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '$';

            private string ReadWord()
            {
                var start = _position;
                while (_position < _text.Length && IsWordCharacter(_text[_position])) _position++;
                if (start == _position) throw Unsupported($"a cell reference is expected at '{Excerpt()}'");
                return _text.Substring(start, _position - start);
            }

            /// <summary>A "text" constant or a 'sheet name'; the quote character is escaped by doubling it.</summary>
            private string ReadQuoted(char quote)
            {
                var builder = new StringBuilder();
                _position++;
                while (true)
                {
                    if (_position >= _text.Length) throw Unsupported(quote == '"' ? "a text constant is not closed" : "a sheet name is not closed");
                    var c = _text[_position++];
                    if (c == quote)
                    {
                        if (_position < _text.Length && _text[_position] == quote)
                        {
                            builder.Append(quote);
                            _position++;
                            continue;
                        }
                        return builder.ToString();
                    }
                    builder.Append(c);
                }
            }

            private double ReadNumber()
            {
                var start = _position;
                while (_position < _text.Length && char.IsAsciiDigit(_text[_position])) _position++;
                if (_position < _text.Length && _text[_position] == '.')
                {
                    _position++;
                    while (_position < _text.Length && char.IsAsciiDigit(_text[_position])) _position++;
                }
                if (_position < _text.Length && _text[_position] is 'e' or 'E')
                {
                    var mark = _position++;
                    if (_position < _text.Length && _text[_position] is '+' or '-') _position++;
                    if (_position < _text.Length && char.IsAsciiDigit(_text[_position]))
                        while (_position < _text.Length && char.IsAsciiDigit(_text[_position])) _position++;
                    else
                        _position = mark;
                }
                var token = _text.Substring(start, _position - start);
                if (!double.TryParse(token, NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) ||
                    !double.IsFinite(value))
                    throw Unsupported($"'{token}' is not a number");
                return value;
            }

            private Expr Reference(string sheet, string word)
            {
                if (!TryParseReference(word, out var row, out var column))
                    throw Unsupported($"'{word}' is not a cell reference (#NAME? or #REF!)");
                if (_position < _text.Length && _text[_position] == ':')
                {
                    _position++;
                    var end = ReadWord();
                    if (!TryParseReference(end, out var row2, out var column2))
                        throw Unsupported($"'{end}' does not end a cell range");
                    return new RangeExpr(sheet, Math.Min(row, row2), Math.Min(column, column2), Math.Max(row, row2), Math.Max(column, column2));
                }
                return new RefExpr(sheet, row, column);
            }

            private Expr Function(string word)
            {
                var name = word.ToUpperInvariant();
                if (!Functions.TryGetValue(name, out var arity)) throw Unsupported($"function {name} is not supported");
                _position++; // '('
                Enter();
                var arguments = new List<Expr>();
                SkipSpace();
                if (_position < _text.Length && _text[_position] == ')')
                {
                    _position++;
                }
                else
                {
                    while (true)
                    {
                        SkipSpace();
                        if (_position < _text.Length && _text[_position] is ',' or ')') throw Unsupported($"{name} has an empty argument");
                        arguments.Add(Comparison());
                        SkipSpace();
                        if (_position >= _text.Length) throw Unsupported($"{name}( is not closed");
                        var separator = _text[_position++];
                        if (separator == ',') continue;
                        if (separator == ')') break;
                        throw Unsupported($"unexpected '{separator}' in the arguments of {name}");
                    }
                }
                _depth--;
                if (arguments.Count < arity.Min || arguments.Count > arity.Max)
                    throw Unsupported($"{name} with {arguments.Count} arguments");
                return new CallExpr(name, arguments);
            }
        }

        // --------------------------------------------------------------------- run

        private sealed class Node
        {
            public Node(XlsxFormulaCell cell) => Cell = cell;
            public XlsxFormulaCell Cell { get; }
            public Expr? Expression;
            public List<Node>? Dependencies;
            public string? Failure;
            /// <summary>0 = not visited, 1 = on the dependency walk, 2 = done.</summary>
            public byte State;
            public bool Circular;
            public bool Evaluated;
            public Value Result;
        }

        private sealed class Run
        {
            private readonly IXlsxCellSource _source;
            private readonly Dictionary<string, string> _sheetNames = new(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _sheetOrder = new(StringComparer.Ordinal);
            private readonly Dictionary<(string Sheet, int Row, int Column), Node> _nodes = new();
            private readonly List<Node> _order = new();
            // sheet -> column -> sorted rows of its formula cells (range dependencies without scanning every cell).
            private readonly Dictionary<string, Dictionary<int, List<int>>> _formulaRows = new(StringComparer.Ordinal);

            public Run(IXlsxCellSource source)
            {
                _source = source;
                var index = 0;
                foreach (var name in source.SheetNames ?? Array.Empty<string>())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    _sheetNames.TryAdd(name, name);
                    _sheetOrder.TryAdd(name, index++);
                }
            }

            public XlsxFormulaEvaluation Execute()
            {
                foreach (var cell in _source.FormulaCells() ?? Enumerable.Empty<XlsxFormulaCell>())
                {
                    if (cell.Sheet == null) continue;
                    var key = (cell.Sheet, cell.Row, cell.Column);
                    if (_nodes.ContainsKey(key)) continue;
                    var node = new Node(cell);
                    _nodes[key] = node;
                    _order.Add(node);
                    if (!_formulaRows.TryGetValue(cell.Sheet, out var columns))
                        _formulaRows[cell.Sheet] = columns = new Dictionary<int, List<int>>();
                    if (!columns.TryGetValue(cell.Column, out var rows))
                        columns[cell.Column] = rows = new List<int>();
                    rows.Add(cell.Row);
                }
                if (_order.Count == 0) return XlsxFormulaEvaluation.None;
                foreach (var columns in _formulaRows.Values)
                    foreach (var rows in columns.Values)
                        rows.Sort();
                foreach (var node in _order) Bind(node);
                Walk();
                return Collect();
            }

            private string ResolveSheet(string name) =>
                _sheetNames.TryGetValue(name, out var actual) ? actual : throw Unsupported($"#REF! (no sheet named '{name}')");

            private void Bind(Node node)
            {
                var cell = node.Cell;
                if (!_sheetOrder.ContainsKey(cell.Sheet) || cell.Row < 1 || cell.Row > MaxRow || cell.Column < 1 || cell.Column > MaxColumn)
                {
                    node.Failure = "the formula cell has no valid address in the workbook";
                    return;
                }
                try
                {
                    var expression = new Parser(cell.Formula ?? string.Empty, cell.Sheet, ResolveSheet).Parse();
                    var dependencies = new List<Node>();
                    CollectDependencies(expression, dependencies);
                    node.Expression = expression;
                    node.Dependencies = dependencies;
                }
                catch (NotEvaluableException ex)
                {
                    node.Failure = ex.Message;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    node.Failure = "formula not understood (" + ex.GetType().Name + ")";
                }
            }

            /// <summary>Every formula cell the expression can read (both IF branches included: a static superset).</summary>
            private void CollectDependencies(Expr expression, List<Node> dependencies)
            {
                switch (expression)
                {
                    case RefExpr reference:
                        if (_nodes.TryGetValue((reference.Sheet, reference.Row, reference.Column), out var node)) dependencies.Add(node);
                        break;
                    case RangeExpr range:
                        if (range.CellCount > MaxRangeCells) throw Unsupported($"a range of {range.CellCount} cells (more than {MaxRangeCells})");
                        if (!_formulaRows.TryGetValue(range.Sheet, out var columns)) break;
                        foreach (var (column, rows) in columns)
                        {
                            if (column < range.Column1 || column > range.Column2) continue;
                            var start = rows.BinarySearch(range.Row1);
                            if (start < 0) start = ~start;
                            for (var i = start; i < rows.Count && rows[i] <= range.Row2; i++)
                                if (_nodes.TryGetValue((range.Sheet, rows[i], column), out var member)) dependencies.Add(member);
                        }
                        break;
                    case UnaryExpr unary:
                        CollectDependencies(unary.Operand, dependencies);
                        break;
                    case ChainExpr chain:
                        CollectDependencies(chain.First, dependencies);
                        foreach (var (_, operand) in chain.Rest) CollectDependencies(operand, dependencies);
                        break;
                    case CallExpr call:
                        foreach (var argument in call.Arguments) CollectDependencies(argument, dependencies);
                        break;
                }
            }

            /// <summary>Iterative depth-first walk: every formula is evaluated after the formulas it reads.</summary>
            private void Walk()
            {
                var path = new List<Node>();
                var next = new List<int>();
                foreach (var root in _order)
                {
                    if (root.State != 0) continue;
                    root.State = 1;
                    path.Add(root);
                    next.Add(0);
                    while (path.Count > 0)
                    {
                        var top = path.Count - 1;
                        var node = path[top];
                        var dependencies = node.Dependencies;
                        if (dependencies != null && next[top] < dependencies.Count)
                        {
                            var dependency = dependencies[next[top]];
                            next[top]++;
                            if (dependency.State == 0)
                            {
                                dependency.State = 1;
                                path.Add(dependency);
                                next.Add(0);
                            }
                            continue;
                        }
                        path.RemoveAt(top);
                        next.RemoveAt(top);
                        Finish(node);
                    }
                }
            }

            private void Finish(Node node)
            {
                // A dependency still on the walk is an ancestor: a cycle. A cycle taints everything that reads it.
                if (node.Dependencies != null)
                    foreach (var dependency in node.Dependencies)
                        if (dependency.State == 1 || dependency.Circular)
                        {
                            node.Circular = true;
                            break;
                        }
                node.State = 2;
                if (node.Circular)
                {
                    node.Failure ??= "circular reference";
                    return;
                }
                if (node.Failure != null || node.Expression == null)
                {
                    node.Failure ??= "formula not parsed";
                    return;
                }
                try
                {
                    node.Result = Top(node.Expression);
                    node.Evaluated = true;
                }
                catch (NotEvaluableException ex)
                {
                    node.Failure = ex.Message;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    node.Failure = "evaluation error (" + ex.GetType().Name + ")";
                }
            }

            private XlsxFormulaEvaluation Collect()
            {
                var values = new Dictionary<(string Sheet, string Reference), XlsxCachedValue>();
                var failed = new List<Node>();
                foreach (var node in _order)
                {
                    if (!node.Evaluated)
                    {
                        failed.Add(node);
                        continue;
                    }
                    var value = node.Result;
                    values[(node.Cell.Sheet, node.Cell.Reference)] = value.Kind switch
                    {
                        K.Text => XlsxCachedValue.OfText(value.Text),
                        K.Logical => XlsxCachedValue.OfBoolean(value.Number != 0),
                        _ => XlsxCachedValue.OfNumber(value.Number),
                    };
                }
                var unevaluable = failed
                    .OrderBy(node => _sheetOrder.TryGetValue(node.Cell.Sheet, out var index) ? index : int.MaxValue)
                    .ThenBy(node => node.Cell.Row)
                    .ThenBy(node => node.Cell.Column)
                    .Select(node => new XlsxUnevaluableCell(node.Cell.Sheet, node.Cell.Reference, node.Failure ?? "not evaluated"))
                    .ToList();
                return new XlsxFormulaEvaluation(values, unevaluable, _order.Count);
            }

            // ------------------------------------------------------------ cells

            private Value CellValue(string sheet, int row, int column)
            {
                if (_nodes.TryGetValue((sheet, row, column), out var node))
                {
                    if (node.Evaluated) return node.Result;
                    throw Unsupported($"reads '{sheet}'!{node.Cell.Reference}, which is not evaluable");
                }
                var cell = _source.GetCell(sheet, row, column);
                switch (cell.Kind)
                {
                    case XlsxSourceKind.Number:
                        return double.IsFinite(cell.Number) ? Value.Num(cell.Number) : throw Unsupported("a referenced number is not finite");
                    case XlsxSourceKind.Text:
                        return Value.Str(cell.Text ?? string.Empty);
                    case XlsxSourceKind.Formula:
                        throw Unsupported("a referenced formula cell was not listed among the formula cells");
                    default:
                        return Value.Empty;
                }
            }

            private Value RangeMatrix(RangeExpr range)
            {
                if (range.CellCount > MaxRangeCells) throw Unsupported($"a range of {range.CellCount} cells (more than {MaxRangeCells})");
                var items = new Value[range.Rows * range.Columns];
                for (var r = 0; r < range.Rows; r++)
                    for (var c = 0; c < range.Columns; c++)
                        items[r * range.Columns + c] = CellValue(range.Sheet, range.Row1 + r, range.Column1 + c);
                return Value.Matrix(items, range.Rows, range.Columns);
            }

            private static RangeExpr RangeArgument(Expr argument, string function) => argument switch
            {
                RefExpr reference => new RangeExpr(reference.Sheet, reference.Row, reference.Column, reference.Row, reference.Column),
                RangeExpr range when range.CellCount <= MaxRangeCells => range,
                RangeExpr range => throw Unsupported($"a range of {range.CellCount} cells (more than {MaxRangeCells})"),
                _ => throw Unsupported($"{function} needs a cell range"),
            };

            // -------------------------------------------------------- evaluation

            private Value Top(Expr expression)
            {
                var value = Unwrap(Eval(expression, false));
                switch (value.Kind)
                {
                    case K.Matrix:
                        throw Unsupported("the formula returns an array, not a single value");
                    case K.Empty:
                        return Value.Num(0); // a formula that reads an empty cell shows 0
                    case K.Number:
                        return Value.Num(Finite(value.Number) == 0 ? 0 : value.Number); // never "-0"
                    case K.Text:
                        if (value.Text.Length > 32767) throw Unsupported("#VALUE! (text longer than 32767 characters)");
                        try { XmlConvert.VerifyXmlChars(value.Text); }
                        catch (XmlException) { throw Unsupported("the text result has characters a workbook cannot store"); }
                        return value;
                    default:
                        return value;
                }
            }

            /// <param name="array">True inside SUMPRODUCT: ranges and the functions over them work element by element.
            /// Outside it a multi-cell range used as one value is Excel's implicit intersection, which is refused.</param>
            private Value Eval(Expr expression, bool array)
            {
                switch (expression)
                {
                    case NumberExpr number:
                        return Value.Num(number.Value);
                    case TextExpr text:
                        return Value.Str(text.Value);
                    case BoolExpr logical:
                        return Value.Logical(logical.Value);
                    case RefExpr reference:
                        return CellValue(reference.Sheet, reference.Row, reference.Column);
                    case RangeExpr range:
                        if (range.Row1 == range.Row2 && range.Column1 == range.Column2) return CellValue(range.Sheet, range.Row1, range.Column1);
                        if (!array) throw Unsupported("a multi-cell range is used where Excel takes one value (implicit intersection)");
                        return RangeMatrix(range);
                    case UnaryExpr unary:
                    {
                        var operand = Eval(unary.Operand, array);
                        return unary.Negate ? Map(operand, v => Value.Num(-Arithmetic(v))) : operand;
                    }
                    case ChainExpr chain:
                    {
                        var accumulated = Eval(chain.First, array);
                        foreach (var (symbol, operand) in chain.Rest)
                        {
                            var right = Eval(operand, array);
                            accumulated = Zip(accumulated, right, (a, b) => Binary(symbol, a, b));
                        }
                        return accumulated;
                    }
                    case CallExpr call:
                        return Call(call, array);
                    default:
                        throw Unsupported("unknown expression");
                }
            }

            private Value Call(CallExpr call, bool array)
            {
                var args = call.Arguments;
                switch (call.Name)
                {
                    case "PI":
                        return Value.Num(Math.PI);
                    case "NA":
                        throw Unsupported("#N/A");
                    case "IF":
                    {
                        var condition = Unwrap(Eval(args[0], array));
                        if (Truth(condition)) return Eval(args[1], array);
                        return args.Count > 2 ? Eval(args[2], array) : Value.Logical(false);
                    }
                    case "CHOOSE":
                    {
                        var index = Unwrap(Eval(args[0], array));
                        if (index.Kind != K.Number) throw Unsupported("#VALUE! (CHOOSE index is not a number)");
                        var chosen = Math.Truncate(index.Number);
                        if (chosen < 1 || chosen > args.Count - 1) throw Unsupported("#VALUE! (CHOOSE index out of range)");
                        return Eval(args[(int)chosen], array);
                    }
                    case "ROUND":
                        return Zip(Eval(args[0], array), Eval(args[1], array), (x, digits) => Value.Num(ExcelRound(Arithmetic(x), Arithmetic(digits))));
                    case "SQRT":
                        return Map(Eval(args[0], array), v =>
                        {
                            var x = Arithmetic(v);
                            return x < 0 ? throw Unsupported("#NUM! (square root of a negative number)") : Value.Num(Math.Sqrt(x));
                        });
                    case "ABS":
                        return Map(Eval(args[0], array), v => Value.Num(Math.Abs(Arithmetic(v))));
                    case "N":
                        // Excel N(): a number stays, TRUE/FALSE → 1/0, text and an empty cell → 0 (BoQ v4: a crossing's
                        // "לא נמדד" hatch cell adds nothing to its painted area).
                        return Map(Eval(args[0], array), v => v.Kind switch
                        {
                            K.Number => Value.Num(v.Number),
                            K.Logical => Value.Num(v.Number != 0 ? 1 : 0),
                            _ => Value.Num(0),
                        });
                    case "NOT":
                        return Map(Eval(args[0], array), v => Value.Logical(!Truth(v)));
                    case "ISNUMBER":
                        return Map(Eval(args[0], array), v => Value.Logical(v.Kind == K.Number));
                    case "LEN":
                        return Map(Eval(args[0], array), v => Value.Num(TextOf(v).Length));
                    case "TRIM":
                        return Map(Eval(args[0], array), v => Value.Str(string.Join(" ", TextOf(v).Split(' ', StringSplitOptions.RemoveEmptyEntries))));
                    case "SUM":
                    {
                        var total = 0.0;
                        foreach (var (item, fromReference) in Arguments(args, array))
                            if (Numeric(item, fromReference, out var number)) total += number;
                        return Value.Num(Finite(total));
                    }
                    case "MIN":
                    case "MAX":
                    {
                        double? best = null;
                        foreach (var (item, fromReference) in Arguments(args, array))
                        {
                            if (!Numeric(item, fromReference, out var number)) continue;
                            best = best == null ? number : call.Name == "MIN" ? Math.Min(best.Value, number) : Math.Max(best.Value, number);
                        }
                        return Value.Num(best ?? 0);
                    }
                    case "AND":
                    case "OR":
                    {
                        bool? result = null;
                        foreach (var (item, fromReference) in Arguments(args, array))
                        {
                            bool truth;
                            switch (item.Kind)
                            {
                                case K.Logical:
                                case K.Number:
                                    truth = item.Number != 0;
                                    break;
                                case K.Text when !fromReference:
                                    throw Unsupported($"#VALUE! (a text argument to {call.Name})");
                                default:
                                    continue;
                            }
                            result = call.Name == "AND" ? (result ?? true) && truth : (result ?? false) || truth;
                        }
                        return result is bool logical ? Value.Logical(logical) : throw Unsupported($"#VALUE! ({call.Name} has no logical values)");
                    }
                    case "SUMPRODUCT":
                    {
                        var matrices = new List<Value>(args.Count);
                        foreach (var argument in args) matrices.Add(ProductArgument(argument));
                        var first = matrices[0];
                        foreach (var matrix in matrices)
                            if (matrix.Rows != first.Rows || matrix.Columns != first.Columns)
                                throw Unsupported("#VALUE! (SUMPRODUCT arrays differ in size)");
                        var total = 0.0;
                        for (var i = 0; i < first.Items.Length; i++)
                        {
                            var product = 1.0;
                            foreach (var matrix in matrices)
                            {
                                var item = matrix.Items[i];
                                product *= item.Kind == K.Number ? item.Number : 0; // SUMPRODUCT treats non-numbers as 0
                            }
                            total += product;
                        }
                        return Value.Num(Finite(total));
                    }
                    case "COUNTIF":
                    {
                        var range = RangeArgument(args[0], call.Name);
                        var criterion = TextCriterion(Unwrap(Eval(args[1], false)));
                        var count = 0;
                        for (var r = 0; r < range.Rows; r++)
                            for (var c = 0; c < range.Columns; c++)
                                if (Matches(CellValue(range.Sheet, range.Row1 + r, range.Column1 + c), criterion)) count++;
                        return Value.Num(count);
                    }
                    case "SUMIF":
                    {
                        var range = RangeArgument(args[0], call.Name);
                        var criterion = TextCriterion(Unwrap(Eval(args[1], false)));
                        var sumRange = args.Count > 2 ? RangeArgument(args[2], call.Name) : range;
                        if (sumRange.Rows != range.Rows || sumRange.Columns != range.Columns)
                            throw Unsupported("SUMIF with a sum range of another size");
                        var total = 0.0;
                        for (var r = 0; r < range.Rows; r++)
                            for (var c = 0; c < range.Columns; c++)
                            {
                                if (!Matches(CellValue(range.Sheet, range.Row1 + r, range.Column1 + c), criterion)) continue;
                                var item = CellValue(sumRange.Sheet, sumRange.Row1 + r, sumRange.Column1 + c);
                                if (item.Kind == K.Number) total += item.Number;
                            }
                        return Value.Num(Finite(total));
                    }
                    default:
                        throw Unsupported($"function {call.Name} is not supported");
                }
            }

            /// <summary>SUMPRODUCT arguments: ranges (a single cell included) and array expressions; a lone number is 1x1.</summary>
            private Value ProductArgument(Expr argument)
            {
                switch (argument)
                {
                    case RefExpr reference:
                        return Value.Matrix(new[] { CellValue(reference.Sheet, reference.Row, reference.Column) }, 1, 1);
                    case RangeExpr range:
                        return RangeMatrix(range);
                }
                var value = Eval(argument, true);
                if (value.Kind == K.Matrix) return value;
                if (value.Kind == K.Number) return Value.Matrix(new[] { value }, 1, 1);
                throw Unsupported("SUMPRODUCT of a single non-numeric value");
            }

            /// <summary>
            /// Arguments of SUM / MIN / MAX / AND / OR with Excel's reference rule: values read through a reference or
            /// range (or an array) skip text and logical values; values typed directly count (text must convert).
            /// </summary>
            private IEnumerable<(Value Item, bool FromReference)> Arguments(List<Expr> args, bool array)
            {
                foreach (var argument in args)
                {
                    if (argument is RefExpr reference)
                    {
                        yield return (CellValue(reference.Sheet, reference.Row, reference.Column), true);
                        continue;
                    }
                    if (argument is RangeExpr range)
                    {
                        if (range.CellCount > MaxRangeCells) throw Unsupported($"a range of {range.CellCount} cells (more than {MaxRangeCells})");
                        for (var row = range.Row1; row <= range.Row2; row++)
                            for (var column = range.Column1; column <= range.Column2; column++)
                                yield return (CellValue(range.Sheet, row, column), true);
                        continue;
                    }
                    var value = Eval(argument, array);
                    if (value.Kind == K.Matrix)
                    {
                        foreach (var item in value.Items) yield return (item, true);
                        continue;
                    }
                    yield return (value, false);
                }
            }
        }

        // ------------------------------------------------------- scalar semantics

        private static Value Unwrap(Value value) => value.Kind == K.Matrix && value.Items.Length == 1 ? value.Items[0] : value;

        private static Value Map(Value value, Func<Value, Value> function)
        {
            value = Unwrap(value);
            if (value.Kind != K.Matrix) return function(value);
            var items = new Value[value.Items.Length];
            for (var i = 0; i < items.Length; i++) items[i] = function(value.Items[i]);
            return Value.Matrix(items, value.Rows, value.Columns);
        }

        private static Value Zip(Value left, Value right, Func<Value, Value, Value> function)
        {
            left = Unwrap(left);
            right = Unwrap(right);
            if (left.Kind != K.Matrix && right.Kind != K.Matrix) return function(left, right);
            int rows, columns;
            if (left.Kind == K.Matrix && right.Kind == K.Matrix)
            {
                if (left.Rows != right.Rows || left.Columns != right.Columns) throw Unsupported("arrays of different sizes");
                rows = left.Rows;
                columns = left.Columns;
            }
            else
            {
                var matrix = left.Kind == K.Matrix ? left : right;
                rows = matrix.Rows;
                columns = matrix.Columns;
            }
            var items = new Value[rows * columns];
            for (var i = 0; i < items.Length; i++)
                items[i] = function(left.Kind == K.Matrix ? left.Items[i] : left, right.Kind == K.Matrix ? right.Items[i] : right);
            return Value.Matrix(items, rows, columns);
        }

        private static Value Binary(string symbol, Value a, Value b)
        {
            switch (symbol)
            {
                case "+":
                    return Value.Num(Finite(Arithmetic(a) + Arithmetic(b)));
                case "-":
                    return Value.Num(Finite(Arithmetic(a) - Arithmetic(b)));
                case "*":
                    return Value.Num(Finite(Arithmetic(a) * Arithmetic(b)));
                case "/":
                {
                    var dividend = Arithmetic(a);
                    var divisor = Arithmetic(b);
                    if (divisor == 0) throw Unsupported("#DIV/0!");
                    return Value.Num(Finite(dividend / divisor));
                }
                case "^":
                {
                    var x = Arithmetic(a);
                    var y = Arithmetic(b);
                    if (x == 0 && y == 0) throw Unsupported("#NUM! (0^0)");
                    if (x == 0 && y < 0) throw Unsupported("#DIV/0!");
                    return Value.Num(Finite(Math.Pow(x, y)));
                }
                case "&":
                    return Value.Str(ConcatenationText(a) + ConcatenationText(b));
                default:
                    return Value.Logical(Compare(symbol, a, b));
            }
        }

        /// <summary>A number for an arithmetic operator: empty = 0, TRUE/FALSE = 1/0; text would be a locale-dependent conversion.</summary>
        private static double Arithmetic(Value value) => value.Kind switch
        {
            K.Number => value.Number,
            K.Empty => 0,
            K.Logical => value.Number,
            K.Text => throw Unsupported("text used as a number (#VALUE! or a locale-dependent conversion)"),
            _ => throw Unsupported("an array used as one number"),
        };

        private static bool Truth(Value value) => value.Kind switch
        {
            K.Logical => value.Number != 0,
            K.Number => value.Number != 0,
            K.Empty => false,
            K.Text => throw Unsupported("#VALUE! (text used as a condition)"),
            _ => throw Unsupported("an array used as one condition"),
        };

        private static string TextOf(Value value) => value.Kind switch
        {
            K.Text => value.Text,
            K.Empty => string.Empty,
            _ => throw Unsupported("a number or logical value turned into text (Excel's General format)"),
        };

        /// <summary>Text for '&amp;': whole numbers only (a fraction's text depends on Excel's General format and locale).</summary>
        private static string ConcatenationText(Value value)
        {
            switch (value.Kind)
            {
                case K.Text:
                    return value.Text;
                case K.Empty:
                    return string.Empty;
                case K.Number:
                {
                    var shown = Fifteen(value.Number);
                    if (Math.Abs(shown) < 1e15 && shown == Math.Floor(shown))
                        return shown == 0 ? "0" : shown.ToString("0", CultureInfo.InvariantCulture);
                    throw Unsupported("a fractional or very large number turned into text (Excel's General format)");
                }
                default:
                    throw Unsupported("a logical value turned into text");
            }
        }

        /// <summary>
        /// Excel's comparison: an empty cell takes the other side's type (0, "" or FALSE); numbers &lt; text &lt; logical
        /// values; numbers compare at 15 significant digits; text equality ignores case. Text ordering follows Excel's
        /// locale collation and is refused.
        /// </summary>
        private static bool Compare(string symbol, Value a, Value b)
        {
            int order;
            if (a.Kind == K.Empty && b.Kind == K.Empty)
            {
                order = 0;
            }
            else
            {
                if (a.Kind == K.Empty) a = EmptyLike(b);
                if (b.Kind == K.Empty) b = EmptyLike(a);
                var rankA = Rank(a);
                var rankB = Rank(b);
                if (rankA != rankB) order = rankA.CompareTo(rankB);
                else if (a.Kind == K.Number) order = Fifteen(a.Number).CompareTo(Fifteen(b.Number));
                else if (a.Kind == K.Logical) order = a.Number.CompareTo(b.Number);
                else if (symbol is "=" or "<>") order = string.Equals(a.Text, b.Text, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                else throw Unsupported("ordering texts depends on Excel's locale collation");
            }
            return symbol switch
            {
                "=" => order == 0,
                "<>" => order != 0,
                "<" => order < 0,
                ">" => order > 0,
                "<=" => order <= 0,
                ">=" => order >= 0,
                _ => throw Unsupported($"operator {symbol}"),
            };
        }

        private static Value EmptyLike(Value other) => other.Kind switch
        {
            K.Text => Value.Str(string.Empty),
            K.Logical => Value.Logical(false),
            _ => Value.Num(0),
        };

        private static int Rank(Value value) => value.Kind switch
        {
            K.Number => 0,
            K.Text => 1,
            K.Logical => 2,
            _ => throw Unsupported("an array compared as one value"),
        };

        private static bool Numeric(Value item, bool fromReference, out double number)
        {
            number = 0;
            switch (item.Kind)
            {
                case K.Number:
                    number = item.Number;
                    return true;
                case K.Logical:
                    if (fromReference) return false;
                    number = item.Number;
                    return true;
                case K.Text:
                    if (fromReference) return false;
                    throw Unsupported("#VALUE! or a locale-dependent conversion (a text argument)");
                default:
                    return false;
            }
        }

        /// <summary>
        /// SUMIF / COUNTIF criteria as the writers use them: plain text matched whole and case-insensitively. Operators,
        /// wildcards and text Excel would read as a number, date or logical value are refused.
        /// </summary>
        private static string TextCriterion(Value value)
        {
            if (value.Kind != K.Text) throw Unsupported("SUMIF/COUNTIF criteria other than plain text");
            var text = value.Text;
            if (text.Length == 0 || text[0] is '=' or '<' or '>' || text.IndexOfAny(CriteriaSpecial) >= 0 ||
                text.Any(char.IsAsciiDigit) ||
                text.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw Unsupported("SUMIF/COUNTIF criteria with an operator, wildcard, number, date or logical value");
            return text;
        }

        private static bool Matches(Value cell, string criterion) =>
            cell.Kind == K.Text && string.Equals(cell.Text, criterion, StringComparison.OrdinalIgnoreCase);
    }
}
