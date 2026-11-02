using System;
using System.Collections.Generic;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Sections.Services
{
    /// <summary>What the palette hands the pick command (2026-10): the drawing it was started from, the half-width to
    /// offer, and which alignments the profile allows. Never the profile itself — the panel already validated it.</summary>
    internal sealed record TypicalSectionPickRequest(
        string Token, IntPtr Database, double DefaultHalfWidthM, IReadOnlyList<string> AllowedAlignmentNames);

    /// <summary>One marked section: the two marker entities, the alignment and station the click resolved to, and the
    /// chord geometry (axis point, tangent, ends) in world coordinates.</summary>
    internal sealed record TypicalSectionPick(
        string LineHandle, string TextHandle, string AlignmentName, double Station, double HalfWidthM,
        double AxisX, double AxisY, double TangentDeg, double AX, double AY, double BX, double BY)
    {
        /// <summary>
        /// True when the axis is a plan polyline (a drafted drawing with no Civil alignment): the section is then read
        /// off the plan (<see cref="DraftedTypicalSectionService"/>), there being no corridor for the firm's program.
        /// </summary>
        public bool FromPlan { get; init; }
    }

    /// <summary>The pick command's report back to the palette. Always sent, so a pending request never leaks.</summary>
    internal sealed class TypicalSectionPickResult
    {
        public TypicalSectionPickResult(string token) { Token = token; }
        public string Token { get; }
        public List<TypicalSectionPick> Picks { get; } = new();
        public bool Cancelled { get; set; }
        public string? Error { get; set; }
        public double? HalfWidthM { get; set; }
    }

    /// <summary>
    /// Token → request, between <c>SendStringToExecute</c> in the palette and the command reading the token on the
    /// command line. One take per token: a stale or repeated command finds nothing and does nothing.
    /// </summary>
    internal static class TypicalSectionPickRequests
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, TypicalSectionPickRequest> Pending = new(StringComparer.Ordinal);

        public static void Register(TypicalSectionPickRequest request)
        {
            lock (Gate) Pending[request.Token] = request;
        }

        public static TypicalSectionPickRequest? TryTake(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;
            lock (Gate) return Pending.Remove(token, out var request) ? request : null;
        }

        public static void Forget(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            lock (Gate) Pending.Remove(token);
        }
    }
}
