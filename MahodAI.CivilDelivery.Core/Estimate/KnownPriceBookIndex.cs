using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.CivilDelivery.Estimate
{
    /// <summary>
    /// 1.4.1 B0 (project 984, live b34 06.10): price books are registered per project (copied next to the profile), so
    /// every new project started with no active price book and its first proposals had no catalog. This per-user index
    /// remembers the books this engineer already registered (stored copy, SHA-256, edition, explicit column mapping) so a
    /// new project can OFFER them. Nothing is selected or copied automatically: the offer shows publisher, edition and
    /// item count, the engineer chooses, and registration then runs through <see cref="PriceBookRegistry.Register"/>
    /// with the remembered hash as the preview binding. A file that is gone or whose bytes changed is not offered.
    /// Pure: the index location is supplied by the caller.
    /// </summary>
    public static class KnownPriceBookIndex
    {
        public const string FileName = "known-price-books.json";
        public const int MaxEntries = 20;

        public sealed record Entry(
            [property: JsonPropertyName("path")] string Path,
            [property: JsonPropertyName("sha256")] string Sha256,
            [property: JsonPropertyName("publisher")] string? Publisher,
            [property: JsonPropertyName("edition")] string? Edition,
            [property: JsonPropertyName("item_count")] int? ItemCount,
            [property: JsonPropertyName("mapping")] ProjectProfile.EstimateProfile.PriceBookColumnMapping? Mapping,
            [property: JsonPropertyName("registered_by")] string? RegisteredBy,
            [property: JsonPropertyName("registered_at_utc")] DateTime? RegisteredAtUtc);

        /// <summary>A verified offer: the file exists now and still has the remembered bytes.</summary>
        public sealed record Offer(Entry Entry)
        {
            public string Label =>
                $"{Entry.Publisher ?? "מחירון"}" +
                (string.IsNullOrWhiteSpace(Entry.Edition) ? "" : $" · {Entry.Edition}") +
                (Entry.ItemCount is { } n ? $" · {n:N0} סעיפים" : "") +
                $" · {System.IO.Path.GetFileName(Entry.Path)}";
        }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        /// <summary>Records a book right after a successful <see cref="PriceBookRegistry.Register"/>.</summary>
        public static void Remember(string indexDirectory, PriceBookRegistry.RegisterResult registered)
        {
            ArgumentNullException.ThrowIfNull(registered);
            var e = registered.Entry;
            if (!CatalogIdentity.IsValidSha256(e.FileHash)) return;
            var entries = Read(indexDirectory)
                .Where(x => !string.Equals(x.Sha256, e.FileHash, StringComparison.OrdinalIgnoreCase) ||
                            !PriceBookRegistry.SameMapping(x.Mapping, e.Mapping))
                .ToList();
            entries.Insert(0, new Entry(registered.StoredPath, e.FileHash!.ToUpperInvariant(), e.Publisher, e.Edition,
                e.ItemCount, e.Mapping, e.RegisteredBy, e.RegisteredAtUtc));
            Write(indexDirectory, entries.Take(MaxEntries).ToList());
        }

        /// <summary>
        /// Offers for a project: remembered books whose file exists and still hashes to the remembered SHA-256, minus
        /// the bytes+reading the project already has. Never selects; the order is most recent first.
        /// </summary>
        public static IReadOnlyList<Offer> Offers(string indexDirectory, ProjectProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var offers = new List<Offer>();
            foreach (var entry in Read(indexDirectory))
            {
                if (profile.Estimate.PriceBooks.Any(b =>
                        string.Equals(b.FileHash, entry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                        PriceBookRegistry.SameMapping(b.Mapping, entry.Mapping)))
                    continue;
                if (!File.Exists(entry.Path)) continue;
                string hash;
                try { hash = ArtifactHash.Sha256OfFile(entry.Path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                offers.Add(new Offer(entry));
            }
            return offers;
        }

        /// <summary>The remembered entries; an unreadable or malformed index reads as empty (it is only a convenience).</summary>
        public static List<Entry> Read(string indexDirectory)
        {
            try
            {
                var path = System.IO.Path.Combine(indexDirectory, FileName);
                if (!File.Exists(path)) return new List<Entry>();
                return (JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json) ?? new List<Entry>())
                    .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Path) && CatalogIdentity.IsValidSha256(e.Sha256))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new List<Entry>();
            }
        }

        private static void Write(string indexDirectory, List<Entry> entries)
        {
            Directory.CreateDirectory(indexDirectory);
            var path = System.IO.Path.Combine(indexDirectory, FileName);
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, Json));
            File.Move(temp, path, overwrite: true);
        }
    }
}
