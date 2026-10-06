using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// Reads a previously delivered estimate workbook (the supplied Judgment-2
    /// example) purely as EVIDENCE: which catalog codes a comparable early-design
    /// road/traffic estimate actually contained.
    ///
    /// Deliberately does NOT read its quantities or prices into the product. Those
    /// belong to another project and another price book — copying them would be
    /// exactly the fabrication the locked plan forbids. Only the code list travels.
    /// </summary>
    public static class ReferenceEstimateLoader
    {
        public sealed class Reference
        {
            public required string SourceFile { get; init; }
            public required string SourceHash { get; init; }
            public List<string> CatalogCodes { get; init; } = new();
            public List<DeliveryFinding> Findings { get; init; } = new();
        }

        public static Reference Load(string xlsxPath)
        {
            if (!File.Exists(xlsxPath))
            {
                return new Reference
                {
                    SourceFile = xlsxPath,
                    SourceHash = string.Empty,
                    Findings =
                    {
                        new DeliveryFinding
                        {
                            Code = "EST-REFERENCE-MISSING",
                            Domain = "estimate",
                            Severity = FindingSeverity.Warning,
                            Title = "Reference estimate workbook not found — proposals lose their comparable-project signal",
                            Message = xlsxPath,
                        }
                    }
                };
            }

            var result = new Reference
            {
                SourceFile = Path.GetFileName(xlsxPath),
                SourceHash = ArtifactHash.Sha256OfFile(xlsxPath),
            };

            // Dependency-free read (see MiniXlsx): every sheet, every cell.
            foreach (var sheetRows in MiniXlsx.ReadAllSheets(xlsxPath))
            {
                foreach (var row in sheetRows)
                {
                    foreach (var cell in row)
                    {
                        var text = cell.Text.Trim();
                        // Catalog codes look like U51.01.0250 — the same shape the
                        // price book uses. Anything else in the sheet is ignored.
                        if (text.Length >= 6 && text[0] == 'U' && text.Count(c => c == '.') >= 2 &&
                            !result.CatalogCodes.Contains(text, StringComparer.OrdinalIgnoreCase))
                        {
                            result.CatalogCodes.Add(text);
                        }
                    }
                }
            }

            return result;
        }

    }
}
