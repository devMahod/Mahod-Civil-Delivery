using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.WebSocket;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    public class WebSocketMessageSerializationTests
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private static T Roundtrip<T>(T obj)
        {
            var json = JsonSerializer.Serialize(obj, Options);
            return JsonSerializer.Deserialize<T>(json, Options)!;
        }

        #region WebSocketMessage Envelope

        [Fact]
        public void WebSocketMessage_Roundtrip_PreservesFields()
        {
            var msg = new WebSocketMessage
            {
                Id = "abc123",
                Type = MessageTypes.Chat,
                SessionId = "session-1",
                CorrelationId = "corr-1"
            };

            var result = Roundtrip(msg);
            result.Id.Should().Be("abc123");
            result.Type.Should().Be("chat");
            result.SessionId.Should().Be("session-1");
            result.CorrelationId.Should().Be("corr-1");
        }

        [Fact]
        public void WebSocketMessage_HasJsonPropertyNameAttributes()
        {
            var msg = new WebSocketMessage { Type = "test", SessionId = "s1" };
            var json = JsonSerializer.Serialize(msg);
            // JsonPropertyName attributes should produce snake_case
            json.Should().Contain("\"type\"");
            json.Should().Contain("\"session_id\"");
            json.Should().Contain("\"correlation_id\"");
        }

        #endregion

        #region ConnectPayload

        [Fact]
        public void ConnectPayload_Roundtrip_PreservesFields()
        {
            var payload = new ConnectPayload
            {
                ApiKey = "test-key",
                ClientVersion = "2.0.0",
                ClientType = "civil3d_plugin",
                Capabilities = new List<string> { "tool_execution", "streaming" }
            };

            var result = Roundtrip(payload);
            result.ApiKey.Should().Be("test-key");
            result.ClientVersion.Should().Be("2.0.0");
            result.ClientType.Should().Be("civil3d_plugin");
            result.Capabilities.Should().Contain("streaming");
        }

        [Fact]
        public void ConnectPayload_DefaultCapabilities_AreSet()
        {
            var payload = new ConnectPayload();
            payload.Capabilities.Should().Contain("tool_execution");
            payload.Capabilities.Should().Contain("event_notifications");
            payload.Capabilities.Should().Contain("streaming");
        }

        #endregion

        #region ConnectAckPayload

        [Fact]
        public void ConnectAckPayload_Roundtrip_PreservesFields()
        {
            var payload = new ConnectAckPayload
            {
                Success = true,
                ConnectionId = "conn-1",
                ServerVersion = "1.0.0"
            };

            var result = Roundtrip(payload);
            result.Success.Should().BeTrue();
            result.ConnectionId.Should().Be("conn-1");
            result.ServerVersion.Should().Be("1.0.0");
        }

        #endregion

        #region ToolCallPayload / ToolResultPayload

        [Fact]
        public void ToolCallPayload_Roundtrip_PreservesFields()
        {
            var payload = new ToolCallPayload
            {
                ToolCallId = "tc-1",
                ToolName = "get_alignments",
                TimeoutSeconds = 45
            };

            var result = Roundtrip(payload);
            result.ToolCallId.Should().Be("tc-1");
            result.ToolName.Should().Be("get_alignments");
            result.TimeoutSeconds.Should().Be(45);
        }

        [Fact]
        public void ToolResultPayload_Roundtrip_PreservesFields()
        {
            var payload = new ToolResultPayload
            {
                ToolCallId = "tc-1",
                Success = true,
                ExecutionTimeMs = 250,
                Cached = false
            };

            var result = Roundtrip(payload);
            result.ToolCallId.Should().Be("tc-1");
            result.Success.Should().BeTrue();
            result.ExecutionTimeMs.Should().Be(250);
            result.Cached.Should().BeFalse();
        }

        [Fact]
        public void ToolResultPayload_WithError_Roundtrip()
        {
            var payload = new ToolResultPayload
            {
                ToolCallId = "tc-2",
                Success = false,
                Error = new ToolError
                {
                    Code = "TOOL_NOT_FOUND",
                    Message = "Tool does not exist",
                    Details = "extra info"
                }
            };

            var result = Roundtrip(payload);
            result.Success.Should().BeFalse();
            result.Error.Should().NotBeNull();
            result.Error!.Code.Should().Be("TOOL_NOT_FOUND");
            result.Error.Message.Should().Be("Tool does not exist");
            result.Error.Details.Should().Be("extra info");
        }

        #endregion

        #region ChatPayload / AnalyzePayload

        [Fact]
        public void ChatPayload_Roundtrip_PreservesFields()
        {
            var payload = new ChatPayload
            {
                Content = "What alignments exist?",
                Context = new ChatContext
                {
                    SelectedObjects = new List<string> { "obj1", "obj2" },
                    CurrentView = new ViewInfo { CenterX = 180000, CenterY = 600000, ZoomLevel = 1.5 }
                }
            };

            var result = Roundtrip(payload);
            result.Content.Should().Be("What alignments exist?");
            result.Context.Should().NotBeNull();
            result.Context!.SelectedObjects.Should().HaveCount(2);
            result.Context.CurrentView!.CenterX.Should().Be(180000);
        }

        [Fact]
        public void ChatPayload_Roundtrip_PreservesImageAttachments()
        {
            var payload = new ChatPayload
            {
                Content = "מה הרדיוס בסקיצה?",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment
                    {
                        Kind = "image",
                        Filename = "sketch.png",
                        MimeType = "image/png",
                        Data = "iVBORw0KGgo="
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, Options);
            // The agent reads snake_case; a camelCase mime_type would be dropped
            // silently and the image rejected as an unsupported format.
            json.Should().Contain("\"mime_type\"");
            json.Should().Contain("\"attachments\"");

            var result = Roundtrip(payload);
            result.Attachments.Should().HaveCount(1);
            result.Attachments![0].Kind.Should().Be("image");
            result.Attachments[0].Filename.Should().Be("sketch.png");
            result.Attachments[0].MimeType.Should().Be("image/png");
            result.Attachments[0].Data.Should().Be("iVBORw0KGgo=");
        }

        [Fact]
        public void ChatPayload_Roundtrip_PreservesDocumentAttachments()
        {
            // v1.17: PDFs and text files travel whole. DOCX/XLS do NOT — no
            // provider accepts them — so they keep the extract-to-text route
            // and must never be emitted with kind "document".
            var payload = new ChatPayload
            {
                Content = "מה הרדיוס לפי המסמך?",
                Attachments = new List<ChatAttachment>
                {
                    new ChatAttachment
                    {
                        Kind = "document",
                        Filename = "guidelines.pdf",
                        MimeType = "application/pdf",
                        Data = "JVBERi0xLjQ="
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, Options);
            json.Should().Contain("\"kind\":\"document\"");
            json.Should().Contain("\"mime_type\":\"application/pdf\"");

            var result = Roundtrip(payload);
            result.Attachments.Should().HaveCount(1);
            result.Attachments![0].Kind.Should().Be("document");
            result.Attachments[0].Filename.Should().Be("guidelines.pdf");
            result.Attachments[0].Data.Should().Be("JVBERi0xLjQ=");
        }

        [Fact]
        public void ChatPayload_WithoutAttachments_OmitsTheKeyEntirely()
        {
            // PROTOCOL v1.16 is additive: an ordinary question must stay
            // byte-identical to what pre-v1.16 plugins sent, so old servers and
            // the golden chat fixtures keep matching.
            var json = JsonSerializer.Serialize(new ChatPayload { Content = "שלום" }, Options);
            json.Should().NotContain("attachments");
        }

        [Fact]
        public void AnalyzePayload_Roundtrip_WithFocusAreas()
        {
            var payload = new AnalyzePayload
            {
                Instructions = "Check grade limits",
                FocusAreas = new List<string> { "alignments", "profiles" }
            };

            var result = Roundtrip(payload);
            result.Instructions.Should().Be("Check grade limits");
            result.FocusAreas.Should().Contain("alignments");
        }

        #endregion

        #region StreamPayloads

        [Fact]
        public void StreamTokenPayload_Roundtrip_PreservesFields()
        {
            var payload = new StreamTokenPayload
            {
                StreamId = "s-1",
                Token = "Hello ",
                Sequence = 42
            };

            var result = Roundtrip(payload);
            result.StreamId.Should().Be("s-1");
            result.Token.Should().Be("Hello ");
            result.Sequence.Should().Be(42);
        }

        [Fact]
        public void StreamEndPayload_Roundtrip_WithReferences()
        {
            var payload = new StreamEndPayload
            {
                StreamId = "s-1",
                TotalTokens = 100,
                Complete = true,
                References = new List<RagReference>
                {
                    new RagReference
                    {
                        DocumentName = "Standards.pdf",
                        Url = "https://example.com/doc",
                        Pages = new List<int> { 1, 5, 10 }
                    }
                }
            };

            var result = Roundtrip(payload);
            result.Complete.Should().BeTrue();
            result.TotalTokens.Should().Be(100);
            result.References.Should().HaveCount(1);
            result.References![0].DocumentName.Should().Be("Standards.pdf");
            result.References[0].Pages.Should().Contain(5);
        }

        #endregion

        #region Fix Workflow Payloads

        [Fact]
        public void FixRequestPayload_Roundtrip_PreservesFields()
        {
            var payload = new FixRequestPayload
            {
                RecommendationIds = new List<string> { "rec-1", "rec-2" },
                UserInstructions = "Fix the grade"
            };

            var result = Roundtrip(payload);
            result.RecommendationIds.Should().Contain("rec-1");
            result.UserInstructions.Should().Be("Fix the grade");
        }

        [Fact]
        public void FixPlanPayload_Roundtrip_PreservesFields()
        {
            var payload = new FixPlanPayload
            {
                PlanId = "plan-1",
                Summary = "Adjust 2 alignments",
                Items = new List<FixPlanItem>
                {
                    new FixPlanItem
                    {
                        Id = "item-1",
                        ObjectName = "Alignment-1",
                        ObjectType = "Alignment",
                        Description = "Increase radius",
                        ToolName = "modify_alignment_curve",
                        Severity = "critical"
                    }
                }
            };

            var result = Roundtrip(payload);
            result.PlanId.Should().Be("plan-1");
            result.Summary.Should().Be("Adjust 2 alignments");
            result.Items.Should().HaveCount(1);
            result.Items[0].ToolName.Should().Be("modify_alignment_curve");
        }

        [Fact]
        public void FixApprovalPayload_Roundtrip_PreservesFields()
        {
            var payload = new FixApprovalPayload
            {
                PlanId = "plan-1",
                Decision = "approved",
                UserMessage = "Looks good"
            };

            var result = Roundtrip(payload);
            result.PlanId.Should().Be("plan-1");
            result.Decision.Should().Be("approved");
            result.UserMessage.Should().Be("Looks good");
        }

        #endregion

        #region ErrorPayload

        [Fact]
        public void ErrorPayload_Roundtrip_PreservesFields()
        {
            var payload = new ErrorPayload
            {
                Code = ErrorCodes.ToolTimeout,
                Message = "Tool timed out",
                Recoverable = true
            };

            var result = Roundtrip(payload);
            result.Code.Should().Be("TOOL_TIMEOUT");
            result.Message.Should().Be("Tool timed out");
            result.Recoverable.Should().BeTrue();
        }

        #endregion

        #region MessageTypes Constants

        [Fact]
        public void MessageTypes_HaveExpectedValues()
        {
            MessageTypes.Connect.Should().Be("connect");
            MessageTypes.ToolCall.Should().Be("tool_call");
            MessageTypes.ToolResult.Should().Be("tool_result");
            MessageTypes.StreamStart.Should().Be("stream_start");
            MessageTypes.StreamToken.Should().Be("stream_token");
            MessageTypes.StreamEnd.Should().Be("stream_end");
            MessageTypes.FixRequest.Should().Be("fix_request");
            MessageTypes.FixPlan.Should().Be("fix_plan");
            MessageTypes.FixApproval.Should().Be("fix_approval");
            MessageTypes.FixResult.Should().Be("fix_result");
            MessageTypes.Error.Should().Be("error");
        }

        [Fact]
        public void StatusTypes_HaveExpectedValues()
        {
            StatusTypes.Thinking.Should().Be("thinking");
            StatusTypes.UsingTool.Should().Be("using_tool");
            StatusTypes.RagLookup.Should().Be("rag_lookup");
            StatusTypes.PlanningFixes.Should().Be("planning_fixes");
            StatusTypes.ApplyingFixes.Should().Be("applying_fixes");
        }

        #endregion
    }
}
