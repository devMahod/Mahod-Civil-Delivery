using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using MahodAI.Civil3D.Plugin.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate;
using MahodAI.CivilDelivery.Estimate.EngineerDraft;
using MahodAI.CivilDelivery.Shared;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

public partial class CivilDeliveryControl
{
    private void OnReviewMappings(object sender, RoutedEventArgs e)
    {
        RefreshGates();
        if (!BtnReviewMappings.IsEnabled || _scan == null || _profile == null || _catalog == null) return;
        try
        {
            var scan = _scan;
            var scope = CaptureProfileDecisionScope("בדיקת שיוכים מרוכזת", scan);
            var catalog = _catalog;
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, scope.Operation);
            var catalogId = catalog.SnapshotId;
            var catalogHash = catalog.FileHash;
            var itemFingerprints = catalog.Items.Values.ToDictionary(item => item.Code,
                CatalogIdentity.ItemFingerprint, StringComparer.OrdinalIgnoreCase);
            var hintStore = new SemanticHintStore();
            var groups = BuildMappingReviewGroups(scan, _quantityRows, _proposals, scope.Profile, hintStore);
            if (groups.Count == 0) return;
            var hintRevisions = groups.ToDictionary(group => group.RuleKey, group => group.SemanticHintRevision, StringComparer.Ordinal);
            var selectedRuleKey = (QuantitiesGrid.SelectedItem as QuantityRowViewModel)?.RuleKey;
            var initialRuleKey = groups.FirstOrDefault(group => string.Equals(group.RuleKey,
                selectedRuleKey, StringComparison.OrdinalIgnoreCase))?.RuleKey ?? selectedRuleKey;
            var assistant = SemanticMappingAssistantConfiguration.Create();
            var dialog = new ManualMappingReviewDialog(groups, catalog, SaveChoices, async (group, context, cancellation) =>
            {
                if (!SemanticMappingAssistantConfiguration.IsConfigured)
                    return new SemanticMappingAssistResult(Array.Empty<MappingProposal>(),
                        "העזרה החכמה אינה מוגדרת למשתמש זה. ניתן להמשיך בחיפוש ושיוך ידני, או לפנות למנהל המערכת להגדרת החיבור.", true);
                // L05: the project rules compute this group — the assistant never proposes a code for its raw sum.
                if (_projectRuleReviews.TryGetValue(group.RuleKey, out var ruleReview) && ruleReview.Governed)
                    return new SemanticMappingAssistResult(Array.Empty<MappingProposal>(), ruleReview.Message, true);
                // ...and the same 04647 gate over the whole case-insensitive closure, on the context re-proven now (Codex 23:59).
                // No context kept (proposals failed, or a catalog change cleared it): prepare one now — never an open default.
                var contextAtStart = _projectRuleContext;
                var rulesContext = contextAtStart ?? _estimate.PrepareProjectRuleContext(scan, catalog);
                if (EstimateWorkflowService.GenericAssistRefusal(scan, catalog, rulesContext, group.RuleKey) is { } ruleRefusal)
                    return new SemanticMappingAssistResult(Array.Empty<MappingProposal>(), ruleRefusal, true);
                // b19: a group the active library holds as a decision with no item is never proposed an item, re-read now.
                if (LibraryProposalGate.Refusal(EstimateWorkflowService.LibraryDispositions(scan, catalog, scope.Profile), group.RuleKey)
                        is { } libraryRefusal)
                    return new SemanticMappingAssistResult(Array.Empty<MappingProposal>(), libraryRefusal, true);
                // This callback runs from the existing review, outside a Civil
                // transaction. Network completion never substitutes a new context.
                RequireProfileDecisionScope(scope);
                if (!ReferenceEquals(_scan, scan) || !ReferenceEquals(_catalog, catalog))
                    throw new InvalidOperationException("הסריקה או המחירון הוחלפו.");
                EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "הצעת שיוך חכמה");
                var result = await assistant.AssistAsync(new MappingProposalEngine.DiscoveredGroup(
                    group.RuleKey, group.Layer, group.MeasurementKind, group.Unit, group.ObjectCount,
                    group.Quantity, group.CadMetadata, group.RecognitionEvidence), catalog, context, cancellation);
                cancellation.ThrowIfCancellationRequested();
                RequireProfileDecisionScope(scope);
                if (!ReferenceEquals(_scan, scan) || !ReferenceEquals(_catalog, catalog))
                    throw new InvalidOperationException("הסריקה או המחירון הוחלפו.");
                EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "קבלת הצעת שיוך חכמה");
                // The context may have changed while the assistant ran: re-prove it before its answer is shown.
                if (!ReferenceEquals(_projectRuleContext, contextAtStart) ||
                    EstimateWorkflowService.GenericAssistRefusal(scan, catalog, rulesContext, group.RuleKey) is { } ||
                    LibraryProposalGate.Refusal(EstimateWorkflowService.LibraryDispositions(scan, catalog, scope.Profile), group.RuleKey) is { })
                    return new SemanticMappingAssistResult(Array.Empty<MappingProposal>(),
                        "הקשר כללי הפרויקט השתנה בזמן שהעזרה החכמה רצה — ההצעה לא מוצגת. יש לבקש שוב.", true);
                return result;
            }, initialRuleKey: initialRuleKey, saveSemanticHint: SaveHint);
            CivilModalHost.ShowFromPalette(dialog);

            void SaveHint(ManualMappingReviewDialog.Group group, SemanticHintPolicy.Draft input, string recordedBy)
            {
                try
                {
                    RequireProfileDecisionScope(scope);
                    if (!ReferenceEquals(_scan, scan) || !ReferenceEquals(_catalog, catalog))
                        throw new InvalidOperationException("הסריקה או המחירון הוחלפו; התיאור לא נשמר.");
                    EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "שמירת משמעות הקבוצה");
                    var saved = _estimate.SaveSemanticHint(scope.Profile, scan, group.RuleKey, input,
                        recordedBy, hintStore, hintRevisions[group.RuleKey]);
                    hintRevisions[group.RuleKey] = saved.FileHash;
                    // Descriptions live in a small local sidecar: profile, scan evidence,
                    // staged catalog choices and their original decision scope stay intact.
                    SetStatus("משמעות הקבוצה נשמרה במחשב זה — לא אושר שיוך או מחיר");
                }
                finally { RefreshGates(); }
            }

            void SaveChoices(IReadOnlyList<ManualMappingReviewDialog.Choice> choices, string approvedBy)
            {
            var profileSaved = false;
            try
            {

            // Revalidate the ORIGINAL context. A new profile/catalog/scan is never
            // substituted to make choices from the old window acceptable.
            RequireProfileDecisionScope(scope);
            if (!ReferenceEquals(_scan, scan) || !ReferenceEquals(_catalog, catalog))
                throw new InvalidOperationException("הסריקה או המחירון הוחלפו בזמן הבדיקה — לא נשמר אף שיוך.");
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, scope.Operation);
            var verified = _estimate.LoadCatalog(scope.Profile, scan.ProfileSource);
            if (verified.Snapshot == null || verified.Findings.Any(finding => finding.Severity >= FindingSeverity.ReviewRequired) ||
                !string.Equals(verified.Snapshot.SnapshotId, catalogId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(verified.Snapshot.FileHash, catalogHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("מהדורת המחירון אינה זהה לזו שהוצגה — לא נשמר אף שיוך.");
            foreach (var choice in choices)
                if (!itemFingerprints.TryGetValue(choice.CatalogCode, out var before) ||
                    !verified.Snapshot.Items.TryGetValue(choice.CatalogCode, out var item) ||
                    !string.Equals(before, CatalogIdentity.ItemFingerprint(item), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("סעיף שנבחר השתנה מאז פתיחת הבדיקה — לא נשמר אף שיוך.");
            RequireProfileDecisionScope(scope);
            EstimateWorkflowService.RequireFreshForDecision(scope.Document, scan, "שמירת השיוכים שנבדקו");
            var reviewed = choices.Select(choice => new EstimateWorkflowService.ReviewedMappingChoice(
                choice.RuleKey, choice.CatalogCode, choice.ExcludedAlternativeRuleKey)).ToArray();
            var profileForSave = CloneProfileForDecision(scope.Profile);
            var saved = _estimate.SaveReviewedMappings(profileForSave, verified.Snapshot, scan, reviewed,
                approvedBy, RequireProfileWriteTarget(), scan.ProfileWriteState ??
                throw new InvalidOperationException("לסריקה אין ראיית CAS מקורית של הפרופיל."));
            profileSaved = true;
            ContinueEstimateReviewAfterDecision(saved, profileForSave,
                choices.Select(choice => choice.RuleKey).ToArray(), choices[0].RuleKey);
            Log($"נשמרו {choices.Count} שיוכים שנבדקו ידנית על ידי {approvedBy}; גרסת פרופיל {saved.NewVersion}.");
            SetStatus($"נשמרו {choices.Count} שיוכים — הכמויות נשמרו; מחירים וממצאים פתוחים עדיין דורשים השלמה");
            }
            catch
            {
                if (profileSaved)
                {
                    InvalidateEstimateEvidence("פרסום השיוכים לא הושלם — יש לבדוק את הפרופיל והראיות לפני המשך.");
                    ReloadProfile();
                }
                throw; // Display inside the review; do not discard the user's choices.
            }
            finally { RefreshGates(); }
            }
        }
        catch (Exception ex)
        {
            ReloadProfile();
            ShowError("בדיקת שיוכים מרוכזת", ex);
        }
        finally { RefreshGates(); }
    }

    private IReadOnlyList<ManualMappingReviewDialog.Group> BuildMappingReviewGroups(EstimateWorkflowService.ScanResult scan)
        => BuildMappingReviewGroups(scan, _quantityRows, _proposals, _profile!);

    internal static IReadOnlyList<ManualMappingReviewDialog.Group> BuildMappingReviewGroups(
        EstimateWorkflowService.ScanResult scan, IEnumerable<QuantityRowViewModel> quantityRows,
        IEnumerable<MappingProposal> mappingProposals, ProjectProfile profile, SemanticHintStore? hintStore = null)
    {
        hintStore ??= new SemanticHintStore();
        // One scan/group/proposal index, not a full scan for every displayed row.
        var scopes = ManualMappingCaseScope.Collect(scan.Records, profile)
            .ToDictionary(scope => scope.RuleKey, StringComparer.OrdinalIgnoreCase);
        var orderedRows = quantityRows.ToArray();
        var rows = orderedRows.ToLookup(row => row.RuleKey, StringComparer.OrdinalIgnoreCase);
        var proposals = mappingProposals.GroupBy(proposal => proposal.RuleKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MappingProposal>)group.ToArray(), StringComparer.Ordinal);
        var library = MahodAI.CivilDelivery.Estimate.EngineerDraft.EngineerBoqLibrary.For(profile);
        var familyEvidence = MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.ForProposalGroups(
            scan.Records, library, profile.Estimate.FamilyDecisions);
        var result = new List<ManualMappingReviewDialog.Group>();
        foreach (var firstRow in orderedRows.DistinctBy(row => row.RuleKey, StringComparer.OrdinalIgnoreCase))
        {
            if (!scopes.TryGetValue(firstRow.RuleKey, out var scope)) continue;
            var members = rows[scope.RuleKey].ToArray();
            var row = members.FirstOrDefault(candidate => candidate.RuleKey == scope.RuleKey) ?? members.FirstOrDefault();
            if (row == null) continue;
            var group = scope.Records;
            var kinds = group.Select(record => record.Measurement.Kind).Distinct(StringComparer.Ordinal).ToArray();
            var units = group.Select(record => Units.Parse(record.Measurement.Unit).Canonical).Distinct(StringComparer.Ordinal).ToArray();
            var reason = scope.Refusal ?? (members.Any(member => member.IsHistorical) ? "היסטוריה לקריאה בלבד — יש לסרוק מחדש." :
                members.Any(member => member.IsIgnored) ? "קבוצה בהיקף מוחרגת — יש להחזירה לרשימה במסך הכמויות לפני שיוך." :
                members.Any(member => !member.CanApproveCatalogMapping) ? "חלופת המדידה השנייה כבר נבחרה; אין לשייך את שתיהן." :
                kinds.Length != 1 || units.Length != 1 ? "בקבוצה קיימות שיטות או יחידות שונות; נדרשת בדיקת הקבוצה." : null);
            var sources = string.Join("; ", group.Select(record => record.Source.DrawingPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => string.IsNullOrWhiteSpace(path) ? "מקור לא מזוהה" : path));
            var scopeText = scope.HasCaseVariants ? "היקף שיוך מלא — המנוע אינו מבחין באותיות גדולות/קטנות: " +
                string.Join("; ", scope.RuleKeys) + $"\n{group.Count:N0} עצמים בכל ההיקף. שכבות: " +
                string.Join("; ", group.Select(record => record.Source.Layer).Distinct(StringComparer.Ordinal)) + "\n" : "";
            if (scope.HasCaseVariants && reason != null)
                scopeText += "אין סכום משותף מאושר; מוצגת כמות השורה המקורית בלבד לצורך זיהוי, ללא אישור.\n";
            if (scope.TotalQuantity == null) scopeText += "סכום ההיקף אינו מספר סופי; אין ערך אפס חלופי.\n";
            var hint = ReadSemanticHint(scope.RuleKey, group);
            // An exact unmapped group may reuse the full-scan family resolution. A wider
            // case-insensitive or partly mapped review scope must not inherit a subset approval.
            var recognition = !scope.HasCaseVariants && group.All(record => string.IsNullOrWhiteSpace(record.Classification.CandidateCatalogCode)) &&
                familyEvidence.TryGetValue(scope.RuleKey, out var resolved) ? resolved :
                MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.For(group, library);
            var familyNotice = recognition.FamilyWithheld;
            result.Add(new ManualMappingReviewDialog.Group(scope.RuleKey,
                scope.HasCaseVariants ? string.Join(" / ", group.Select(record => SectionProjectionLogic.LayerLeaf(record.Source.Layer)).Distinct(StringComparer.Ordinal)) : row.Layer,
                scope.HasCaseVariants ? string.Join(" / ", group.Select(record => record.Source.EntityType).Distinct(StringComparer.OrdinalIgnoreCase)) : row.EntityType,
                kinds.Length == 1 ? kinds[0] : "mixed", row.Unit,
                scope.HasCaseVariants && reason == null ? scope.TotalQuantity!.Value : row.Quantity,
                scope.HasCaseVariants ? group.Count : row.ObjectCount, row.CatalogCode,
                row.AlternativeRuleKey, row.AlternativeQuantityDisplay,
                scopeText + string.Join("\n", members.Select(member => $"{member.StatusDetail}\n{member.Findings}")) + $"\nמקורות: {sources}" +
                    (string.IsNullOrWhiteSpace(familyNotice) ? string.Empty : "\n" + familyNotice),
                proposals.GetValueOrDefault(scope.RuleKey) ?? Array.Empty<MappingProposal>(), reason,
                QuantityCadMetadataPolicy.Summarize(group.Select(record => record.Measurement)),
                hint.Input, hint.Revision, hint.Available,
                MahodAI.CivilDelivery.Estimate.Recognition.CatalogEvidenceBridge.Snapshot(recognition)));
        }
        return result;

        (SemanticHintPolicy.Draft? Input, string? Revision, bool Available) ReadSemanticHint(string key, IReadOnlyList<NeutralQuantityRecord> records)
        {
            try
            {
                // A missing tiny sidecar needs no record serialization. Only a saved
                // candidate requires comparing the exact source group fingerprint.
                var snapshot = hintStore.Read(profile.ProfileId, scan.SourceDrawing, key);
                var current = snapshot.Hint == null ? null : SemanticHintPolicy.Capture(profile.ProfileId,
                    scan.SourceDrawing, scan.SourceDrawingHash ?? "", key, records);
                return (snapshot.Hint?.Source == current ? snapshot.Hint?.Input : null, snapshot.FileHash, true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { return (null, null, false); } // No saved input is reused or overwritten without a readable identity.
        }
    }
}
