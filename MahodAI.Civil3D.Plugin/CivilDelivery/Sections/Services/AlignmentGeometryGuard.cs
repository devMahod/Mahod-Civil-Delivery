using System;
using System.Collections.Generic;
using System.Linq;
using CivilDb = Autodesk.Civil.DatabaseServices;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>
    /// An alignment with no geometry (zero length, no entities) cannot carry a section, and Civil's
    /// <c>IntersectWith</c> throws <c>eDegenerateGeometry</c> on it. Project 984 (06.10, employee install) holds such an
    /// alignment ("4", length 0, 0 entities): every CL crossing probe against it failed, so the setup scan was marked
    /// incomplete for every layer and picking a separate CL drawing aborted with the raw exception. An empty alignment
    /// is therefore left out of crossing probes and reported once, visibly, as information. It is never selectable as
    /// a section axis. Any other read or intersection failure keeps its existing fail-closed path.
    /// </summary>
    internal static class AlignmentGeometryGuard
    {
        public const string FindingCode = "SEC-ALIGNMENT-EMPTY";
        public const double MinUsableLengthM = 1e-6;

        /// <summary>
        /// True only for a proven-empty alignment: a finite length at or below <see cref="MinUsableLengthM"/>, or a
        /// readable entity count of zero. A non-finite length or an unreadable entity list (<c>null</c>) is a read
        /// failure, not emptiness, and stays with the caller's fail-closed handling.
        /// </summary>
        internal static bool HasNoGeometry(double length, int? entityCount) =>
            (double.IsFinite(length) && length >= 0 && length <= MinUsableLengthM) || entityCount == 0;

        /// <summary>The exact evidence read from Civil; a failed read is null, never zero.</summary>
        internal readonly record struct Evidence(double Length, int? EntityCount)
        {
            public bool IsEmpty => HasNoGeometry(Length, EntityCount);
            public string Text => $"אורך {(double.IsFinite(Length) ? Length.ToString("0.######") : "לא קריא")}, " +
                $"אלמנטים {(EntityCount?.ToString() ?? "לא קריא")}";
        }

        internal static Evidence Read(CivilDb.Alignment alignment)
        {
            ArgumentNullException.ThrowIfNull(alignment);
            double length;
            try { length = alignment.Length; }
            catch { length = double.NaN; }
            int? entities;
            try { entities = alignment.Entities.Count; }
            catch { entities = null; }
            return new Evidence(length, entities);
        }

        internal static bool HasNoGeometry(CivilDb.Alignment alignment) => Read(alignment).IsEmpty;

        internal static DeliveryFinding Finding(string alignmentName, string? profileId, Evidence? evidence = null) => new()
        {
            Code = FindingCode,
            Domain = SectionPlanLogic.Domain,
            Severity = FindingSeverity.Info,
            Title = $"תוואי ריק דולג: {Bidi.Ltr(alignmentName)}",
            Message = $"לתוואי {Bidi.Ltr(alignmentName)} אין גאומטריה שמישה" +
                      (evidence is { } e ? $" ({e.Text})" : "") + ". " +
                      "הוא אינו משתתף בחיפוש קווי CL ובתכנון החתכים, ואינו ניתן לבחירה כציר חתך.",
            RecommendedAction = "אם התוואי נדרש — יש להשלים אותו ב-Civil ולהריץ שוב. אחרת אין צורך בפעולה.",
            ProjectProfileId = profileId,
        };

        /// <summary>Adds the information finding once per alignment name.</summary>
        internal static void Report(List<DeliveryFinding> findings, string alignmentName, string? profileId,
            Evidence? evidence = null)
        {
            ArgumentNullException.ThrowIfNull(findings);
            var finding = Finding(alignmentName, profileId, evidence);
            if (findings.Any(f => f.Code == FindingCode &&
                    string.Equals(f.Title, finding.Title, StringComparison.Ordinal)))
                return;
            findings.Add(finding);
        }
    }
}
