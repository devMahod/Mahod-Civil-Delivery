using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class KnownPriceBookReuseTests : IDisposable
{
    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    public void PreWriteDirtyRefusalHasHebrewRecoveryAndPreservesOriginalFailure(int dbmod)
    {
        var failure = new InvalidOperationException("רישום מחירון מוכר requires one saved, unchanged active drawing: " +
            EstimateSourceSnapshotPolicy.InitialFailure(new string('a', 64), dbmod));
        var shown = KnownPriceBookReuse.DescribePreWriteFailure(failure);
        shown.Message.Should().Contain("שמור את השרטוט ב-Civil").And.Contain("לחץ שוב")
            .And.Contain("לא נמחקה הסריקה הקיימת").And.Contain($"DBMOD={dbmod}")
            .And.NotContain("requires one saved");
        shown.InnerException.Should().BeSameAs(failure);
    }

    [Theory]
    [InlineData("profile bytes changed")]
    [InlineData("רישום מחירון מוכר requires one saved, unchanged active drawing: cannot hash drawing")]
    [InlineData("רישום מחירון מוכר requires one saved, unchanged active drawing: the active drawing has unsaved changes (DBMOD=0)")]
    [InlineData("רישום מחירון מוכר requires one saved, unchanged active drawing: the active drawing has unsaved changes (DBMOD=16)\ninner failure")]
    public void UnrelatedPreWriteFailureIsNotReclassifiedAsSaveRecovery(string message)
    {
        var failure = new InvalidOperationException(message);
        KnownPriceBookReuse.DescribePreWriteFailure(failure).Should().BeSameAs(failure);
        var io = new IOException(message);
        KnownPriceBookReuse.DescribePreWriteFailure(io).Should().BeSameAs(io);
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mhd-known-reuse-" + Guid.NewGuid().ToString("N"));
    public KnownPriceBookReuseTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { Directory.Delete(_dir, true); }
    private string Index => Path.Combine(_dir, "index");
    private PriceBookRegistry.RegisterResult Registered(bool automatic = false)
    {
        var book = new MiniXlsx.Workbook { SheetName = "SYNTHETIC-ONLY" };
        string[][] rows = { new[] { "קוד", "תיאור", "יחידה", "מחיר קודם", "מחיר" }, new[] { "51.01.0250", "TEST ONLY", "מטר", "30", "95" } };
        if (automatic) rows = new[] { new[] { "קוד", "תיאור", "יחידה", "מחיר" }, new[] { "51.01.0250", "TEST ONLY", "מטר", "95" } };
        for (var r = 0; r < rows.Length; r++)
        { var row = new MiniXlsx.OutRow(r + 1); for (var c = 0; c < rows[r].Length; c++) row.Text(((char)('A' + c)).ToString(), rows[r][c], 0); book.Rows.Add(row); }
        var path = Path.Combine(_dir, "incoming.xlsx"); MiniXlsx.Write(book, path);
        var mapping = new PriceBookXlsxLoader.ColumnMapping(ArtifactHash.Sha256OfFile(path), "SYNTHETIC-ONLY", 1, "A", "B", "C", "E");
        return PriceBookRegistry.Register(new ProjectProfile { ProfileId = "TEST-OLD" }, Path.Combine(_dir, "old"), path,
            "TEST-ONLY approver", publisher: "SYNTHETIC", edition: "TEST-ONLY", makeActive: true, expectedInspectionHash: mapping.ExpectedFileHash, mapping: automatic ? null : mapping);
    }

    private (ProjectProfile Profile, string Path, ProjectProfileWriter.ExpectedProfileState Cas) Target()
    {
        var p = new ProjectProfile { ProfileId = "TEST-NEW" }; var path = Path.Combine(_dir, "new", "project-profile.yaml");
        var saved = ProfileCasTest.Save(p, path, "TEST-ONLY initial", "TEST-ONLY approver");
        return (ProjectProfileLoader.LoadFromFile(path).Profile!, path, ProjectProfileWriter.CaptureExpectedState(path, saved.NewHash, path));
    }

    [Fact]
    public void ExplicitMappingSurvivesIndexServiceSaveReopenAndReturnsSelectedPrice()
    {
        var first = Registered(); KnownPriceBookIndex.Remember(Index, first);
        var t = Target(); var offer = KnownPriceBookIndex.Offers(Index, t.Profile).Single();
        t.Profile.Estimate.PriceBooks.Should().BeEmpty();
        var result = new EstimateWorkflowService().RegisterPriceBook(t.Profile, offer.Entry.Path, "TEST-ONLY new approver", t.Path, t.Cas,
            publisher: offer.Entry.Publisher, edition: offer.Entry.Edition, makeActive: true,
            expectedInspectionHash: offer.Entry.Sha256, mapping: KnownPriceBookReuse.Mapping(offer));
        var reopened = ProjectProfileLoader.LoadFromFile(t.Path); reopened.IsUsable.Should().BeTrue();
        var active = PriceBookRegistry.Active(reopened.Profile!)!;
        PriceBookRegistry.SameMapping(first.Entry.Mapping, active.Mapping).Should().BeTrue();
        active.Mapping!.PriceColumn.Should().Be("E"); active.FileHash.Should().BeEquivalentTo(first.Entry.FileHash);
        var inspection = PriceBookXlsxLoader.Inspect(result.StoredPath, PriceBookRegistry.LoaderMapping(active)!);
        inspection.IsUsable.Should().BeTrue();
        var snapshot = PriceBookXlsxLoader.Load(result.StoredPath, active.Id!, PriceBookRegistry.LoaderMapping(active)!);
        snapshot.Prices.Values.Single().Price.Should().Be(95m);
        var independentlyReloaded = ProjectProfileLoader.LoadFromFile(t.Path);
        independentlyReloaded.Profile.Should().NotBeSameAs(reopened.Profile);
        KnownPriceBookReuse.RequirePublished(independentlyReloaded.Profile, independentlyReloaded.ProfileHash, t.Path, reopened.Profile!, reopened.ProfileHash!, t.Path);
        KnownPriceBookReuse.Remember(Index, result).Should().BeNull();
        KnownPriceBookIndex.Offers(Index, reopened.Profile!).Should().BeEmpty();
    }

    [Fact]
    public void MappedBytesChangedAfterOfferRejectInLoaderWithoutChangingProfile()
    {
        var first = Registered(); KnownPriceBookIndex.Remember(Index, first); var t = Target();
        var offer = KnownPriceBookIndex.Offers(Index, t.Profile).Single(); var before = File.ReadAllBytes(t.Path);
        File.AppendAllText(offer.Entry.Path, "TEST-ONLY changed after offer");
        Action commit = () => new EstimateWorkflowService().RegisterPriceBook(t.Profile, offer.Entry.Path, "TEST-ONLY", t.Path, t.Cas,
            makeActive: true, expectedInspectionHash: offer.Entry.Sha256, mapping: KnownPriceBookReuse.Mapping(offer));
        commit.Should().Throw<InvalidOperationException>().WithMessage("הקובץ אינו מחירון קריא: *"); File.ReadAllBytes(t.Path).Should().Equal(before);
        t.Profile.Estimate.PriceBooks.Should().BeEmpty();
    }

    [Fact]
    public void AutomaticOfferChangedBytesAreRejectedByPreviewHashNotMapping()
    {
        var first = Registered(automatic: true); KnownPriceBookIndex.Remember(Index, first); var t = Target();
        var offer = KnownPriceBookIndex.Offers(Index, t.Profile).Single(); var before = File.ReadAllBytes(t.Path);
        KnownPriceBookReuse.Mapping(offer).Should().BeNull();
        File.AppendAllText(offer.Entry.Path, "SYNTHETIC changed bytes after review");
        PriceBookXlsxLoader.Inspect(offer.Entry.Path).IsUsable.Should().BeTrue("the workbook is still readable, only its reviewed bytes changed");
        Action commit = () => new EstimateWorkflowService().RegisterPriceBook(t.Profile, offer.Entry.Path, "TEST-ONLY", t.Path, t.Cas,
            makeActive: true, expectedInspectionHash: offer.Entry.Sha256, mapping: KnownPriceBookReuse.Mapping(offer));
        commit.Should().Throw<InvalidOperationException>().WithMessage("קובץ המחירון השתנה מאז התצוגה המקדימה. יש לבחור שוב \"טען מחירון\", לבדוק את התצוגה המקדימה ולאשר מחדש.");
        File.ReadAllBytes(t.Path).Should().Equal(before); t.Profile.Estimate.PriceBooks.Should().BeEmpty();
    }

    [Fact]
    public void FailedConvenienceIndexDoesNotUndoRegisteredBook()
    {
        var registered = Registered(); var hash = ArtifactHash.Sha256OfFile(registered.StoredPath);
        var error = KnownPriceBookReuse.Remember(Index, registered, (_, _) => throw new IOException("TEST-ONLY disk failure"));
        error.Should().Contain("נרשם בפרויקט"); ArtifactHash.Sha256OfFile(registered.StoredPath).Should().Be(hash);
        Action unexpected = () => KnownPriceBookReuse.Remember(Index, registered, (_, _) => throw new InvalidOperationException("logic bug"));
        unexpected.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("empty")][InlineData("hash")][InlineData("target")][InlineData("mapping")][InlineData("profile")]
    public void WrongReloadCannotPublishSuccess(string mismatch)
    {
        var result = Registered(); var expected = new ProjectProfile { ProfileId = "TEST" };
        expected.Estimate.PriceBooks.Add(result.Entry); PriceBookRegistry.MakeActive(expected, result.Entry.Id!, Path.GetDirectoryName(result.StoredPath));
        var actual = JsonSerializer.Deserialize<ProjectProfile>(JsonSerializer.Serialize(expected))!;
        if (mismatch == "empty") actual.Estimate.PriceBooks.Clear();
        if (mismatch == "mapping") actual.Estimate.PriceBooks.Single().Mapping!.PriceColumn = "D";
        if (mismatch == "profile") actual.ProfileId = "FOREIGN";
        Action publish = () => KnownPriceBookReuse.RequirePublished(actual, mismatch == "hash" ? "wrong" : "same",
            mismatch == "target" ? "other" : "target", expected, "same", "target");
        publish.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void KnownUiCapturesScopeBeforeReviewAndRegistersExactHashAndMappingOnlyAfterConsent()
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.KnownPriceBooks.cs"));
        var method = source[source.IndexOf("private void OnKnownPriceBooks(", StringComparison.Ordinal)..];
        var capture = method.IndexOf("CaptureProfileDecisionScope(", StringComparison.Ordinal);
        var review = method.IndexOf("CivilModalHost.ShowFromPalette(review)", StringComparison.Ordinal);
        var approver = method.IndexOf("RequireApprover(", StringComparison.Ordinal);
        var check = method.IndexOf("RequireProfileDecisionScope(scope)", StringComparison.Ordinal);
        var register = method.IndexOf("_estimate.RegisterPriceBook(", StringComparison.Ordinal);
        (capture >= 0 && capture < review && review < approver && approver < check && check < register).Should().BeTrue();
        method.Should().Contain("review.Accepted is not { } chosen").And.Contain("expectedProfileState: scope.ExpectedState")
            .And.Contain("expectedInspectionHash: chosen.Entry.Sha256, mapping: KnownPriceBookReuse.Mapping(chosen)");
        (register < method.IndexOf("PublishPriceBookProfile(", StringComparison.Ordinal)).Should().BeTrue();
        method.Split("_estimate.RegisterPriceBook(").Length.Should().Be(2, "no retry without the approved binding");
        method.Should().Contain("catch (Exception ex) { ShowError(\"מחירון מוכר\", KnownPriceBookReuse.DescribePreWriteFailure(ex)); return; }")
            .And.Contain("if (writeAttempted)");
        method.IndexOf("writeAttempted = true;", StringComparison.Ordinal).Should().BeGreaterThan(check);
    }

    [Theory]
    [InlineData("CivilDeliveryControl.KnownPriceBooks.cs", "OnKnownPriceBooks")]
    [InlineData("CivilDeliveryControl.xaml.cs", "OnLoadPriceBook")]
    public void BothRegistrationRoutesRememberOnlyAfterVerifiedPublishOutsideFailureCatch(string file, string handler)
    {
        var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", file));
        var start = source.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
        var end = source.IndexOf("\n        private ", start + 20, StringComparison.Ordinal);
        var method = end < 0 ? source[start..] : source[start..end];
        var publish = method.IndexOf("PublishPriceBookProfile(", StringComparison.Ordinal);
        var final = method.LastIndexOf("finally { RefreshGates(); }", StringComparison.Ordinal);
        var remember = method.IndexOf("if (published != null) RememberPriceBook(published);", StringComparison.Ordinal);
        publish.Should().BeGreaterThan(0); final.Should().BeGreaterThan(publish); remember.Should().BeGreaterThan(final);
        method.Should().Contain("if (writeAttempted)");
        source.Should().NotContain("RememberPriceBook(r);").And.NotContain("RememberPriceBook(registered);");
    }
}
