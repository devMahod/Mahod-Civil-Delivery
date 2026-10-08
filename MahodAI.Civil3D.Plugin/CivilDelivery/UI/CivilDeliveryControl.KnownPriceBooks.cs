using System;
using System.IO;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private static string KnownPriceBookDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MahodAI_Civil3D", "civil-delivery", "price-books");

    private void RememberPriceBook(PriceBookRegistry.RegisterResult registered)
    {
        // This is a post-commit convenience, never part of registration's failure/rollback boundary.
        try
        {
            var warning = KnownPriceBookReuse.Remember(KnownPriceBookDirectory, registered);
            if (warning != null) { Log(warning); SetStatus(warning); }
        }
        catch (Exception ex)
        {
            Log("מחירון נרשם בפרויקט; כשל בלתי צפוי באינדקס המחירונים המוכרים: " + ex);
            SetStatus("המחירון פעיל בפרויקט. הוספתו לרשימת המחירונים המוכרים לא הושלמה.");
        }
    }

    private void PublishPriceBookProfile(ProjectProfileLoader.LoadResult durable, string target)
    {
        (ProjectProfile? Profile, string? Hash, string? Target) Reload()
        {
            ReloadProfile();
            _refreshingPriceBooks = true; // ReloadProfile's nested refresh restores its own guard.
            return (_profile, _profileHash, _profileWriteTarget);
        }
        _refreshingPriceBooks = true;
        try { PublishPriceBookProfile(durable, target, Reload, PriceBookCombo); }
        finally { _refreshingPriceBooks = false; }
    }

    // Native-free publication boundary used by the live handler: real reload first, identity proof, then real combo.
    internal static void PublishPriceBookProfile(ProjectProfileLoader.LoadResult durable, string target,
        Func<(ProjectProfile? Profile, string? Hash, string? Target)> reload, System.Windows.Controls.ComboBox combo)
    {
        var actual = reload();
        KnownPriceBookReuse.RequirePublished(actual.Profile, actual.Hash, actual.Target,
            durable.Profile!, durable.ProfileHash!, target);
        RefreshPriceBookCombo(combo, actual.Profile);
        if (combo.SelectedItem is not PriceBookChoice selected ||
            selected.Id != PriceBookRegistry.Active(durable.Profile!)?.Id)
            throw new InvalidOperationException("המחירון נשמר אך אינו מוצג כפעיל ברשימה; טען מחדש את הפרופיל לפני המשך עבודה.");
    }

    private void OnKnownPriceBooks(object sender, RoutedEventArgs e)
    {
        if (_profile == null) return;
        ProfileDecisionScope scope;
        try { scope = CaptureProfileDecisionScope("רישום מחירון מוכר"); }
        catch (Exception ex) { ShowError("מחירון מוכר", KnownPriceBookReuse.DescribePreWriteFailure(ex)); return; }
        var writeAttempted = false;
        PriceBookRegistry.RegisterResult? published = null;
        try
        {
            var remembered = KnownPriceBookIndex.Offers(KnownPriceBookDirectory, scope.Profile);
            var bundled = BundledPriceBooks.Offers(KnownPriceBookDirectory, scope.Profile);
            var offers = bundled.Concat(remembered.Where(offer => !bundled.Any(book =>
                string.Equals(book.Entry.Sha256, offer.Entry.Sha256, StringComparison.OrdinalIgnoreCase) &&
                PriceBookRegistry.SameMapping(book.Entry.Mapping, offer.Entry.Mapping)))).ToArray();
            var review = new KnownPriceBookDialog(offers, scope.Profile.ProjectName);
            if (CivilModalHost.ShowFromPalette(review) != true || review.Accepted is not { } chosen)
            { SetStatus("לא נרשם מחירון מוכר — הפרויקט לא השתנה"); return; }
            var approver = RequireApprover("רישום מחירון מוכר בפרויקט");
            if (approver == null) return;
            RequireProfileDecisionScope(scope);
            var profileForSave = CloneProfileForDecision(scope.Profile);
            writeAttempted = true;
            var registered = _estimate.RegisterPriceBook(profileForSave, chosen.Entry.Path, approver,
                targetProfilePath: scope.ExpectedState.TargetPath, expectedProfileState: scope.ExpectedState,
                publisher: chosen.Entry.Publisher, edition: chosen.Entry.Edition, makeActive: true,
                expectedInspectionHash: chosen.Entry.Sha256, mapping: KnownPriceBookReuse.Mapping(chosen));
            var durable = ProjectProfileLoader.LoadFromFile(scope.ExpectedState.TargetPath);
            if (!durable.IsUsable || durable.Profile == null || !CatalogIdentity.IsValidSha256(durable.ProfileHash) ||
                PriceBookRegistry.Active(durable.Profile)?.Id != registered.Entry.Id ||
                !string.Equals(PriceBookRegistry.Active(durable.Profile)?.FileHash, registered.Entry.FileHash, StringComparison.OrdinalIgnoreCase) ||
                !PriceBookRegistry.SameMapping(PriceBookRegistry.Active(durable.Profile)?.Mapping, registered.Entry.Mapping))
                throw new InvalidOperationException("המחירון נרשם אך הפרופיל לא נקרא בחזרה באופן מאומת.");
            InvalidateEstimateEvidence("מחירון מוכר נרשם בפרויקט — יש לסרוק מחדש");
            PublishPriceBookProfile(durable, scope.ExpectedState.TargetPath);
            Log($"מחירון מוכר נרשם והופעל: {registered.Entry.Id}; אושר ע\"י {approver}; פרופיל {scope.ExpectedState.TargetPath}; SHA {durable.ProfileHash}");
            SetStatus("המחירון המוכר נרשם והופעל — יש להריץ סריקה חדשה");
            published = registered;
        }
        catch (Exception ex)
        {
            if (writeAttempted)
            {
                InvalidateEstimateEvidence("רישום מחירון מוכר לא הושלם באופן מאומת");
                ReloadProfile();
            }
            RtlMessageBox.Show(ex.Message, "מחירון מוכר לא הופעל", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { RefreshGates(); }
        if (published != null) RememberPriceBook(published);
    }
}
