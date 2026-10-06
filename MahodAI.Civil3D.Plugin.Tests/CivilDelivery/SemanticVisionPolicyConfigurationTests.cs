using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.Recognition;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>
/// The vision switch is a non-secret, machine-wide policy file read from an injected path. These tests never touch the
/// real credential file, the DPAPI store, the GEMINI_API_KEY variable or the machine's real policy file.
/// </summary>
public sealed class SemanticVisionPolicyConfigurationTests
{
    private const string ValidPolicy =
        "{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":true,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}";

    [Fact]
    public void PolicyPath_IsMachineWideAndOutsideEveryUserProfile()
    {
        var path = SemanticMappingAssistantConfiguration.VisionPolicyPath;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        path.Should().Be(Path.Combine(programData, "MahodAI_Civil3D", "civil-delivery", "semantic-vision.policy.json"));
        path.Should().NotStartWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .And.NotStartWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
            .And.NotStartWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        path.Should().NotContain(Path.DirectorySeparatorChar + "secrets" + Path.DirectorySeparatorChar);
        Path.GetDirectoryName(SemanticMappingAssistantConfiguration.CredentialPath).Should().NotBe(Path.GetDirectoryName(path));
    }

    [Fact]
    public void AbsentPolicyFile_KeepsVisionDisabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-vision-policy-" + Guid.NewGuid().ToString("N"));
        SemanticMappingAssistantConfiguration.ReadVisionPolicy(Path.Combine(directory, "semantic-vision.policy.json")).Should().BeFalse();
        SemanticMappingAssistantConfiguration.ReadVisionPolicy(Path.Combine(directory, "semantic-vision.policy.json"), _ => true).Should().BeFalse();
    }

    [Theory]
    [InlineData(ValidPolicy, true)]
    [InlineData("{\"schema\":\"mahod-semantic-vision/1\",\"enabled\":false,\"granted_by\":\"Arthur\",\"granted_at_utc\":\"2026-09-27T08:00:00Z\"}", false)]
    [InlineData("{\"enabled\":true}", false)]
    [InlineData("", false)]
    public void PolicyFile_IsReadThroughTheStrictCoreParser(string content, bool expected)
    {
        // The protection check is injected as satisfied here, so only the parser decides.
        WithPolicyFile(content, path => SemanticMappingAssistantConfiguration.ReadVisionPolicy(path, _ => true).Should().Be(expected));
    }

    [Fact]
    public void OversizedPolicyFile_IsIgnored()
    {
        WithPolicyFile(ValidPolicy + new string(' ', VisionOrganizationPolicy.MaxFileBytes),
            path => SemanticMappingAssistantConfiguration.ReadVisionPolicy(path, _ => true).Should().BeFalse());
    }

    [Fact]
    public void ValidPolicyThatAStandardUserCanWrite_IsIgnored()
    {
        WithPolicyFile(ValidPolicy, path =>
        {
            // Deterministic whoever runs the tests: BUILTIN\Users may modify this copy.
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify, AccessControlType.Allow));
            file.SetAccessControl(security);

            SemanticMappingAssistantConfiguration.ReadVisionPolicy(path, _ => true).Should().BeTrue("the content itself is valid");
            SemanticMappingAssistantConfiguration.ReadVisionPolicy(path).Should().BeFalse();
        });
    }

    [Fact]
    public void ValidPolicyCreatedByTheCurrentUserInTheirProfile_IsIgnored()
    {
        using var identity = WindowsIdentity.GetCurrent();
        // A SYSTEM-run build agent owns what it creates in a system temp folder; the attack is a standard user's file.
        if (identity.IsSystem) return;
        WithPolicyFile(ValidPolicy, path => SemanticMappingAssistantConfiguration.ReadVisionPolicy(path).Should().BeFalse());
    }

    [Theory]
    [InlineData("administrators-owner", true)]
    [InlineData("system-owner", true)]
    [InlineData("users-read-only", true)]
    [InlineData("deny-write-to-everyone", true)]
    [InlineData("engineer-owner", false)]
    [InlineData("users-owner", false)]
    [InlineData("users-write", false)]
    [InlineData("users-append", false)]
    [InlineData("authenticated-modify", false)]
    [InlineData("everyone-full", false)]
    [InlineData("engineer-delete", false)]
    [InlineData("engineer-change-permissions", false)]
    [InlineData("engineer-take-ownership", false)]
    [InlineData("no-owner", false)]
    public void OrganisationPolicy_CountsOnlyWhenOwnedAndWritableByAdministratorsAlone(string variant, bool expected)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var authenticated = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var engineer = new SecurityIdentifier("S-1-5-21-1000000001-2000000002-3000000003-1001");

        var security = new FileSecurity();
        switch (variant)
        {
            case "system-owner": security.SetOwner(system); break;
            case "engineer-owner": security.SetOwner(engineer); break;
            case "users-owner": security.SetOwner(users); break;
            case "no-owner": break;
            default: security.SetOwner(administrators); break;
        }
        security.AddAccessRule(new FileSystemAccessRule(administrators, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        switch (variant)
        {
            case "deny-write-to-everyone":
                security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Write, AccessControlType.Deny));
                break;
            case "users-write":
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Write, AccessControlType.Allow));
                break;
            case "users-append":
                security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.AppendData, AccessControlType.Allow));
                break;
            case "authenticated-modify":
                security.AddAccessRule(new FileSystemAccessRule(authenticated, FileSystemRights.Modify, AccessControlType.Allow));
                break;
            case "everyone-full":
                security.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.FullControl, AccessControlType.Allow));
                break;
            case "engineer-delete":
                security.AddAccessRule(new FileSystemAccessRule(engineer, FileSystemRights.Delete, AccessControlType.Allow));
                break;
            case "engineer-change-permissions":
                security.AddAccessRule(new FileSystemAccessRule(engineer, FileSystemRights.ChangePermissions, AccessControlType.Allow));
                break;
            case "engineer-take-ownership":
                security.AddAccessRule(new FileSystemAccessRule(engineer, FileSystemRights.TakeOwnership, AccessControlType.Allow));
                break;
        }

        SemanticMappingAssistantConfiguration.IsAdministratorControlled(security).Should().Be(expected);
    }

    [Fact]
    public void FamilyProvider_IsTheBoundedGeminiTransport()
    {
        // Construction only: no request is sent, so neither the key delegate nor the policy file is read here.
        SemanticMappingAssistantConfiguration.CreateFamilyProvider().Should().BeOfType<GeminiMappingAssistant>();
    }

    private static void WithPolicyFile(string content, Action<string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mahod-vision-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "semantic-vision.policy.json");
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
            check(path);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
