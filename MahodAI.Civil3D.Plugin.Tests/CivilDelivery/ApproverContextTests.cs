using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;
using Button = System.Windows.Controls.Button;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

/// <summary>b24 (Codex 11:18): one current approver per session, set only from a typed and confirmed name.</summary>
public sealed class ApproverContextTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  \r\n\t ", null)]
    [InlineData("  נטלי  כהן ", "נטלי כהן")]
    [InlineData("first\r\nsecond", "first second")]
    public void ANameIsOneLineWithSingleSpaces_ABlankNameIsNone(string? typed, string? expected) =>
        Assert.Equal(expected, ApproverContext.Normalize(typed));

    [Fact]
    public void ANewSessionHasNoApprover_OnlyATypedNameSetsIt()
    {
        var context = new ApproverContext();
        var changes = 0; context.Changed += () => changes++;
        Assert.Null(context.Name);
        Assert.False(context.Confirm("   "));
        Assert.Null(context.Name);
        Assert.True(context.Confirm(" SYNTHETIC REVIEWER "));
        Assert.Equal("SYNTHETIC REVIEWER", context.Name);
        Assert.True(context.Confirm("SYNTHETIC REVIEWER"));
        Assert.Equal(1, changes);
        Assert.True(context.Confirm("ANOTHER SYNTHETIC REVIEWER"));
        Assert.Equal("ANOTHER SYNTHETIC REVIEWER", context.Name);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void RequireAsksOnlyWhenNobodyIsSet_ACancelledAskReturnsNothingAndChangesNothing()
    {
        var context = new ApproverContext();
        var asked = 0;
        Assert.Null(context.Require("SYNTHETIC ACTION", (_, _) => { asked++; return null; }));
        Assert.Null(context.Require("SYNTHETIC ACTION", (_, _) => { asked++; return "  "; }));
        Assert.Null(context.Name);
        Assert.Equal("SYNTHETIC REVIEWER", context.Require("SYNTHETIC ACTION", (action, current) =>
        {
            asked++; Assert.Equal("SYNTHETIC ACTION", action); Assert.Null(current);
            return " SYNTHETIC REVIEWER ";
        }));
        Assert.Equal(3, asked);
        Assert.Equal("SYNTHETIC REVIEWER", context.Require("NEXT ACTION", (_, _) => throw new InvalidOperationException("must not ask again")));
    }

    [Fact]
    public void ThePromptStartsFromTheCurrentApproverOrEmpty_AndOnlyConfirmReturnsAName() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var empty = new ApproverPromptDialog("SYNTHETIC ACTION", null);
        try
        {
            Assert.Equal("", empty.NameInput.Text);
            Assert.False(empty.Confirm.IsEnabled);
            empty.NameInput.Text = "   ";
            Assert.False(empty.Confirm.IsEnabled);
            empty.NameInput.Text = "  SYNTHETIC  REVIEWER ";
            Assert.True(empty.Confirm.IsEnabled);
            Click(empty.Confirm);
            Assert.Equal("SYNTHETIC REVIEWER", empty.ConfirmedName);
        }
        finally { empty.Close(); }
        var current = new ApproverPromptDialog(null, "SYNTHETIC REVIEWER");
        try
        {
            Assert.Equal("SYNTHETIC REVIEWER", current.NameInput.Text);
            current.NameInput.Text = "SOMEONE ELSE";
            Click(current.Cancel);
            Assert.Null(current.ConfirmedName);
        }
        finally { current.Close(); }
    });

    [Fact]
    public void TheDecisionDialogsStartFromTheSessionApproverNeverWindows() => ManualMappingBatchDialogTests.RunSta(() =>
    {
        var start = new EstimateProjectStartDialog("synthetic", "Synthetic", @"C:\synthetic-only\Host.dwg",
            @"C:\synthetic-only\profile.yaml", "Host only", "SYNTHETIC REVIEWER");
        try { Assert.Equal("SYNTHETIC REVIEWER", start.ApproverInput.Text); }
        finally { start.Close(); }
        var units = new DrawingUnitsReviewDialog(new DrawingUnitsReviewContext(@"C:\synthetic-only\Host.dwg",
            "10101010-2020-3030-4040-505050505050", 0, new string('a', 64), "synthetic", CurrentApprover: "SYNTHETIC REVIEWER"));
        try { Assert.Equal("SYNTHETIC REVIEWER", units.Approver.Text); }
        finally { units.Close(); }
    });

    [Fact]
    public void NoDecisionSiteTakesTheWindowsAccount()
    {
        var root = Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery");
        // The one technical actor left: who prepared an export file — not an approval (Codex 11:18).
        var allowed = new[] { "EstimateWorkflowService.cs: PreparedBy: Environment.UserName);" };
        var found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            .SelectMany(file => File.ReadAllLines(file)
                .Where(line => line.Contains("Environment.UserName", StringComparison.Ordinal))
                .Select(line => Path.GetFileName(file) + ": " + line.Trim()))
            .ToList();
        Assert.Equal(allowed, found);
    }

    [Fact]
    public void TheMappingAndItsExcludedAlternativeAreSavedUnderTheSameApprover()
    {
        var control = File.ReadAllText(Path.Combine(TestPaths.PluginSourceDir, "CivilDelivery", "UI", "CivilDeliveryControl.xaml.cs"));
        var mapping = control[control.IndexOf("var approver = RequireApprover(\"אישור שיוך לסעיף\");", StringComparison.Ordinal)..];
        mapping = mapping[..mapping.IndexOf("Log($\"מיפוי אושר", StringComparison.Ordinal)];
        Assert.Contains("row.AlternativeRuleKey!, approver,", mapping);
        Assert.Contains("new[] { approval }, approver,", mapping);
        Assert.Equal(2, Regex.Matches(mapping, @"\bapprover,").Count);
    }
}
