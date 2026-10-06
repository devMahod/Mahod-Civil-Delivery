using System;
using System.IO;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services;
using MahodAI.Civil3D.Plugin.Tools.CivilDelivery;
using MahodAI.CivilDelivery.Shared;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery
{
    public class ActiveProjectProfileServiceTests
    {
        [Fact]
        public void ExplicitParameterWinsEnvironmentAndDrawingNumber()
        {
            var selected = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg",
                null,
                @"C:\Profiles\explicit.yaml",
                "7777");

            selected.Selector.Should().Be(@"C:\Profiles\explicit.yaml");
            selected.IsExplicit.Should().BeTrue();
        }

        [Fact]
        public void EnvironmentSelectorWinsDrawingNumberWhenParameterIsAbsent()
        {
            var selected = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg", null, null, "7777");

            selected.Selector.Should().Be("7777");
            selected.IsExplicit.Should().BeTrue();
        }

        [Theory]
        [InlineData(@"C:\Drawings\6422-CIVIL-WEST.dwg", "6422")]
        [InlineData(@"D:\Work\12345678_plan.dwg", "12345678")]
        public void LeadingProjectNumberSelectsThatProjectsProfile(
            string drawing, string expected)
        {
            var selected = ActiveProjectProfileService.SelectForDrawing(
                drawing, null, null, null);

            selected.Selector.Should().Be(expected);
            selected.IsExplicit.Should().BeFalse();
        }

        [Fact]
        public void UnrelatedDrawingsReceiveDifferentSafeProfilesAndNever6422()
        {
            var first = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\Campus-East.dwg", null, null, null);
            var second = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\Campus-West.dwg", null, null, null);

            first.Selector.Should().StartWith("drawing-campus-east-");
            second.Selector.Should().StartWith("drawing-campus-west-");
            first.Selector.Should().NotBe(second.Selector);
            first.Selector.Should().NotBe("6422");
            second.Selector.Should().NotBe("6422");
        }

        [Fact]
        public void SameFileNameInDifferentDirectoriesHasDifferentDeterministicProfiles()
        {
            var first = ActiveProjectProfileService.SelectForDrawing(
                @"C:\One\Campus-East.dwg", null, null, null);
            var second = ActiveProjectProfileService.SelectForDrawing(
                @"D:\Another\campus-east.DWG", null, null, null);

            first.Selector.Should().NotBe(second.Selector,
                "a generic filename is not authority to share CL or estimate decisions across projects");
        }

        [Fact]
        public void SameCanonicalDrawingPathIsCaseInsensitiveAndStable()
        {
            var first = ActiveProjectProfileService.SelectForDrawing(
                @"C:\One\Campus-East.dwg", null, null, null);
            var second = ActiveProjectProfileService.SelectForDrawing(
                @"c:\one\CAMPUS-EAST.DWG", null, null, null);

            first.Selector.Should().Be(second.Selector);
            first.Selector.Should().NotBe("6422");
        }

        [Fact]
        public void MissingDrawingProfileIsEmptyAndRoutesToSetupWithoutBleed()
        {
            var firstSelection = ActiveProjectProfileService.SelectForDrawing(
                null, "Unconfigured-Alpha-Unique.dwg", null, null);
            var first = ActiveProjectProfileService.CreateUnconfiguredResult(firstSelection);
            first.Profile!.Sections.Cl.LayerPatterns.Add("MUTATED-IN-FIRST");

            var secondSelection = ActiveProjectProfileService.SelectForDrawing(
                null, "Unconfigured-Beta-Unique.dwg", null, null);
            var second = ActiveProjectProfileService.CreateUnconfiguredResult(secondSelection);

            first.IsGeneratedForDrawing.Should().BeTrue();
            second.IsGeneratedForDrawing.Should().BeTrue();
            first.Profile.ProfileId.Should().NotBe(second.Profile!.ProfileId);
            second.Profile.Sections.Cl.LayerPatterns.Should().BeEmpty();
            second.Profile.Sections.Cl.SourceFiles.Should().BeEmpty();
            second.Profile.Sections.Alignments.AllowedNames.Should().BeEmpty();
            second.Profile.Sections.Sources.SampledSourceRules.Should().BeEmpty();
            second.Profile.Estimate.QuantitySources.Rules.Should().BeEmpty();
            ProjectSetupService.NeedsSetup(second.Profile).Should().BeTrue();
            second.Findings.Should().Contain(f =>
                f.Code == "SHR-PROFILE-DRAWING-UNCONFIGURED");
            second.ProfileHash.Should().NotBeNullOrWhiteSpace();
            second.ProfileWriteTarget.Should().Be(RuntimePath(second.Profile.ProfileId));
            second.ProfileSource.Should().Be(second.ProfileWriteTarget,
                "a generated seed is bound to the exact initially-absent runtime target");
        }

        [Fact]
        public void ExplicitAbsoluteProfileRemainsItsOwnWriteTarget()
        {
            var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mahod-profile-tests", "custom.yaml"));
            var selection = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg", null, path, null);

            ActiveProjectProfileService.ResolveWriteTarget(selection, path, null)
                .Should().Be(path);
        }

        [Fact]
        public void ExplicitRelativeProfileThatResolvedDirectlyRemainsItsOwnWriteTarget()
        {
            var relative = Path.Combine("profiles", "custom.yaml");
            var resolved = Path.GetFullPath(relative);
            var selection = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg", null, relative, null);

            ActiveProjectProfileService.ResolveWriteTarget(selection, resolved, null)
                .Should().Be(resolved);
        }

        [Fact]
        public void ProfileResolvedInsideEnvironmentDirectoryRemainsItsOwnWriteTarget()
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mahod-profile-dir"));
            var path = Path.Combine(root, "6422", "project-profile.yaml");
            var selection = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg", null, null, null);

            ActiveProjectProfileService.ResolveWriteTarget(selection, path, root)
                .Should().Be(path);
        }

        [Fact]
        public void DrawingIdLoadedFromShippedBaselineWritesToRuntimeProfile()
        {
            var selection = ActiveProjectProfileService.SelectForDrawing(
                @"C:\Drawings\6422-CIVIL-WEST.dwg", null, null, null);
            var shipped = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bundle", "profiles", "6422", "project-profile.yaml"));

            ActiveProjectProfileService.ResolveWriteTarget(selection, shipped, null)
                .Should().Be(RuntimePath("6422"));
        }

        [Fact]
        public void ExistingRuntimeProfileSupersedesThePlanSourceOnFreshnessReload()
        {
            var source = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(), "bundle", "profiles", "6422", "project-profile.yaml"));
            var target = RuntimePath("6422");

            ActiveProjectProfileService.AuthoritativeReloadSelector(
                    "6422", source, target,
                    path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                .Should().Be(target);
        }

        [Fact]
        public void MissingExpectedSourceDoesNotFallThroughToAnotherSameIdProfile()
        {
            var missingSource = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(), "deleted-custom-profile.yaml"));
            var missingTarget = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(), "deleted-custom-target.yaml"));

            ActiveProjectProfileService.AuthoritativeReloadSelector(
                    "6422", missingSource, missingTarget, _ => false)
                .Should().Be(missingSource,
                    "the exact PLAN source must be reread and fail if it disappeared");
        }

        [Fact]
        public void ProfileIdIsUsedOnlyWhenTheWorkflowHadNoExactSource()
        {
            ActiveProjectProfileService.AuthoritativeReloadSelector(
                    "6422", null, null, _ => false)
                .Should().Be("6422");
        }

        [Fact]
        public void ParsedProfileWithErrorIsNotUsableByPaletteWorkflows()
        {
            var invalid = ProjectProfileLoader.LoadFromText(
                "schema_version: 99\nprofile_id: invalid\nproject_name: Invalid\n");
            invalid.Profile.Should().NotBeNull("the regression requires a parsed-but-invalid profile");
            invalid.IsUsable.Should().BeFalse();
            var loaded = new ActiveProjectProfileService.ActiveLoadResult
            {
                Loaded = invalid,
                ProfileSource = @"C:\Profiles\invalid.yaml",
                ProfileWriteTarget = @"C:\Profiles\invalid.yaml",
            };

            ActiveProjectProfileService.SelectUsableProfile(loaded).Should().BeNull();
            loaded.ErrorFindings.Should().Contain(finding =>
                finding.Code == "SHR-PROFILE-SCHEMA-VERSION");
        }

        [Fact]
        public void MutationBoundariesRejectMissingOrRelativeWriteTargetsBeforeMutation()
        {
            var profile = new ProjectProfile { ProfileId = "6422" };

            var estimate = () => new EstimateWorkflowService().SaveEarthworksDecision(
                profile, includeEarthworks: true, reason: null,
                approvedBy: "nataly", targetPath: null!);
            estimate.Should().Throw<ArgumentException>();
            profile.Estimate.Earthworks.Requested.Should().BeNull();

            var session = () => CivilDeliverySession.SetScan(
                null!, profile, null, "relative-profile.yaml");
            session.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void PaletteReloadUsesUsabilityAndAuthoritativeWriteTarget()
        {
            var source = File.ReadAllText(Path.Combine(
                TestPaths.PluginSourceDir, "CivilDelivery", "UI",
                "CivilDeliveryControl.xaml.cs"));

            source.Should().Contain(
                "_profile = ActiveProjectProfileService.SelectUsableProfile(loaded)");
            source.Should().Contain("loaded.ErrorFindings.ToList()",
                "parsed profiles with Error findings must be visible while workflows stay blocked");
            source.Should().Contain("_profileWriteTarget = loaded.ProfileWriteTarget");
            source.Should().Contain("var profileUsable = _profile != null",
                "all palette workflow gates must share the parsed-profile validity decision");
            source.Should().Contain("BtnBuild.IsEnabled = profileUsable");
            source.Should().Contain("BtnExport.IsEnabled = profileUsable");
            source.Should().Contain("BtnTrace.IsEnabled = profileUsable");
            source.Should().Contain("_sectionRows.Clear()");
            source.Should().Contain("_quantityRows.Clear()");
            source.Should().Contain("RequireProfileWriteTarget()",
                "profile mutations must fail closed if their exact target was lost");
            source.Should().Contain("Path.IsPathFullyQualified(_profileWriteTarget)",
                "the palette must never persist an approval through a relative or ambiguous target");
            source.Should().NotContain(
                "ProjectProfileWriter." + "RuntimeProfile" + "Path(",
                "the palette must never re-resolve a mutation target independently of its load");
        }

        [Fact]
        public void DirectAndAiMutationRoutesCarryTheLoadedWriteTarget()
        {
            var relativeFiles = new[]
            {
                Path.Combine("CivilDelivery", "Commands", "MhdEstimateCommand.cs"),
                Path.Combine("CivilDelivery", "Commands", "MhdSectionsCommand.cs"),
                Path.Combine("CivilDelivery", "Commands", "MhdSetupCommand.cs"),
                Path.Combine("Tools", "CivilDelivery", "EstimateTools.cs"),
                Path.Combine("Tools", "CivilDelivery", "SectionsTools.cs"),
                Path.Combine("Tools", "CivilDelivery", "SetupTools.cs"),
            };

            foreach (var relative in relativeFiles)
            {
                var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, relative));
                source.Should().Contain("ProfileWriteTarget",
                    $"{relative} must preserve the source selected for this workflow session");
            }
        }

        [Fact]
        public void MutationServicesCannotSilentlyReResolveAWriteTarget()
        {
            var relativeFiles = new[]
            {
                Path.Combine("CivilDelivery", "Estimate", "EstimateWorkflowService.cs"),
                Path.Combine("CivilDelivery", "Sections", "Services", "ProjectSetupService.cs"),
                Path.Combine("Tools", "CivilDelivery", "CivilDeliverySession.cs"),
            };
            var forbidden = "RuntimeProfile" + "Path(";

            foreach (var relative in relativeFiles)
            {
                var source = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, relative));
                source.Should().NotContain(forbidden,
                    $"{relative} must require the authoritative target carried from load");
            }
        }

        private static string RuntimePath(string profileId) => Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "MahodAI_Civil3D", "civil-delivery", "profiles", profileId,
            "project-profile.yaml");
    }
}
