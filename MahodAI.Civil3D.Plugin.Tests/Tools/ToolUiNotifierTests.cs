using System;
using System.Threading.Tasks;
using MahodAI.Civil3D.Plugin.Tools;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.Tools
{
    /// <summary>
    /// Contract of the interactive-tool UI hooks ("hint one step late" fix):
    /// unset hooks are no-ops, broken hooks never take a tool down.
    /// Hooks are static — every test restores them.
    /// </summary>
    public class ToolUiNotifierTests
    {
        [Fact]
        public void Hint_WithoutHook_DoesNotThrow()
        {
            var saved = ToolUiNotifier.PostChatHint;
            try
            {
                ToolUiNotifier.PostChatHint = null;
                ToolUiNotifier.Hint("שלום");
            }
            finally
            {
                ToolUiNotifier.PostChatHint = saved;
            }
        }

        // ── Step line (2026-08-04): ONE line rewritten per step, not a bubble
        //    per step. The step text must never reach PostChatHint, or the
        //    per-step-bubble spam the owner reported comes straight back.

        [Fact]
        public void Step_GoesToSetStepHint_NotToAChatBubble()
        {
            var savedStep = ToolUiNotifier.SetStepHint;
            var savedPost = ToolUiNotifier.PostChatHint;
            try
            {
                string? step = null;
                int bubbles = 0;
                ToolUiNotifier.SetStepHint = t => step = t;
                ToolUiNotifier.PostChatHint = _ => bubbles++;

                ToolUiNotifier.Step("שלב 1/3 — בחר נקודת התחלה");

                Assert.Equal("שלב 1/3 — בחר נקודת התחלה", step);
                Assert.Equal(0, bubbles);
            }
            finally
            {
                ToolUiNotifier.SetStepHint = savedStep;
                ToolUiNotifier.PostChatHint = savedPost;
            }
        }

        [Fact]
        public void Step_ReplacesThePreviousStep_OneLinePerFlow()
        {
            var saved = ToolUiNotifier.SetStepHint;
            try
            {
                var seen = new System.Collections.Generic.List<string>();
                ToolUiNotifier.SetStepHint = t => seen.Add(t);

                ToolUiNotifier.Step("שלב 1/3");
                ToolUiNotifier.Step("שלב 2/3");
                ToolUiNotifier.Step("שלב 3/3");

                // Each call targets the same line; the LAST one is what shows.
                Assert.Equal(new[] { "שלב 1/3", "שלב 2/3", "שלב 3/3" }, seen);
            }
            finally
            {
                ToolUiNotifier.SetStepHint = saved;
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Step_IgnoresBlankText(string? text)
        {
            var saved = ToolUiNotifier.SetStepHint;
            try
            {
                int calls = 0;
                ToolUiNotifier.SetStepHint = _ => calls++;
                ToolUiNotifier.Step(text!);
                Assert.Equal(0, calls);
            }
            finally
            {
                ToolUiNotifier.SetStepHint = saved;
            }
        }

        [Fact]
        public void Step_WithoutHook_DoesNotThrow()
        {
            var saved = ToolUiNotifier.SetStepHint;
            try
            {
                ToolUiNotifier.SetStepHint = null;
                ToolUiNotifier.Step("שלב 1/3");
            }
            finally
            {
                ToolUiNotifier.SetStepHint = saved;
            }
        }

        [Fact]
        public void Step_BrokenHook_DoesNotTakeTheToolDown()
        {
            var saved = ToolUiNotifier.SetStepHint;
            try
            {
                ToolUiNotifier.SetStepHint = _ => throw new InvalidOperationException("ui boom");
                ToolUiNotifier.Step("שלב 1/3");
            }
            finally
            {
                ToolUiNotifier.SetStepHint = saved;
            }
        }

        [Fact]
        public void ClearStep_InvokesHook_AndSurvivesBothUnsetAndBroken()
        {
            var saved = ToolUiNotifier.ClearStepHint;
            try
            {
                int cleared = 0;
                ToolUiNotifier.ClearStepHint = () => cleared++;
                ToolUiNotifier.ClearStep();
                Assert.Equal(1, cleared);

                ToolUiNotifier.ClearStepHint = null;
                ToolUiNotifier.ClearStep();

                ToolUiNotifier.ClearStepHint = () => throw new InvalidOperationException("ui boom");
                ToolUiNotifier.ClearStep();
            }
            finally
            {
                ToolUiNotifier.ClearStepHint = saved;
            }
        }

        [Fact]
        public void Hint_InvokesHook_AndSkipsBlankText()
        {
            var saved = ToolUiNotifier.PostChatHint;
            try
            {
                string? received = null;
                int calls = 0;
                ToolUiNotifier.PostChatHint = t => { received = t; calls++; };

                ToolUiNotifier.Hint("בחר נקודת התחלה");
                ToolUiNotifier.Hint("   ");
                ToolUiNotifier.Hint("");

                Assert.Equal("בחר נקודת התחלה", received);
                Assert.Equal(1, calls);
            }
            finally
            {
                ToolUiNotifier.PostChatHint = saved;
            }
        }

        [Fact]
        public void Hint_HookThrows_IsSwallowed()
        {
            var saved = ToolUiNotifier.PostChatHint;
            try
            {
                ToolUiNotifier.PostChatHint = _ => throw new InvalidOperationException("ui boom");
                ToolUiNotifier.Hint("hint");
            }
            finally
            {
                ToolUiNotifier.PostChatHint = saved;
            }
        }

        [Fact]
        public async Task FlushChatAsync_WithoutHook_Completes()
        {
            var saved = ToolUiNotifier.FlushChat;
            try
            {
                ToolUiNotifier.FlushChat = null;
                await ToolUiNotifier.FlushChatAsync();
            }
            finally
            {
                ToolUiNotifier.FlushChat = saved;
            }
        }

        [Fact]
        public async Task FlushChatAsync_AwaitsHook()
        {
            var saved = ToolUiNotifier.FlushChat;
            try
            {
                bool flushed = false;
                ToolUiNotifier.FlushChat = async () =>
                {
                    await Task.Delay(1);
                    flushed = true;
                };

                await ToolUiNotifier.FlushChatAsync();

                Assert.True(flushed);
            }
            finally
            {
                ToolUiNotifier.FlushChat = saved;
            }
        }

        [Fact]
        public async Task FlushChatAsync_SynchronousHookThrow_IsSwallowed()
        {
            var saved = ToolUiNotifier.FlushChat;
            try
            {
                ToolUiNotifier.FlushChat = () => throw new InvalidOperationException("flush boom");
                await ToolUiNotifier.FlushChatAsync();
            }
            finally
            {
                ToolUiNotifier.FlushChat = saved;
            }
        }
    }
}
