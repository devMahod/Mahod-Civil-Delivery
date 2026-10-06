using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MahodAI.CivilDelivery.Estimate;

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI;

/// <summary>
/// Pages existing evidence, never filters it out or changes approval. Only one
/// small issue batch is formatted at a time, and exceptionally long identities
/// or messages remain accessible in subsequent character pages.
/// </summary>
internal sealed class EstimateReviewPager
{
    internal const int IssuesPerBatch = 20;
    internal const int CharactersPerPage = 12000;
    private readonly IReadOnlyList<EstimateReviewPolicy.Issue> _issues;
    private readonly (string? DrawingPath, string? XrefChain)[] _sources;
    private readonly string? _gateReason;
    private readonly string? _sourceDrawing;
    private string _batchText = "";
    internal IReadOnlyList<EstimateReviewPolicy.Issue> CurrentBatch { get; private set; } = Array.Empty<EstimateReviewPolicy.Issue>();
    private int _batch;
    private int _characterPage;

    internal EstimateReviewPager(string? gateReason, string? sourceDrawing,
        IEnumerable<(string? DrawingPath, string? XrefChain)> sources,
        IReadOnlyList<EstimateReviewPolicy.Issue> issues)
    {
        _gateReason = gateReason;
        _sourceDrawing = sourceDrawing;
        _sources = sources.ToArray();
        _issues = issues;
        BlockingCount = issues.Count(issue => issue.Blocking);
        LoadBatch();
    }

    /// <summary>Drills into one presentation group without flattening or replacing its evidence.</summary>
    internal static EstimateReviewPager ForGroup(string? gateReason, string? sourceDrawing,
        IEnumerable<(string? DrawingPath, string? XrefChain)> sources,
        EstimateReviewGroupingPolicy.GroupSummary group) =>
        new(gateReason, sourceDrawing, sources, group.Issues);

    internal int IssueCount => _issues.Count;
    internal int BlockingCount { get; }
    internal int BatchCount => Math.Max(1, (int)Math.Ceiling(_issues.Count / (double)IssuesPerBatch));
    private int CharacterPageCount => Math.Max(1, (int)Math.Ceiling(_batchText.Length / (double)CharactersPerPage));
    internal bool CanPrevious => _batch > 0 || _characterPage > 0;
    internal bool CanNext => _batch + 1 < BatchCount || _characterPage + 1 < CharacterPageCount;
    internal string Position => $"{IssueCount:N0} ממצאים · {BlockingCount:N0} חסמים · " +
        $"קבוצת תצוגה {_batch + 1}/{BatchCount} · חלק {_characterPage + 1}/{CharacterPageCount}";
    internal string Text
    {
        get
        {
            var start = _characterPage * CharactersPerPage;
            return _batchText.Substring(start, Math.Min(CharactersPerPage, _batchText.Length - start));
        }
    }

    internal bool MoveNext()
    {
        if (!CanNext) return false;
        if (_characterPage + 1 < CharacterPageCount) _characterPage++;
        else { _batch++; _characterPage = 0; LoadBatch(); }
        return true;
    }

    internal bool MovePrevious()
    {
        if (!CanPrevious) return false;
        if (_characterPage > 0) _characterPage--;
        else { _batch--; LoadBatch(); _characterPage = CharacterPageCount - 1; }
        return true;
    }

    private void LoadBatch()
    {
        var first = _batch * IssuesPerBatch;
        var count = Math.Min(IssuesPerBatch, _issues.Count - first);
        var batch = new EstimateReviewPolicy.Issue[count];
        for (var i = 0; i < count; i++) batch[i] = _issues[first + i];
        CurrentBatch = batch;
        _batchText = EstimateGuidedReviewText.BuildUnified(_gateReason, _sourceDrawing, _sources, batch);
    }

    internal static string RowSummary(IEnumerable<EstimateReviewPolicy.Issue> issues)
    {
        var groups = issues.GroupBy(issue => (issue.Code, issue.Blocking)).ToList();
        var text = new StringBuilder();
        text.AppendLine("סיכום ממצאים לקבוצה — כל הפרטים זמינים ב׳כמויות וממצאים׳:");
        var shown = 0;
        foreach (var group in groups)
        {
            if (shown++ == 12) break;
            var title = group.First().Title;
            if (title.Length > 200) title = title[..200] + "…";
            text.AppendLine($"{group.Count():N0} × {(group.Key.Blocking ? "חסם" : "מידע")} · {group.Key.Code} · {title}");
        }
        if (groups.Count > 12) text.AppendLine($"ועוד {groups.Count - 12} סוגי ממצאים בחלון הפירוט.");
        return text.ToString();
    }
}
