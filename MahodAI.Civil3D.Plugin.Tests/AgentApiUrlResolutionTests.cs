using System;
using FluentAssertions;
using MahodAI.Civil3D.Plugin.Config;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests
{
    /// <summary>
    /// How the plugin decides which agent it talks to.
    ///
    /// This is the single most frequent field failure in the product: a stale
    /// MAHOD_AGENT_API_URL outranks the address compiled into the build, so a PC
    /// that once ran the old setup-server.bat keeps reaching the wrong server
    /// through any number of reinstalls. Four separate reports so far, the last
    /// one an engineer whose fresh production install announced
    /// "http://dev.mahodeng.co.il:8002".
    ///
    /// The empty-string case is the subtle one. Every call site used
    /// <c>GetEnvironmentVariable(...) ?? Default</c>, which falls back only on
    /// NULL — so clearing the variable the obvious way, <c>setx VAR ""</c>,
    /// leaves an empty value that passes the null check and becomes the base URL.
    /// That turns "wrong server" into "no server", which is why install.bat now
    /// REMOVES the variable rather than blanking it.
    /// </summary>
    [Collection("EnvironmentVariable")]
    public class AgentApiUrlResolutionTests : IDisposable
    {
        private readonly string? _original =
            Environment.GetEnvironmentVariable(PluginConstants.AgentApiUrlVariable);

        private static void Set(string? value) =>
            Environment.SetEnvironmentVariable(PluginConstants.AgentApiUrlVariable, value);

        public void Dispose() => Set(_original);

        [Fact]
        public void With_no_override_the_compiled_in_address_is_used()
        {
            Set(null);

            PluginConstants.ResolveAgentApiUrl().Should().Be(PluginConstants.DefaultAgentApiUrl);
            PluginConstants.AgentApiUrlIsOverridden.Should().BeFalse();
        }

        [Fact]
        public void A_real_override_wins_over_the_compiled_in_address()
        {
            Set("https://example.invalid/api");

            PluginConstants.ResolveAgentApiUrl().Should().Be("https://example.invalid/api");
            PluginConstants.AgentApiUrlIsOverridden.Should().BeTrue();
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void A_blank_override_falls_back_instead_of_becoming_the_address(string blank)
        {
            Set(blank);

            PluginConstants.ResolveAgentApiUrl()
                .Should().Be(PluginConstants.DefaultAgentApiUrl,
                    "a blanked variable must not be treated as a server address");
            PluginConstants.AgentApiUrlIsOverridden
                .Should().BeFalse("a blank value is not an override worth reporting");
        }

        [Fact]
        public void Surrounding_whitespace_is_trimmed()
        {
            Set("  https://example.invalid/api  ");

            PluginConstants.ResolveAgentApiUrl().Should().Be("https://example.invalid/api");
        }

        [Fact]
        public void Every_path_resolves_the_same_address()
        {
            // The URL was written out in three places once, and a production build
            // sent chat to prod while REST and uploads still went to dev.
            Set("https://example.invalid/api");

            MahodAI.Civil3D.Plugin.Services.AgentCommunicationService.GetBaseUrl()
                .Should().Be(PluginConstants.ResolveAgentApiUrl());
        }
    }
}
