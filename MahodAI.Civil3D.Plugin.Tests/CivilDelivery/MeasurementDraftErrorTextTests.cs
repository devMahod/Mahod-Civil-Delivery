using System;
using System.IO;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class MeasurementDraftErrorTextTests
{
    [Fact]
    public void SavedDwgChangedPreWriteFailureExplainsRescanAndPreservesInnerError()
    {
        var reason = EstimateSourceSnapshotPolicy.FreshnessFailure(
            new string('a', 64), "revision", new string('b', 64), "revision", 0, scannedDbMod: 0);
        var original = new InvalidOperationException("ייצוא מדידות לבדיקה נעצר: מקור השרטוט אינו תואם לסריקה: " + reason);
        var displayed = MeasurementDraftErrorText.Describe(original);
        Assert.Same(original, displayed.InnerException);
        Assert.Contains("DWG", displayed.Message);
        Assert.Contains("סרוק כמויות", displayed.Message);
        Assert.Contains("לא נוצר קובץ", displayed.Message);
        Assert.DoesNotContain("saved DWG bytes changed", displayed.Message);
    }

    [Theory]
    [InlineData("CAS mismatch")]
    [InlineData("ייצוא מדידות לבדיקה נעצר: מקור השרטוט אינו תואם לסריקה: the live drawing database changed since the quantity scan")]
    [InlineData("פרסום טיוטה נעצר: מקור השרטוט אינו תואם לסריקה: the saved DWG bytes changed since the quantity scan")]
    [InlineData("ייצוא מדידות לבדיקה נעצר: מקור השרטוט אינו תואם לסריקה: the saved DWG bytes changed since the quantity scan; withdrawal failed")]
    public void OtherFailureTypesAndStagesAreNotReclassified(string text)
    {
        var error = new InvalidOperationException(text);
        Assert.Same(error, MeasurementDraftErrorText.Describe(error));
    }

    [Fact]
    public void IoExceptionWithSimilarTextIsNeverTurnedIntoAnOrdinaryRescanInstruction()
    {
        var error = new IOException("ייצוא מדידות לבדיקה נעצר: מקור השרטוט אינו תואם לסריקה: the saved DWG bytes changed since the quantity scan");
        Assert.Same(error, MeasurementDraftErrorText.Describe(error));
    }
}
