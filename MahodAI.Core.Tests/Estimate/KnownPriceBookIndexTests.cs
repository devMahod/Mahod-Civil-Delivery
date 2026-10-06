using System;
using System.IO;
using FluentAssertions;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Core.Tests.Estimate;

/// <summary>
/// 1.4.1 B0 (984 live, 06.10): a new project had no active price book. A per-user index offers books this engineer
/// already registered elsewhere — verified by SHA-256, never selected or copied automatically.
/// </summary>
public sealed class KnownPriceBookIndexTests : IDisposable
{
    private const string Approver = "TEST-ONLY engineer";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_known_pb_" + Guid.NewGuid().ToString("N")[..8]);
    private string IndexDir => Path.Combine(_dir, "index");

    public KnownPriceBookIndexTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Book(string name = "nti-test.xlsx")
    {
        var book = new MiniXlsx.Workbook { SheetName = "TEST-ONLY" };
        string[][] rows =
        {
            new[] { "סעיף", "תאור", "יחידה", "מחיר" },
            new[] { "51.32.1850", "קו ניתוב ברוחב 10 ס\"מ TEST-ONLY", "מטר", "20" },
            new[] { "51.06.0100", "אבן-שפה TEST-ONLY", "מטר", "115" },
        };
        for (var r = 0; r < rows.Length; r++)
        {
            var row = new MiniXlsx.OutRow(r + 1);
            for (var c = 0; c < rows[r].Length; c++) row.Text(((char)('A' + c)).ToString(), rows[r][c], 0);
            book.Rows.Add(row);
        }
        var path = Path.Combine(_dir, "incoming", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        MiniXlsx.Write(book, path);
        return path;
    }

    private static ProjectProfile Project(string id) => new() { ProfileId = id, ProjectName = "SIMULATION ONLY" };

    [Fact]
    public void A_book_registered_in_one_project_is_offered_to_a_new_project_and_registers_by_its_hash()
    {
        var first = Project("6422");
        var registered = PriceBookRegistry.Register(first, Path.Combine(_dir, "p6422"), Book(), Approver, makeActive: true);
        KnownPriceBookIndex.Remember(IndexDir, registered);

        KnownPriceBookIndex.Offers(IndexDir, first).Should().BeEmpty("the project already has these bytes and reading");

        var fresh = Project("984");
        var offers = KnownPriceBookIndex.Offers(IndexDir, fresh);
        offers.Should().ContainSingle();
        offers[0].Entry.Sha256.Should().Be(registered.Entry.FileHash!.ToUpperInvariant());
        offers[0].Label.Should().Contain("סעיפים");
        fresh.Estimate.PriceBooks.Should().BeEmpty("an offer never registers or activates anything by itself");

        var again = PriceBookRegistry.Register(fresh, Path.Combine(_dir, "p984"), offers[0].Entry.Path, Approver,
            makeActive: true, expectedInspectionHash: offers[0].Entry.Sha256);
        again.Entry.FileHash.Should().BeEquivalentTo(registered.Entry.FileHash);
        PriceBookRegistry.Active(fresh)!.Id.Should().Be(again.Entry.Id);
    }

    [Fact]
    public void A_changed_or_missing_file_is_not_offered()
    {
        var registered = PriceBookRegistry.Register(Project("6422"), Path.Combine(_dir, "p6422"), Book(), Approver);
        KnownPriceBookIndex.Remember(IndexDir, registered);
        KnownPriceBookIndex.Offers(IndexDir, Project("984")).Should().ContainSingle();

        File.AppendAllText(registered.StoredPath, "tampered");
        KnownPriceBookIndex.Offers(IndexDir, Project("984")).Should().BeEmpty("the bytes no longer match the remembered SHA-256");

        File.Delete(registered.StoredPath);
        KnownPriceBookIndex.Offers(IndexDir, Project("984")).Should().BeEmpty();
    }

    [Fact]
    public void Remembering_the_same_book_twice_keeps_one_entry_and_a_broken_index_reads_as_empty()
    {
        var a = PriceBookRegistry.Register(Project("A"), Path.Combine(_dir, "pA"), Book(), Approver);
        var b = PriceBookRegistry.Register(Project("B"), Path.Combine(_dir, "pB"), Book(), Approver);
        KnownPriceBookIndex.Remember(IndexDir, a);
        KnownPriceBookIndex.Remember(IndexDir, b);
        KnownPriceBookIndex.Read(IndexDir).Should().ContainSingle().Which.Path.Should().Be(b.StoredPath);

        File.WriteAllText(Path.Combine(IndexDir, KnownPriceBookIndex.FileName), "{ not json");
        KnownPriceBookIndex.Read(IndexDir).Should().BeEmpty();
        KnownPriceBookIndex.Offers(IndexDir, Project("984")).Should().BeEmpty();
    }
}
