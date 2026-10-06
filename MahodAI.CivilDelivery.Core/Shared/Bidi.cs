using System;
using System.Linq;

namespace MahodAI.CivilDelivery.Shared
{
    /// <summary>
    /// Bidirectional-text helpers for Hebrew UI strings that embed Latin identifiers
    /// (layer names, style names, file names, catalog codes). Without an explicit
    /// embedding, "6422-CIVIL-WEST.dwg" inside a Hebrew sentence renders as
    /// "CIVIL-WEST.dwg-6422" and "[Warning] CODE: ..." scatters its brackets.
    /// </summary>
    public static class Bidi
    {
        /// <summary>
        /// Left-to-right marks (LRM) around a Latin token. WPF's text engine ignores the
        /// explicit embeddings LRE/PDF and LRI/PDI (tested 2026-08-19: "6422-X.dwg" still
        /// rendered as "X.dwg-6422"); a leading/trailing LRM does anchor the run. Excel and
        /// Win32 message boxes honour LRM as well, so one helper serves every surface.
        /// </summary>
        public static string Ltr(string? s) =>
            string.IsNullOrEmpty(s) ? (s ?? "") : "\u200E" + s + "\u200E";

        /// <summary>
        /// True when the text mixes Hebrew letters with Latin letters or digits (e.g. "la-293-SP-E--Q-כביש.dwg"). LRM marks
        /// cannot keep such a token in order inside a right-to-left WPF paragraph (the Hebrew word is its own run), and WPF
        /// ignores LRE/PDF — the token must be shown in a left-to-right block instead.
        /// </summary>
        public static bool MixesHebrewAndLatin(string? s) =>
            !string.IsNullOrEmpty(s) && s.Any(c => c is >= '\u0590' and <= '\u05FF') && s.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

        /// <summary>Hebrew label for a finding severity, for the engineer-facing lists.</summary>
        public static string Severity(DeliveryFinding finding) => finding.Severity switch
        {
            FindingSeverity.Info => "מידע",
            FindingSeverity.Warning => "אזהרה",
            FindingSeverity.ReviewRequired => "דרושה בדיקה",
            FindingSeverity.Error => "שגיאה",
            _ => finding.Severity.ToString(),
        };

        /// <summary>
        /// One finding as the engineer reads it: Hebrew severity, the Hebrew title, and
        /// the machine code last, isolated, so support can still quote it.
        /// </summary>
        public static string FindingLine(DeliveryFinding f) =>
            $"{Severity(f)}: {f.Title} ({Ltr(f.Code)})";
    }
}
