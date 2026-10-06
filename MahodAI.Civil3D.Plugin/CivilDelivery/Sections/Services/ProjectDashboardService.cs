using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using MahodAI.CivilDelivery.Shared;
using CivilDb = Autodesk.Civil.DatabaseServices;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// What the product already understands about the open drawing, computed the moment
    /// the palette opens - before the engineer clicks anything. Alignments, surfaces,
    /// pipe networks, how many CL lines the configured CL drawing holds, and how many
    /// sections this tool has already created here. Read-only, fast, and every number
    /// is a fact from the database, not a guess.
    /// </summary>
    public sealed class ProjectDashboardService
    {
        public sealed record Dashboard(
            string DrawingName,
            int Alignments,
            IReadOnlyList<string> AlignmentNames,
            int Surfaces,
            int PipeNetworks,
            int ClLines,
            string? ClSourceName,
            int ExistingToolSections,
            IReadOnlyList<string> Notes)
        {
            public bool ClInModel { get; init; }

            /// <summary>One readable paragraph for the palette header.</summary>
            public string Summary()
            {
                var parts = new List<string>();
                parts.Add(Alignments < 0 ? "תוואים: לא ניתן לקרוא" : Alignments == 1 ? "תוואי אחד" : $"{Alignments:N0} תוואים");
                parts.Add(Surfaces < 0 ? "משטחים: לא ניתן לקרוא" : Surfaces == 1 ? "משטח אחד" : $"{Surfaces:N0} משטחים");
                if (PipeNetworks < 0) parts.Add("רשתות צנרת: לא ניתן לקרוא");
                if (PipeNetworks > 0) parts.Add(PipeNetworks == 1 ? "רשת צנרת אחת" : $"{PipeNetworks:N0} רשתות צנרת");
                var line1 = string.Join(" · ", parts);

                string line2;
                if (ClInModel)
                    line2 = "קווי חתך: בשכבות המודל — ספירה ובדיקת מקורות בתכנון; אין צורך בקובץ CL נפרד";
                else if (ClSourceName == null)
                    line2 = "קווי חתך: טרם הוגדרו — בחר שכבה במודל או קובץ CL בהגדרת פרויקט";
                else if (ClLines < 0)
                    line2 = $"קובץ CL: {ClSourceName} — לא נמצא או לא ניתן לקרוא; בדוק בתכנון";
                else
                    line2 = $"קובץ CL: {ClSourceName} — {ClLines:N0} קווי חתך";

                var line3 = ExistingToolSections < 0
                    ? "חתכים של הכלי: לא ניתן להשלים את הקריאה — בדוק בתכנון"
                    : ExistingToolSections == 0
                    ? "אין עדיין חתכים של הכלי בשרטוט הזה"
                    // SEC-m2 (review of 1.3.9): this counts every owned SAMPLE LINE; PLAN
                    // matches only those whose CL still exists, so "תכנון יזהה אותם"
                    // was not true for owned lines without a current CL record.
                    : ExistingToolSections == 1
                    ? "קו דגימה אחד של הכלי קיים בשרטוט — טבלת התכנון מראה אם הוא תואם לקו CL"
                    : $"{ExistingToolSections:N0} קווי דגימה של הכלי קיימים בשרטוט — טבלת התכנון מראה אילו מהם תואמים לקווי CL";

                var all = new List<string> { line1, line2, line3 };
                all.AddRange(Notes);
                return string.Join("\n", all);
            }
        }

        public Dashboard Build(Database db, CivilDocument civilDoc, ProjectProfile profile)
        {
            var notes = new List<string>();
            var alignmentNames = new List<string>();
            int surfaces = 0, networks = 0, owned = 0;
            bool alignmentsUnreadable = false, ownedUnreadable = false;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    foreach (ObjectId id in civilDoc.GetAlignmentIds())
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is CivilDb.Alignment a) alignmentNames.Add(a.Name);
                    }
                }
                catch (Exception ex) { alignmentsUnreadable = true; notes.Add("לא ניתן לקרוא תוואים: " + ex.Message); }

                try { surfaces = civilDoc.GetSurfaceIds().Count; } catch { surfaces = -1; }
                try { networks = civilDoc.GetPipeNetworkIds().Count; } catch { networks = -1; }

                // Sections this tool owns here: sample lines carrying our ownership Xrecord.
                try
                {
                    foreach (ObjectId alignmentId in civilDoc.GetAlignmentIds())
                    {
                        if (tr.GetObject(alignmentId, OpenMode.ForRead) is not CivilDb.Alignment a) continue;
                        foreach (ObjectId slgId in a.GetSampleLineGroupIds())
                        {
                            if (tr.GetObject(slgId, OpenMode.ForRead) is not CivilDb.SampleLineGroup slg) continue;
                            foreach (ObjectId slId in slg.GetSampleLineIds())
                            {
                                try
                                {
                                    var sl = tr.GetObject(slId, OpenMode.ForRead);
                                    if (SectionOwnershipService.Read(tr, sl) != null) owned++;
                                }
                                catch { ownedUnreadable = true; }
                            }
                        }
                    }
                }
                catch { ownedUnreadable = true; }
                tr.Commit();
            }

            // External CL preview only, not a complete PLAN count (which also reads
            // the host and XREFs). For model-layer CL defer the expensive scan to PLAN.
            int clLines = 0;
            string? clName = null;
            var hostPath = db.Filename ?? string.Empty;
            if (profile.Sections.Cl.SourceFiles.Count > 0)
            {
                foreach (var clFile in profile.Sections.Cl.SourceFiles)
                {
                    var resolved = clFile;
                    if (!Path.IsPathRooted(resolved) && !string.IsNullOrEmpty(hostPath))
                        resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(hostPath)!, resolved));
                    if (!File.Exists(resolved)) continue;
                    if (!string.IsNullOrEmpty(hostPath) &&
                        string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(hostPath), StringComparison.OrdinalIgnoreCase))
                        continue;
                    clName = Path.GetFileName(resolved);
                    clLines = CountClLines(resolved, profile);
                    break;
                }
                if (clName == null) { clName = Path.GetFileName(profile.Sections.Cl.SourceFiles[0]); clLines = -1; }
            }

            // A note the engineer should see before planning: design-variant alignments.
            var variants = alignmentNames.Where(n =>
                n.Contains("-des", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("(1)", StringComparison.Ordinal) ||
                n.Contains("-Right", StringComparison.OrdinalIgnoreCase) ||
                n.Contains("-Left", StringComparison.OrdinalIgnoreCase)).ToList();
            if (variants.Count > 0 && profile.Sections.Alignments.AllowedNames.Count == 0)
                notes.Add($"שים לב: {variants.Count} תוואים נראים כגרסאות תכנון ({string.Join(", ", variants.Take(3))}…) — בחר תוואים ראשיים בהגדרת פרויקט כדי למנוע דו-משמעות");

            return new Dashboard(
                Path.GetFileName(hostPath), alignmentsUnreadable ? -1 : alignmentNames.Count, alignmentNames, surfaces, networks,
                clLines, clName, ownedUnreadable ? -1 : owned, notes)
            {
                ClInModel = profile.Sections.Cl.SourceFiles.Count == 0 &&
                    profile.Sections.Cl.LayerPatterns.Count > 0,
            };
        }

        private static int CountClLines(string clPath, ProjectProfile profile)
        {
            try
            {
                using var sideDwg = SideDwg.OpenReadOnly(clPath);
                var side = sideDwg.Db;
                using var tr = side.TransactionManager.StartTransaction();
                var reader = new ClInstructionReader();
                // Read against the side DB as host, with an empty SourceFiles so it does
                // not recurse into itself; same layer/type filters as PLAN.
                var tmp = new ProjectProfile { ProfileId = profile.ProfileId };
                tmp.Sections.Cl.LayerPatterns.AddRange(profile.Sections.Cl.LayerPatterns);
                tmp.Sections.Cl.AllowedEntityTypes.AddRange(profile.Sections.Cl.AllowedEntityTypes);
                var result = reader.Read(side, tr, tmp);
                tr.Commit();
                return result.Findings.Any(f => f.Severity >= FindingSeverity.Error)
                    ? -1 : result.Records.Count;
            }
            catch { return -1; }
        }
    }
}
