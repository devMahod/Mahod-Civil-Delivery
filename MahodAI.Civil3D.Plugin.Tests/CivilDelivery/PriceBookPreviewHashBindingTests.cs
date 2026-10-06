using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    /// <summary>
    /// "טען מחירון" shows a preview of one workbook and asks the engineer to approve it.
    /// Registration must store exactly the bytes that preview read: when the file is
    /// replaced between preview and approval, the registry refuses before any profile,
    /// directory or file change, and the engineer previews the current file again.
    /// Every workbook here is a TEST-ONLY synthetic sheet; nothing here is native evidence,
    /// and column ambiguity / unparsed rows / edition updates are deliberately not covered.
    /// </summary>
    public class PriceBookPreviewHashBindingTests : IDisposable
    {
        private const string Code = "51.01.0250";
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcd_pvh_" + Guid.NewGuid().ToString("N")[..8]);

        public PriceBookPreviewHashBindingTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        // ------------------------------------------------------------ fixtures

        /// <summary>A registry that already knows an unrelated book, which must survive untouched.</summary>
        private static ProjectProfile Profile()
        {
            var profile = new ProjectProfile { ProfileId = "TEST-ONLY-PREVIEW-HASH" };
            profile.Estimate.PriceBooks.Add(new ProjectProfile.EstimateProfile.PriceBookEntry
            {
                Id = "prior",
                File = "prior.xlsx",
                FileHash = new string('a', 64),
                Notes = "TEST-ONLY unrelated entry",
            });
            return profile;
        }

        /// <summary>One-item contractor-style sheet: תיאור | קוד | מחיר יחידה | יח'.</summary>
        private static string WriteBook(string path, string price)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var book = new MiniXlsx.Workbook { SheetName = "TEST-ONLY" };
            string[][] rows =
            {
                new[] { "תיאור", "קוד", "מחיר יחידה", "יח'" },
                new[] { "TEST-ONLY curb", Code, price, "מטר" },
            };
            for (var r = 0; r < rows.Length; r++)
            {
                var row = new MiniXlsx.OutRow(r + 1);
                for (var c = 0; c < rows[r].Length; c++)
                    row.Text(((char)('A' + c)).ToString(), rows[r][c], 0);
                book.Rows.Add(row);
            }
            MiniXlsx.Write(book, path);   // FileMode.Create: a second call replaces the bytes
            return path;
        }

        private string Incoming(string name = "book.xlsx") => Path.Combine(_dir, "incoming", name);

        /// <summary>Every directory and file under <paramref name="directory"/>, with file SHA-256.</summary>
        private static string TreeState(string directory)
        {
            if (!Directory.Exists(directory)) return "ABSENT";
            return string.Join("\n", Directory
                .GetFileSystemEntries(directory, "*", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(directory, p) +
                             (File.Exists(p) ? " " + ArtifactHash.Sha256OfFile(p) : "/"))
                .OrderBy(s => s, StringComparer.Ordinal));
        }

        /// <summary>Runs a registration that must be refused and proves it changed nothing.</summary>
        private static T RefusedWithoutChange<T>(ProjectProfile profile, string destination, Action register)
            where T : Exception
        {
            var profileBefore = JsonSerializer.Serialize(profile.Estimate);
            var treeBefore = TreeState(destination);

            var refusal = register.Should().ThrowExactly<T>().Which;

            JsonSerializer.Serialize(profile.Estimate).Should().Be(profileBefore,
                "a refused registration leaves the in-memory registry untouched");
            TreeState(destination).Should().Be(treeBefore,
                "a refused registration creates, copies and deletes nothing");
            return refusal;
        }

        public static TheoryData<string> MalformedHashes => new()
        {
            "",
            new string('a', 63),
            new string('a', 65),
            new string('g', 64),
            " " + new string('a', 63),
            new string('a', 64) + " ",
            new string('a', 63) + "\n",
            new string('ａ', 64),   // full-width 'a' is not an ASCII hex digit
        };

        // ------------------------------------------------------------ registry

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void PreviewedBytes_AreRegisteredAndActivated_WithThePreviewHash(bool upperCase)
        {
            var source = WriteBook(Incoming(), "95");
            var preview = PriceBookXlsxLoader.Inspect(source);
            preview.IsUsable.Should().BeTrue();
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");

            var r = PriceBookRegistry.Register(profile, registry, source, "nataly",
                id: "test-only-a", makeActive: true,
                expectedInspectionHash: upperCase ? preview.FileHash.ToUpperInvariant() : preview.FileHash);

            r.Entry.FileHash.Should().Be(preview.FileHash);
            ArtifactHash.Sha256OfFile(r.StoredPath).Should().Be(preview.FileHash, "the stored copy is the previewed bytes");
            PriceBookXlsxLoader.Load(r.StoredPath, "test-only-a").Prices[Code].Price.Should().Be(95m);
            PriceBookRegistry.Active(profile)!.Id.Should().Be("test-only-a");
            profile.Estimate.PriceBooks.Should().ContainSingle(e =>
                e.Id == "prior" && e.Notes == "TEST-ONLY unrelated entry");
        }

        [Fact]
        public void OmittedPreviewHash_KeepsExistingPositionalCallersCompiling_AndWorking()
        {
            var source = WriteBook(Incoming(), "95");
            var profile = Profile();

            // The eight positional arguments every existing caller can use; null is the
            // explicit compatibility mode, not a preview binding.
            var r = PriceBookRegistry.Register(profile, Path.Combine(_dir, "registry"), source, "nataly",
                "test-only-a", null, null, true);

            r.MadeActive.Should().BeTrue();
            r.Entry.FileHash.Should().Be(PriceBookXlsxLoader.Inspect(source).FileHash);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void FileReplacedAfterPreview_IsRefusedBeforeAnyRegistryOrFileChange(bool destinationExists)
        {
            var source = WriteBook(Incoming(), "95");
            var preview = PriceBookXlsxLoader.Inspect(source);
            WriteBook(source, "120");                       // an equally valid, different book replaces it
            var current = PriceBookXlsxLoader.Inspect(source);
            current.IsUsable.Should().BeTrue();
            current.FileHash.Should().NotBe(preview.FileHash);
            var registry = Path.Combine(_dir, "registry");
            if (destinationExists)
            {
                Directory.CreateDirectory(registry);
                File.WriteAllText(Path.Combine(registry, "unrelated.txt"), "TEST-ONLY must survive");
            }
            var profile = Profile();

            var refusal = RefusedWithoutChange<InvalidOperationException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, source, "nataly",
                    id: "test-only-a", makeActive: true, expectedInspectionHash: preview.FileHash));

            refusal.Message.Should().Contain("השתנה מאז התצוגה המקדימה");
        }

        [Fact]
        public void AfterARefusal_PreviewingTheCurrentFileAgain_RegistersWhatThatPreviewShowed()
        {
            var source = WriteBook(Incoming(), "95");
            var stale = PriceBookXlsxLoader.Inspect(source);
            WriteBook(source, "120");
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");
            RefusedWithoutChange<InvalidOperationException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, source, "nataly",
                    id: "test-only-a", makeActive: true, expectedInspectionHash: stale.FileHash));

            var fresh = PriceBookXlsxLoader.Inspect(source);
            var r = PriceBookRegistry.Register(profile, registry, source, "nataly",
                id: "test-only-a", makeActive: true, expectedInspectionHash: fresh.FileHash);

            r.Entry.FileHash.Should().Be(fresh.FileHash);
            PriceBookXlsxLoader.Load(r.StoredPath, "test-only-a").Prices[Code].Price.Should().Be(120m);
        }

        [Fact]
        public void WellFormedButDifferentHash_IsRefusedWithoutChange()
        {
            var source = WriteBook(Incoming(), "95");
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");

            var refusal = RefusedWithoutChange<InvalidOperationException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, source, "nataly",
                    id: "test-only-a", expectedInspectionHash: new string('0', 64)));

            refusal.Message.Should().Contain("השתנה מאז התצוגה המקדימה");
        }

        [Theory]
        [MemberData(nameof(MalformedHashes))]
        public void MalformedPreviewHash_IsRejectedBeforeTheWorkbookIsRead(string malformed)
        {
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");
            var neverWritten = Path.Combine(_dir, "never-written.xlsx");

            var refusal = RefusedWithoutChange<ArgumentException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, neverWritten, "nataly",
                    expectedInspectionHash: malformed));

            refusal.ParamName.Should().Be("expectedInspectionHash",
                "the format guard runs before Inspect touches the (missing) path");
        }

        [Fact]
        public void EngineerNameGuard_StillRunsFirst()
        {
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");

            var refusal = RefusedWithoutChange<ArgumentException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, Path.Combine(_dir, "never-written.xlsx"), "",
                    expectedInspectionHash: "bad"));

            refusal.ParamName.Should().Be("registeredBy");
        }

        [Fact]
        public void ImmutableIdGuard_StillRefusesNewBytesUnderAnExistingId_EvenWithTheirOwnPreviewHash()
        {
            var first = WriteBook(Incoming("a.xlsx"), "95");
            var second = WriteBook(Incoming("b.xlsx"), "120");
            var profile = Profile();
            var registry = Path.Combine(_dir, "registry");
            PriceBookRegistry.Register(profile, registry, first, "nataly", id: "test-only-edition",
                makeActive: true, expectedInspectionHash: PriceBookXlsxLoader.Inspect(first).FileHash);

            var refusal = RefusedWithoutChange<InvalidOperationException>(profile, registry, () =>
                PriceBookRegistry.Register(profile, registry, second, "nataly", id: "test-only-edition",
                    makeActive: true, expectedInspectionHash: PriceBookXlsxLoader.Inspect(second).FileHash));

            refusal.Message.Should().Contain("immutable");
        }

        // ------------------------------------------------------------ service

        [Fact]
        public void Service_ChangedFileAfterPreview_LeavesProfileFileRegistryAndFolderUntouched()
        {
            var profilePath = Path.Combine(_dir, "project-profile.yaml");
            var initial = ProfileCasTest.Save(new ProjectProfile { ProfileId = "TEST-ONLY-PREVIEW-HASH" },
                profilePath, "initial profile", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(profilePath, initial.NewHash, profilePath);
            var source = WriteBook(Incoming(), "95");
            var preview = PriceBookXlsxLoader.Inspect(source);
            WriteBook(source, "120");
            var candidate = new ProjectProfile { ProfileId = "TEST-ONLY-PREVIEW-HASH" };
            var folderBefore = TreeState(_dir);

            var act = () => new EstimateWorkflowService().RegisterPriceBook(
                candidate, source, "nataly", profilePath, expected,
                id: "test-only-a", makeActive: true, expectedInspectionHash: preview.FileHash);

            act.Should().ThrowExactly<InvalidOperationException>().WithMessage("*השתנה מאז התצוגה המקדימה*");
            TreeState(_dir).Should().Be(folderBefore,
                "no profile save, stored copy, staged copy or folder happens after a stale preview");
            candidate.Estimate.PriceBooks.Should().BeEmpty("the service restores its estimate snapshot");
            candidate.Estimate.Pricing.PriceBookSnapshotId.Should().BeNull();
        }

        [Fact]
        public void Service_UnchangedPreview_SavesAProfileThatReopensOnThePreviewedBytes()
        {
            var profilePath = Path.Combine(_dir, "project-profile.yaml");
            var initial = ProfileCasTest.Save(new ProjectProfile { ProfileId = "TEST-ONLY-PREVIEW-HASH" },
                profilePath, "initial profile", "arthur");
            var expected = ProjectProfileWriter.CaptureExpectedState(profilePath, initial.NewHash, profilePath);
            var source = WriteBook(Incoming(), "95");
            var preview = PriceBookXlsxLoader.Inspect(source);
            var candidate = new ProjectProfile { ProfileId = "TEST-ONLY-PREVIEW-HASH" };

            var r = new EstimateWorkflowService().RegisterPriceBook(
                candidate, source, "nataly", profilePath, expected,
                id: "test-only-a", makeActive: true, expectedInspectionHash: preview.FileHash);

            r.Entry.FileHash.Should().Be(preview.FileHash);
            var durable = ProjectProfileLoader.LoadFromFile(profilePath);
            durable.IsUsable.Should().BeTrue(string.Join("; ", durable.Findings.Select(f => f.Code + ":" + f.Title)));
            var active = PriceBookRegistry.Active(durable.Profile!)!;
            active.Id.Should().Be("test-only-a");
            active.FileHash.Should().Be(preview.FileHash);
            ArtifactHash.Sha256OfFile(Path.Combine(_dir, "test-only-a.xlsx")).Should().Be(preview.FileHash);
        }

        // ------------------------------------------------------------ source contract

        private static string PluginSource(params string[] parts) =>
            File.ReadAllText(Path.Combine(new[] { EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin" }
                    .Concat(parts).ToArray()))
                .Replace("\r\n", "\n", StringComparison.Ordinal);

        private static string Method(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            start.Should().BeGreaterThanOrEqualTo(0, "the production method {0} must exist", signature);
            var rest = source[(start + signature.Length)..];
            var next = Regex.Match(rest, @"(?m)^[ \t]+(?:private|internal|public)\s");
            return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
        }

        private static int At(string source, string token)
        {
            var index = source.IndexOf(token, StringComparison.Ordinal);
            index.Should().BeGreaterThanOrEqualTo(0, "the source contract requires {0}", token);
            return index;
        }

        private static int Count(string source, string token) =>
            Regex.Matches(source, Regex.Escape(token)).Count;

        [Fact]
        public void LoadPriceBookUi_RegistersOnlyTheInspectedPreview_WithNoUnboundRetry()
        {
            var body = Method(PluginSource("CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"),
                "private void OnLoadPriceBook(");

            var inspect = At(body, "insp = PriceBookXlsxLoader.Inspect(dlg.FileName)");
            // b15: the approval is the review window (preview, coverage, optional sheet/column choice).
            var approval = At(body, "CivilModalHost.ShowFromPalette(review)");
            var register = At(body, "_estimate.RegisterPriceBook(");
            inspect.Should().BeLessThan(approval);
            approval.Should().BeLessThan(register);
            Count(body, "PriceBookXlsxLoader.Inspect(").Should().Be(1, "the preview is the only inspection the UI shows");
            Count(body, "RegisterPriceBook(").Should().Be(1, "a refusal is never retried without the preview hash");
            var call = body[register..(body.IndexOf(");", register, StringComparison.Ordinal) + 2)];
            call.Should().Contain("expectedInspectionHash: approved.FileHash")
                .And.Contain("mapping: review.ApprovedMapping");
            body.Should().Contain("review.ApprovedInspection is not { IsUsable: true } approved",
                "only a usable reading the engineer approved is registered");
        }

        [Fact]
        public void Service_ForwardsThePreviewHashToItsOnlyRegistryCall_BeforeTheProfileIsWritten()
        {
            var body = Method(PluginSource("CivilDelivery", "Estimate", "EstimateWorkflowService.cs"),
                "public PriceBookRegistry.RegisterResult RegisterPriceBook(");

            body.Should().Contain("bool makeActive = false, string? expectedInspectionHash = null,")
                .And.Contain("PriceBookXlsxLoader.ColumnMapping? mapping = null)")
                .And.Contain("id, publisher, edition, makeActive, expectedInspectionHash, mapping);");
            Count(body, "PriceBookRegistry.Register(").Should().Be(1);
            At(body, "PriceBookRegistry.Register(").Should().BeLessThan(At(body, "ProjectProfileWriter.Save("),
                "the preview binding is checked before the profile is written");
        }

        [Fact]
        public void Production_HasExactlyOneRegistrationPath_ThroughTheService()
        {
            var root = Path.Combine(EstimateFixtures.RepoRoot(), "MahodAI.Civil3D.Plugin");
            var sources = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(p => !Path.GetRelativePath(root, p).Split(Path.DirectorySeparatorChar)
                    .Any(s => s is "obj" or "bin"))
                .ToDictionary(p => Path.GetRelativePath(root, p), p => File.ReadAllText(p));

            sources.Where(kv => kv.Value.Contains("PriceBookRegistry.Register(", StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .Should().Equal(Path.Combine("CivilDelivery", "Estimate", "EstimateWorkflowService.cs"));
            sources.Sum(kv => Count(kv.Value, "RegisterPriceBook("))
                .Should().Be(3, "one service definition, the inspected-file UI call, and the explicit hash+mapping-bound known-book UI call");
        }
    }
}
