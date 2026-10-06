using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// Stage 2 UI safety tests: skipped-reason itemization, engineer-input
    /// placeholder fields, and undo-count gating. Pure HTML rendering — no
    /// AutoCAD runtime required.
    /// </summary>
    public class FixPlanHtmlRendererTests
    {
        private static FixPlanPayload MakePlan(
            List<FixPlanItem>? items = null,
            List<string>? skippedReasons = null)
        {
            return new FixPlanPayload
            {
                PlanId = "plan-1",
                Summary = "סיכום",
                Items = items ?? new List<FixPlanItem>(),
                SkippedReasons = skippedReasons,
            };
        }

        #region Skipped reasons

        [Fact]
        public void RenderPlan_WithSkippedReasons_RendersCollapsibleHebrewList()
        {
            var html = FixPlanHtmlRenderer.RenderPlan(MakePlan(skippedReasons: new List<string>
            {
                "תוואי 73 / tangent_length: לא ניתן לתיקון אוטומטי",
                "פרופיל P1 / radius_m: אין כלי תיקון מתאים",
            }));

            html.Should().Contain("fix-skipped-reasons");
            html.Should().Contain("<details");
            html.Should().Contain("<summary");
            html.Should().Contain("ממצאים שלא נכללו בתוכנית התיקונים (2)");
            html.Should().Contain("תוואי 73 / tangent_length: לא ניתן לתיקון אוטומטי");
            html.Should().Contain("פרופיל P1 / radius_m: אין כלי תיקון מתאים");
        }

        [Fact]
        public void RenderPlan_WithoutSkippedReasons_OmitsSection()
        {
            FixPlanHtmlRenderer.RenderPlan(MakePlan(skippedReasons: null))
                .Should().NotContain("fix-skipped-reasons");
            FixPlanHtmlRenderer.RenderPlan(MakePlan(skippedReasons: new List<string>()))
                .Should().NotContain("fix-skipped-reasons");
        }

        [Fact]
        public void RenderSkippedReasons_HtmlEncodesContent()
        {
            var html = FixPlanHtmlRenderer.RenderSkippedReasons(
                new List<string> { "<script>alert(1)</script>" });

            html.Should().NotContain("<script>alert(1)</script>");
            html.Should().Contain("&lt;script&gt;");
        }

        #endregion

        #region Placeholder (engineer-input) fields

        private static FixPlanItem MakeItemWithEditableFields(Dictionary<string, FieldMeta> editable)
        {
            return new FixPlanItem
            {
                Id = "item-1",
                ObjectName = "תוואי 73",
                ObjectType = "alignment",
                Description = "רדיוס מתחת למינימום",
                ToolName = "modify_alignment_curve_radius",
                ToolParams = JsonDocument.Parse(
                    """{"alignment_name":"73","element_index":0,"new_radius":900}""").RootElement.Clone(),
                EditableFields = editable,
            };
        }

        [Fact]
        public void RenderPlan_PlaceholderIndexField_IsRenderedEditable()
        {
            // The agent adds element_index to editable_fields ONLY when the
            // engineer must fill it (INDEX_MISSING sentinel). It used to be
            // hidden, silently sending placeholder 0 on approve.
            var item = MakeItemWithEditableFields(new Dictionary<string, FieldMeta>
            {
                ["new_radius"] = new FieldMeta { Type = "number", Unit = "m" },
                ["element_index"] = new FieldMeta { Type = "number", Min = 0, Max = 9999 },
            });

            var html = FixPlanHtmlRenderer.RenderPlan(MakePlan(items: new List<FixPlanItem> { item }));

            html.Should().Contain("data-param-name='element_index'",
                "the placeholder index must be editable so the edit reaches edited_items");
            html.Should().Contain("data-param-name='new_radius'");
            html.Should().Contain("אינדקס אלמנט");
            html.Should().Contain("דרוש קלט מהנדס");
        }

        [Fact]
        public void RenderPlan_PviPlaceholderField_IsRenderedEditable()
        {
            var item = MakeItemWithEditableFields(new Dictionary<string, FieldMeta>
            {
                ["pvi_index"] = new FieldMeta { Type = "number", Min = 0 },
            });

            var html = FixPlanHtmlRenderer.RenderPlan(MakePlan(items: new List<FixPlanItem> { item }));

            html.Should().Contain("data-param-name='pvi_index'");
            html.Should().Contain("אינדקס PVI");
        }

        [Fact]
        public void RenderPlan_NoPlaceholderFields_RendersOnlyPrimary()
        {
            var item = MakeItemWithEditableFields(new Dictionary<string, FieldMeta>
            {
                ["new_radius"] = new FieldMeta { Type = "number", Unit = "m" },
            });

            var html = FixPlanHtmlRenderer.RenderPlan(MakePlan(items: new List<FixPlanItem> { item }));

            html.Should().Contain("data-param-name='new_radius'");
            html.Should().NotContain("data-param-name='element_index'");
            html.Should().NotContain("דרוש קלט מהנדס");
        }

        [Fact]
        public void IsPlaceholderIndexField_RecognizesIndexFieldsOnly()
        {
            FixPlanHtmlRenderer.IsPlaceholderIndexField("element_index").Should().BeTrue();
            FixPlanHtmlRenderer.IsPlaceholderIndexField("pvi_index").Should().BeTrue();
            FixPlanHtmlRenderer.IsPlaceholderIndexField("new_radius").Should().BeFalse();
            FixPlanHtmlRenderer.IsPlaceholderIndexField("length").Should().BeFalse();
        }

        #endregion

        #region Undo button gating on applied_count

        private static FixResultPayload MakeResult(int? appliedCount, List<FixResultItem>? items = null)
        {
            return new FixResultPayload
            {
                PlanId = "plan-1",
                Success = true,
                Summary = "בוצע",
                Results = items ?? new List<FixResultItem>(),
                AppliedCount = appliedCount,
            };
        }

        [Fact]
        public void RenderCombinedResult_AppliedCountMissing_UndoDisabledWithTooltip()
        {
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(appliedCount: null), null);

            html.Should().Contain("disabled");
            html.Should().Contain("לא דווח על ידי השרת");
            html.Should().NotContain("undo_all", "a disabled undo button must not be clickable");
        }

        [Fact]
        public void RenderCombinedResult_AppliedCountZero_UndoDisabled()
        {
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(appliedCount: 0), null);

            html.Should().Contain("disabled");
            html.Should().Contain("אין מה לבטל");
            html.Should().NotContain("undo_all");
        }

        [Fact]
        public void RenderCombinedResult_AppliedCountPositive_UndoEnabledWithCount()
        {
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(appliedCount: 3), null);

            html.Should().Contain("undo_all");
            html.Should().Contain("plan-1");
            html.Should().Contain("(Undo) — 3");
            html.Should().NotContain("cursor:not-allowed");
        }

        #endregion

        #region Per-item status rendering

        [Fact]
        public void RenderCombinedResult_StatusSkipped_RendersAsSoftSkip_EvenWithSuccessTrue()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "i1", Success = true, Status = "skipped", Description = "קשת בקצה התוואי" },
            };
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(0, items), null);

            html.Should().Contain("נדלג");
            html.Should().NotContain(">הצליח<");
        }

        [Fact]
        public void RenderCombinedResult_StatusApplied_RendersAsSuccess()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "i1", Success = true, Status = "applied", Description = "עודכן רדיוס" },
            };
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(1, items), null);

            html.Should().Contain("הצליח");
            html.Should().NotContain("נדלג");
        }

        [Fact]
        public void RenderCombinedResult_LegacyDescriptionMarker_StillDetectedAsSoftSkip()
        {
            // Older agents have no status field — the Hebrew description marker
            // fallback must keep working.
            var items = new List<FixResultItem>
            {
                new() { ItemId = "i1", Success = true, Status = null, Description = "אלמנט זה אינו ניתן לתיקון אוטומטי" },
            };
            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(0, items), null);

            html.Should().Contain("נדלג");
        }

        #endregion

        #region Execution summary banner (single source of truth)

        [Fact]
        public void RenderExecutionSummary_ReportsBucketedCounts()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "a", Success = true, Status = "applied" },
                new() { ItemId = "b", Success = true, Status = "applied" },
                new() { ItemId = "c", Success = true, Status = "applied" },
                new() { ItemId = "d", Success = true, Status = "applied" },   // 4 applied
                new() { ItemId = "e", Success = false, Status = "failed" },   // 1 failed
                new() { ItemId = "f", Success = true, Status = "manual_required" },
                new() { ItemId = "g", Success = true, Status = "manual_required" },
                new() { ItemId = "h", Success = true, Status = "skipped" },
                new() { ItemId = "i", Success = true, Status = "skipped" },
                new() { ItemId = "j", Success = true, Status = "skipped" },
                new() { ItemId = "k", Success = true, Status = "skipped" },
                new() { ItemId = "l", Success = true, Status = "skipped" },
                new() { ItemId = "m", Success = true, Status = "skipped" },
                new() { ItemId = "n", Success = true, Status = "skipped" },   // 9 manual
            };

            var html = FixPlanHtmlRenderer.RenderExecutionSummary(items);

            html.Should().Contain("מתוך 14 תיקונים");
            html.Should().Contain("4 בוצעו");
            html.Should().Contain("9 דורשים טיפול ידני");
            html.Should().Contain("1 נכשלו");
        }

        [Fact]
        public void RenderCombinedResult_WithItems_RendersBanner_NotRawServerSummary()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "a", Success = true, Status = "applied" },
                new() { ItemId = "b", Success = true, Status = "manual_required" },
            };
            var result = MakeResult(1, items);
            result.Summary = "טקסט שרת שעלול לסתור";   // must NOT be echoed when a table exists

            var html = FixPlanHtmlRenderer.RenderCombinedResult(result, null);

            html.Should().Contain("סיכום ביצוע");
            html.Should().Contain("מתוך 2 תיקונים");
            html.Should().NotContain("טקסט שרת שעלול לסתור");
        }

        [Fact]
        public void RenderCombinedResult_DoesNotRenderLlmVerifySummaryText()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "a", Success = true, Status = "applied" },
            };
            var verify = new FixVerifyResultPayload
            {
                PlanId = "plan-1",
                SummaryText = "טקסט אימות מה-LLM עם מספרים סותרים",
            };

            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(1, items), verify);

            html.Should().NotContain("טקסט אימות מה-LLM עם מספרים סותרים");
        }

        [Fact]
        public void RenderCombinedResult_NoItems_FallsBackToServerSummary()
        {
            var result = MakeResult(0, items: null);
            result.Summary = "לא נבחרו פריטים לתיקון.";

            var html = FixPlanHtmlRenderer.RenderCombinedResult(result, null);

            html.Should().Contain("לא נבחרו פריטים לתיקון.");
            html.Should().NotContain("סיכום ביצוע");
        }

        [Fact]
        public void RenderExecutionSummary_SplitsAppliedButVerifyFailedFromCleanSuccesses()
        {
            // 2 applied; verification fails one of them. The banner must show 1 clean success
            // and a separate "בוצע אך האימות נכשל" count — never 2 בוצעו (which would exceed the
            // ✅ green rows the engineer counts in the table).
            var items = new List<FixResultItem>
            {
                new() { ItemId = "ok", Success = true, Status = "applied" },
                new() { ItemId = "bad", Success = true, Status = "applied" },
            };
            var verifyLookup = new Dictionary<string, FixVerifyItem>
            {
                ["bad"] = new FixVerifyItem { ItemId = "bad", Verified = false },
            };

            var html = FixPlanHtmlRenderer.RenderExecutionSummary(items, verifyLookup);

            html.Should().Contain("1 בוצעו");
            html.Should().Contain("1 בוצע אך האימות נכשל");
            html.Should().NotContain("2 בוצעו");
        }

        [Fact]
        public void RenderCombinedResult_AppliedButVerifyFailed_RowReadsHonestly()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "i1", Success = true, Status = "applied", Description = "רדיוס" },
            };
            var verify = new FixVerifyResultPayload
            {
                PlanId = "plan-1",
                Items = new List<FixVerifyItem> { new() { ItemId = "i1", Verified = false } },
            };

            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(1, items), verify);

            // Execution says applied, but verification contradicts — the row must not read a
            // clean green "הצליח" next to the red verify cell.
            html.Should().Contain("בוצע — האימות נכשל");
        }

        [Fact]
        public void RenderCombinedResult_PostFixValidation_IsNotRendered()
        {
            var items = new List<FixResultItem>
            {
                new() { ItemId = "i1", Success = true, Status = "applied" },
            };
            var postFix = new PostFixValidationPayload
            {
                PlanId = "plan-1",
                Totals = new PostFixValidationTotals { Resolved = 10, StillOpen = 4, New = 0 },
                Entities = new List<PostFixValidationEntity>
                {
                    new() { EntityName = "73", EntityType = "alignment", Status = "validated", ResolvedCount = 6 },
                },
            };

            var html = FixPlanHtmlRenderer.RenderCombinedResult(MakeResult(1, items), null, postFix);

            html.Should().NotContain("אימות לאחר תיקון");
            html.Should().Contain("סיכום ביצוע");   // the single execution summary remains
        }

        #endregion

        #region Empty engineer-input value field

        [Fact]
        public void RenderPlan_EmptyPrimaryValueField_GetsEngineerInputAffordance()
        {
            // A max-grade fix (modify_profile_grade) leaves new_elevation blank for the engineer.
            // The plugin must render a clear "enter value" affordance, not a bare empty box.
            var item = new FixPlanItem
            {
                Id = "g1",
                ObjectName = "פרופיל 73",
                ObjectType = "profile",
                Description = "שיפוע 7% גדול מהמותר",
                ToolName = "modify_profile_grade",
                ToolParams = JsonDocument.Parse("""{"alignment_name":"73"}""").RootElement.Clone(),
                EditableFields = new Dictionary<string, FieldMeta>
                {
                    ["new_elevation"] = new FieldMeta { Type = "number", Unit = "m" },
                },
            };

            var html = FixPlanHtmlRenderer.RenderPlan(MakePlan(items: new List<FixPlanItem> { item }));

            html.Should().Contain("data-param-name='new_elevation'");
            html.Should().Contain("הזן ערך");
        }

        #endregion

        #region Payload serialization (additive fields)

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        [Fact]
        public void FixResultPayload_DeserializesAppliedCountAndStatus()
        {
            const string json = """
                {"plan_id":"p1","success":true,"summary":"",
                 "results":[{"item_id":"i1","success":true,"status":"applied","description":"d"}],
                 "applied_count":2}
                """;

            var payload = JsonSerializer.Deserialize<FixResultPayload>(json, JsonOpts)!;

            payload.AppliedCount.Should().Be(2);
            payload.Results.Should().ContainSingle()
                .Which.Status.Should().Be("applied");
        }

        [Fact]
        public void FixResultPayload_MissingAppliedCount_IsNull_NotDefaulted()
        {
            const string json = """
                {"plan_id":"p1","success":true,"summary":"","results":[]}
                """;

            var payload = JsonSerializer.Deserialize<FixResultPayload>(json, JsonOpts)!;

            payload.AppliedCount.Should().BeNull("older agents don't send it — the UI must disable undo, not guess 1");
        }

        #endregion

        #region Location cell (📍 link + pin focus)

        private const string Loc =
            "ציר Second Street — תחנה 0+035.05 עד 0+175.48";

        [Fact]
        public void RenderLocationCell_WithAnalysisLocationText_RendersSameLinkAsFindingsTable()
        {
            var html = FixPlanHtmlRenderer.RenderLocationCell(Loc, "Second Street", "רדיוס קטן מהמינימום", null);

            html.Should().Contain("mahod-loc-link");
            html.Should().Contain("data-align=\"Second Street\"");
            html.Should().Contain("data-s1=\"35.05\"");
            html.Should().Contain("data-s2=\"175.48\"");
            html.Should().Contain("data-prob=\"רדיוס קטן מהמינימום\"", "the pin is disambiguated by the row's problem text");
            html.Should().Contain("mahodZoomTo");
            html.Should().Contain("📍");
        }

        [Fact]
        public void RenderLocationCell_NoLocationText_SynthesizesFromTargetStation()
        {
            var toolParams = JsonSerializer.Deserialize<JsonElement>(
                """{"alignment_name":"73","element_index":4,"target_station":374.13}""");

            var html = FixPlanHtmlRenderer.RenderLocationCell(
                "", "73", "עקום מעבר חסר", toolParams, "alignment");

            html.Should().Contain("mahod-loc-link");
            html.Should().Contain("data-align=\"73\"");
            html.Should().Contain("data-s1=\"374.13\"");
            html.Should().Contain("ציר 73 — תחנה 0+374.13", "a synthesized location must read exactly like an agent-written one");
        }

        [Fact]
        public void RenderLocationCell_NonAlignmentObject_IsNotLabelledAsAnAlignment()
        {
            var toolParams = JsonSerializer.Deserialize<JsonElement>(
                """{"profile_name":"D-1","target_station":6628.56}""");

            var html = FixPlanHtmlRenderer.RenderLocationCell(
                "", "D-1", "ערך K נמוך", toolParams, "profile");

            html.Should().NotContain("ציר D-1", "'ציר' means alignment — a profile row must not claim to be one");
            html.Should().Contain("D-1 — תחנה 6+628.56");
            html.Should().Contain("mahod-loc-link", "the row must still be clickable");
        }

        [Fact]
        public void RenderLocationCell_NoLocationAndNoStation_RendersDashNotABrokenLink()
        {
            FixPlanHtmlRenderer.RenderLocationCell("", "73", "d", null)
                .Should().NotContain("mahod-loc-link").And.Contain("&mdash;");
        }

        [Fact]
        public void RenderLocationCell_UnparseableLocationText_RendersEncodedTextWithoutLink()
        {
            var html = FixPlanHtmlRenderer.RenderLocationCell("מפגש <צירים>", "73", "d", null);

            html.Should().NotContain("mahod-loc-link");
            html.Should().Contain("&lt;צירים&gt;");
        }

        [Theory]
        [InlineData(374.13, "0+374.13")]
        [InlineData(35.05, "0+035.05")]
        [InlineData(6628.56, "6+628.56")]
        [InlineData(0.0, "0+000.00")]
        public void FormatStationHe_MatchesTheAgentsKPlusSssContract(double meters, string expected)
        {
            FixPlanHtmlRenderer.FormatStationHe(meters).Should().Be(expected);
        }

        [Fact]
        public void RenderPlan_RendersLocationColumnForEveryItem()
        {
            var plan = MakePlan(items: new List<FixPlanItem>
            {
                new()
                {
                    Id = "i1",
                    ObjectName = "Second Street",
                    Location = Loc,
                    Description = "רדיוס קטן מהמינימום",
                    Severity = "critical",
                },
            });

            var html = FixPlanHtmlRenderer.RenderPlan(plan);

            html.Should().Contain(">מיקום</th>");
            html.Should().Contain("mahod-loc-link");
            html.Should().Contain("data-s1=\"35.05\"");
        }

        [Fact]
        public void RenderCombinedResult_RendersObjectNameAndLocation()
        {
            var result = new FixResultPayload
            {
                PlanId = "plan-1",
                Success = true,
                AppliedCount = 1,
                Results = new List<FixResultItem>
                {
                    new()
                    {
                        ItemId = "i1",
                        Status = "applied",
                        ObjectName = "Second Street",
                        Location = Loc,
                        Description = "רדיוס",
                    },
                },
            };

            var html = FixPlanHtmlRenderer.RenderCombinedResult(result, null);

            html.Should().Contain("Second Street", "object_name now comes through from the agent");
            html.Should().Contain("mahod-loc-link");
            html.Should().Contain("data-s1=\"35.05\"");
        }

        [Fact]
        public void RenderCombinedResult_OlderAgentWithoutObjectName_RendersDash()
        {
            var result = new FixResultPayload
            {
                PlanId = "plan-1",
                Success = true,
                AppliedCount = 1,
                Results = new List<FixResultItem>
                {
                    new() { ItemId = "i1", Status = "applied", Description = "רדיוס" },
                },
            };

            FixPlanHtmlRenderer.RenderCombinedResult(result, null).Should().Contain("&mdash;");
        }

        #endregion
    }
}
