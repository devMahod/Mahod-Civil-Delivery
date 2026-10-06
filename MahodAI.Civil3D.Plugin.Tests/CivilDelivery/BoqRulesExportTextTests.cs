using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The export message box text (display only): one sentence per line, a wrapped name on its own line after a colon, and
/// nothing split inside an LRM pair (Codex 14:34: "Road. Phase2.dwg" must stay one name).
/// </summary>
public sealed class BoqRulesExportTextTests
{
    private const string L = "‎";

    [Fact]
    public void TheRealB13NoteBecomesShortLinesWithTheNameAndRangeWhole()
    {
        var note = "מקור שנבחר במפורש לייצוא: " + L + "6422-CIVIL-WEST-2026-08-04-UT.claude-copy.dwg" + L +
                   ". פרקים " + L + "51.01–51.04" + L + " מהמדידה estimate-corridor-boq-20261001-083705-468db276 של " +
                   L + "6422-CIVIL-WEST-2026-08-04-UT.claude-copy.dwg" + L + " (01.10.2026 11:37).";
        BoqRulesExportText.CorridorLines(new[] { note }).Should().Be(
            "מקור שנבחר במפורש לייצוא:\n" +
            L + "6422-CIVIL-WEST-2026-08-04-UT.claude-copy.dwg" + L + ".\n" +
            "פרקים " + L + "51.01–51.04" + L + " מהמדידה estimate-corridor-boq-20261001-083705-468db276\nשל " +
            L + "6422-CIVIL-WEST-2026-08-04-UT.claude-copy.dwg" + L + " (01.10.2026 11:37).");
    }

    [Fact]
    public void TheDrawingNameAfterShelStartsItsOwnLine_SoTheBoxNeverWrapsInsideIt()
    {
        // b14 live 17:31: the box wrapped "6422-CIVIL-…" at its first hyphen and reversed "של 6422".
        BoqRulesExportText.CorridorLines(new[] { "מהמדידה run-1 של " + L + "A-B.dwg" + L + " (01.10.2026)." })
            .Should().Be("מהמדידה run-1\nשל " + L + "A-B.dwg" + L + " (01.10.2026).");
        BoqRulesExportText.CorridorLines(new[] { "אין שינוי של מדידה." }).Should().Be("אין שינוי של מדידה.",
            "only a name in an LRM pair after של is moved");
    }

    [Fact]
    public void ANameWithADotAndSpaceInsideTheLrmPairIsNotSplit()
    {
        var note = "מקור: " + L + "Road. Phase2.dwg" + L + ". סוף.";
        BoqRulesExportText.CorridorLines(new[] { note }).Should().Be("מקור:\n" + L + "Road. Phase2.dwg" + L + ".\nסוף.");
    }

    [Fact]
    public void HebrewSentencesSplitOnlyAtSentenceEndsAndDatesStayWhole()
    {
        BoqRulesExportText.CorridorLines(new[] { "לא נמצאה מדידה בפרופיל. יש למדוד (01.10.2026 11:37)." })
            .Should().Be("לא נמצאה מדידה בפרופיל.\nיש למדוד (01.10.2026 11:37).");
        BoqRulesExportText.CorridorLines(new[] { "שטח: 12.5 מ\"ר" }).Should().Be("שטח: 12.5 מ\"ר", "a colon before plain text is not a name");
    }

    [Fact]
    public void SeveralNotesEachStartALineAndNoNotesIsEmpty()
    {
        BoqRulesExportText.CorridorLines(new[] { "א.", "ב." }).Should().Be("א.\nב.");
        BoqRulesExportText.CorridorLines(Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void TheNotesThemselvesAreNotChanged()
    {
        var notes = new[] { "מקור: " + L + "a. b.dwg" + L + ". סוף." };
        var copy = (string[])notes.Clone();
        BoqRulesExportText.CorridorLines(notes);
        notes.Should().Equal(copy);
    }
}
