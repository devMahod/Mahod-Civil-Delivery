using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Data;
using System.Windows.Threading;
using MahodAI.Civil3D.Plugin.CivilDelivery.UI;
using Xunit;

namespace MahodAI.Civil3D.Plugin.Tests.CivilDelivery;

public sealed class QuantitySearchDebouncerTests
{
    // Real WPF textbox/dispatcher/view, no HWND and no Civil host/keyboard automation.
    [Fact]
    public void FastTypingAndPasteCoalesceWithoutChangingTextCaretOr675SourceRows() => Sta(() =>
    {
        var text = new TextBox();
        var rows = new ObservableCollection<QuantityRowViewModel>(Enumerable.Range(0, 675).Select(i =>
            new QuantityRowViewModel { RuleKey = "test-" + i, Layer = i == 418 ? "2519-simun818" : "other-" + i,
                EntityType = "TEST", Method = "test-only", MappingState = "test-only unapproved",
                Quantity = i + 0.5, ObjectCount = 1, Unit = "מטר" }));
        var source = rows.ToArray(); var quantities = rows.Select(r => r.Quantity).ToArray();
        var view = new ListCollectionView(rows);
        view.Filter = value => QuantityReviewFilter.Matches((QuantityRowViewModel)value, text.Text, QuantityReviewFilter.Mode.All);
        var refreshes = 0;
        var debounce = new QuantitySearchDebouncer(Dispatcher.CurrentDispatcher, () => { refreshes++; view.Refresh(); });
        text.TextChanged += (_, _) => debounce.Schedule();
        foreach (var character in "simun818") { text.AppendText(character.ToString()); text.CaretIndex = text.Text.Length; }
        var caret = text.CaretIndex;
        Assert.True(debounce.IsPending); Assert.Equal(0, refreshes); Assert.Equal(675, view.Count);
        PumpUntil(() => refreshes == 1);
        Assert.Equal("simun818", text.Text); Assert.Equal(caret, text.CaretIndex);
        Assert.Same(source[418], Assert.Single(view.Cast<QuantityRowViewModel>()));
        Assert.Equal(source, rows); Assert.Equal(quantities, rows.Select(r => r.Quantity));
        // Replacing a selection (paste semantics) uses the final current query, not a captured old query.
        text.SelectAll(); text.SelectedText = "other-674"; text.CaretIndex = text.Text.Length;
        PumpUntil(() => refreshes == 2);
        Assert.Equal("other-674", text.Text);
        Assert.Same(source[674], Assert.Single(view.Cast<QuantityRowViewModel>()));
        Assert.False(debounce.IsPending);
    });

    [Fact]
    public void ClearModeChangeAndUnloadCancelPendingRefreshAndCanResume() => Sta(() =>
    {
        var text = new TextBox(); var refreshes = 0;
        var debounce = new QuantitySearchDebouncer(Dispatcher.CurrentDispatcher, () => refreshes++);
        text.TextChanged += (_, _) => debounce.Schedule();
        text.Text = "simun";
        text.Clear(); debounce.Cancel(); refreshes++; // Same clear ordering as the live handler.
        PumpFor(TimeSpan.FromMilliseconds(340));
        Assert.Equal(1, refreshes); Assert.Equal("", text.Text); Assert.False(debounce.IsPending);
        text.Text = "pending"; debounce.Cancel(); // palette unload / explicit mode change
        PumpFor(TimeSpan.FromMilliseconds(340));
        Assert.Equal(1, refreshes); Assert.Equal("pending", text.Text);
        text.Text = "after reopen";
        PumpUntil(() => refreshes == 2);
        Assert.Equal("after reopen", text.Text); Assert.False(debounce.IsPending);
    });

    [Fact]
    public void LiveWiringDefersOnlySearchAndKeepsClearSelectionAndUnloadImmediate()
    {
        var dir = typeof(QuantitySearchDebouncerTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "MahodPluginSourceDir").Value!;
        var source = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "UI", "CivilDeliveryControl.QuantityFilter.cs"));
        Assert.Contains("ReferenceEquals(sender, QuantitySearchBox)", source);
        Assert.Contains("_quantitySearchDebouncer?.Schedule();", source);
        Assert.Contains("if (!force && _quantitySearchDebouncer?.IsPending == true) return;", source);
        Assert.Contains("Unloaded += (_, _) => _quantitySearchDebouncer.Cancel();", source);
        Assert.Contains("Loaded += (_, _) => RefreshQuantityReviewFilter(force: true);", source);
        Assert.Contains("QuantitySearchBox.Clear(); QuantityFilterBox.SelectedIndex = 0;", source);
        var select = source[source.IndexOf("private void SelectQuantityReviewRow", StringComparison.Ordinal)..];
        Assert.True(select.IndexOf("RefreshQuantityReviewFilter(force: true)", StringComparison.Ordinal) <
            select.IndexOf("QuantitiesGrid.SelectedItem = row", StringComparison.Ordinal));
        var known = File.ReadAllText(Path.Combine(dir, "CivilDelivery", "UI", "CivilDeliveryControl.KnownPriceBooks.cs"));
        Assert.True(known.IndexOf("KnownPriceBookReuse.DescribePreWriteFailure(ex)", StringComparison.Ordinal) <
            known.IndexOf("var writeAttempted = false", StringComparison.Ordinal));
    }

    private static void PumpUntil(Func<bool> done)
    {
        var elapsed = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || elapsed.Elapsed > TimeSpan.FromSeconds(5)) frame.Continue = false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        Assert.True(done(), "WPF refresh did not complete within the bounded dispatcher pump");
    }
    private static void PumpFor(TimeSpan duration)
    { var clock = Stopwatch.StartNew(); PumpUntil(() => clock.Elapsed >= duration); }
    private static void Sta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF test timed out");
        if (error != null) throw new InvalidOperationException("WPF search regression failed", error);
    }
}
