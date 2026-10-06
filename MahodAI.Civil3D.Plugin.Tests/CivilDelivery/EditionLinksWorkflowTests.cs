using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Estimate.Recognition;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The edition-link workflow outside Civil: the dialog's selection rules, and the profile write through the CAS
/// against the active price list only. Price-list rows and quantities are SYNTHETIC.
/// </summary>
public sealed class EditionLinksWorkflowTests : IDisposable
{
    private const string Hash = "1797086f48504d3fa3f3aa541a8757e00e5fdad17cc2b3ee7e709110be6dcb2b";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mhd-edition-save-" + Guid.NewGuid().ToString("N"));

    public EditionLinksWorkflowTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static CatalogSnapshot Catalog(string hash = Hash)
    {
        var catalog = new CatalogSnapshot { SnapshotId = "nti-unified-2026-07", FileHash = hash };
        void Add(string code, string description, string unit, decimal? price)
        {
            catalog.Items[code] = new CatalogItem { Code = code, Description = description, UnitRaw = unit };
            catalog.Prices[code] = new PriceRecord { Code = code, Price = price, PriceBookId = catalog.SnapshotId, SourceHash = hash };
        }
        Add("51.01.1080", "ריסוס והדברה בשטחי סלילה", "מ\"ר", 3.5m);
        Add("51.06.1578", "אבן גן בחתך 10/20 ס\"מ עם פאזה ובאורך 100", "מטר", 80m);
        Add("51.06.1582", "אבן גן בחתך 10/20 ס\"מ עם פאזה ובאורך 100", "מטר", 95m);
        Add("51.04.1430", "מדרכות ואיים מוגבהים בעובי 3 ס''מ עם אגרגט גס גירי/דולומיטי סוג א' וביטומן PG68-10", "מ\"ר", 30m);
        Add("51.04.1440", "מדרכות ואיים מוגבהים בעובי 4 ס''מ עם אגרגט גס גירי/דולומיטי סוג א' וביטומן PG68-10", "מ\"ר", 34m);
        return catalog;
    }

    private static ProjectProfile ActiveProfile(string hash = Hash)
    {
        var profile = new ProjectProfile { ProfileId = "EDITION-SAVE", ProjectName = "SIMULATION ONLY" };
        profile.Estimate.Pricing.PriceBookSnapshotId = "nti-unified-2026-07";
        profile.Estimate.Pricing.PriceBookHash = hash;
        profile.Estimate.Catalog.CatalogFile = "nti-unified-2026-07.xlsx";
        profile.Estimate.Catalog.CatalogFileHash = hash;
        profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
        {
            Id = "nti-unified-2026-07", Publisher = "נתיבי ישראל", Edition = "07-2026", File = "nti-unified-2026-07.xlsx", FileHash = hash,
        });
        return profile;
    }

    [Fact]
    public void OnlyUniqueExactTextMatchesAreMarkedAndNothingIsPreselected()
    {
        var proposals = EstimateWorkflowService.EditionLinkProposals(ActiveProfile(), Catalog());
        var exact = EditionLinksDialog.UniqueExactMatches(proposals);
        exact.Should().Contain(new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText));
        exact.Should().NotContain(r => r.LibraryCode == "U51.06.3060", "two cut rows share the garden-curb text: an ambiguity");
        exact.Should().NotContain(r => r.CatalogCode == "51.04.1430", "3 cm is not the library's 4 cm");
        EditionLinksDialog.Reviewable(proposals).Should().OnlyContain(p => !p.PresentInCatalog && p.Reference != null);
    }

    [Fact]
    public void LinksAreSavedAgainstTheActiveListOnlyAndApplyOnTheNextLoad()
    {
        var profile = ActiveProfile();
        var path = Path.Combine(_directory, "project-profile.yaml");
        var expected = ProjectProfileWriter.CaptureExpectedGeneratedState(profile, EstimateTraceIdentity.EffectiveProfileHash(profile), path);
        var service = new EstimateWorkflowService();
        var requests = new[]
        {
            new EditionLinkRequest("U51.01.2000", "51.01.1080", EditionMatchKind.ExactText),
            new EditionLinkRequest("U51.06.3060", "51.06.1582", EditionMatchKind.TruncatedPrefix),
        };

        FluentActions.Invoking(() => service.SaveEditionLinks(profile, Catalog(new string('b', 64)), requests, "SYNTHETIC engineer", "x", path, expected))
            .Should().Throw<InvalidOperationException>().WithMessage("*המחירון הפעיל*");
        FluentActions.Invoking(() => service.SaveEditionLinks(profile, Catalog(), requests, "SYNTHETIC engineer", " ", path, expected))
            .Should().Throw<ArgumentException>();
        profile.Estimate.EditionLinks.Should().BeEmpty("a refused batch changes nothing");

        var saved = service.SaveEditionLinks(profile, Catalog(), requests, "SYNTHETIC engineer", "גוון אפור לפי תכנית הפיתוח", path, expected);

        saved.Path.Should().Be(path);
        var loaded = ProjectProfileLoader.LoadFromFile(path);
        loaded.IsUsable.Should().BeTrue(string.Join("; ", loaded.Findings.Select(f => f.Code + ":" + f.Title)));
        loaded.Profile!.SchemaVersion.Should().Be(EditionLinkPolicy.EditionLinksSchemaVersion);
        var linked = EditionLinkPolicy.WithLibraryAliases(Catalog(), loaded.Profile.Estimate.EditionLinks, out var stale);
        stale.Should().BeEmpty();
        linked.ResolveLibraryCode("U51.01.2000").Should().Be("51.01.1080");
        linked.ResolveLibraryCode("U51.06.3060").Should().Be("51.06.1582");
        loaded.Profile.Estimate.EditionLinks.Should().OnlyContain(l => l.ApprovedBy == "SYNTHETIC engineer" && l.Status == "active");
    }

    [Fact]
    public void TheDialogShowsEveryReviewableCodeWithNothingCheckedButKeep()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var proposals = EstimateWorkflowService.EditionLinkProposals(ActiveProfile(), Catalog());
                var dialog = new EditionLinksDialog(proposals, Catalog(), Array.Empty<string>(), "SYNTHETIC engineer");
                dialog.Requests.Should().BeEmpty();
                dialog.ApprovedBy.Should().Be("SYNTHETIC engineer");
                dialog.Reason.Should().BeEmpty();
                dialog.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure.Should().BeNull(failure?.ToString());
    }
}
