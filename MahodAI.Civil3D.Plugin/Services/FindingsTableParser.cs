using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MahodAI.Civil3D.Plugin.Models;

namespace MahodAI.Civil3D.Plugin.Services
{
    /// <summary>
    /// Parses the analysis report's findings table out of the assistant's markdown so each
    /// row can be pinned on the drawing overlay. Recognizes the Hebrew columns
    /// (מיקום / בעיה / ערך בפועל / נדרש / מקור) by header text and extracts the
    /// alignment + station(s) from the location column via <see cref="LocationParser"/>.
    /// </summary>
    public static class FindingsTableParser
    {
        private static readonly Regex MarkdownLinkRegex = new Regex(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

        /// <summary>
        /// Extracts pin-able findings from markdown content. Returns an empty list if no
        /// recognizable findings table (one with a location and a problem column) is present.
        /// </summary>
        public static List<ProblemMarker> Parse(string? markdown)
        {
            var markers = new List<ProblemMarker>();
            if (string.IsNullOrWhiteSpace(markdown))
                return markers;

            var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            // Walk the content collecting contiguous markdown-table blocks, then parse the
            // first block that looks like a findings table.
            var block = new List<string>();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                bool isTableRow = line.StartsWith("|") && line.EndsWith("|");
                if (isTableRow)
                {
                    block.Add(line);
                }
                else
                {
                    if (block.Count > 0)
                    {
                        if (TryParseBlock(block, markers))
                            return markers;   // first findings table wins
                        block.Clear();
                    }
                }
            }
            if (block.Count > 0)
                TryParseBlock(block, markers);

            return markers;
        }

        private static bool TryParseBlock(List<string> block, List<ProblemMarker> markers)
        {
            if (block.Count < 2)
                return false;

            // First row = header; the rest are separator or body rows.
            var header = SplitCells(block[0]);
            int locCol = IndexOfHeader(header, "מיקום");
            int problemCol = IndexOfHeader(header, "בעיה");
            if (locCol < 0 || problemCol < 0)
                return false;

            int valueCol = IndexOfHeader(header, "בפועל");
            int requiredCol = IndexOfHeader(header, "נדרש");
            int sourceCol = IndexOfHeader(header, "מקור");

            int index = 0;
            for (int i = 1; i < block.Count; i++)
            {
                var line = block[i];
                if (Regex.IsMatch(line, @"^\|[\s\-:|]+\|$"))
                    continue;   // separator

                var cells = SplitCells(line);
                if (cells.Count <= locCol)
                    continue;

                string locText = CleanCell(cells[locCol]);
                if (!LocationParser.TryParse(locText, out string align, out double s1, out double? s2))
                    continue;   // not a placeable location

                index++;
                string problem = problemCol < cells.Count ? CleanCell(cells[problemCol]) : "";
                markers.Add(new ProblemMarker
                {
                    Index = index,
                    Alignment = align,
                    StationStart = s1,
                    StationEnd = s2,
                    LocationText = locText,
                    Problem = problem,
                    ActualValue = valueCol >= 0 && valueCol < cells.Count ? CleanCell(cells[valueCol]) : "",
                    Required = requiredCol >= 0 && requiredCol < cells.Count ? CleanCell(cells[requiredCol]) : "",
                    Source = sourceCol >= 0 && sourceCol < cells.Count ? CleanCell(cells[sourceCol]) : "",
                    Severity = InferSeverity(problem),
                });
            }

            return markers.Count > 0;
        }

        private static List<string> SplitCells(string line)
        {
            var cells = line.Split('|').Select(c => c.Trim()).ToList();
            if (cells.Count > 0 && string.IsNullOrEmpty(cells[0]))
                cells.RemoveAt(0);
            if (cells.Count > 0 && string.IsNullOrEmpty(cells[cells.Count - 1]))
                cells.RemoveAt(cells.Count - 1);
            return cells;
        }

        private static int IndexOfHeader(List<string> header, string keyword)
        {
            for (int i = 0; i < header.Count; i++)
            {
                if (header[i].Contains(keyword))
                    return i;
            }
            return -1;
        }

        /// <summary>Strip markdown links/bold and collapse whitespace for plain display.</summary>
        private static string CleanCell(string cell)
        {
            string s = MarkdownLinkRegex.Replace(cell, "$1");
            s = s.Replace("**", "").Replace("__", "");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        public static string InferSeverity(string problem)
        {
            if (string.IsNullOrEmpty(problem)) return "important";
            if (problem.Contains("קריטי") || problem.Contains("חמור") || problem.Contains("מסוכן"))
                return "critical";
            if (problem.Contains("המלצה") || problem.Contains("קל") || problem.Contains("זניח"))
                return "minor";
            return "important";
        }
    }
}
