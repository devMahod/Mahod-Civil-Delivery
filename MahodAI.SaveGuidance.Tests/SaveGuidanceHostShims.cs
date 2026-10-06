// Host-boundary simulation only. The tests compile the complete actual SaveGuidance
// partial; no copy of its queue/token/readiness algorithm lives in these shims.
using System;
using System.Collections.Generic;
using System.Linq;

namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public delegate void RoutedEventHandler(object sender, RoutedEventArgs e);
    public enum MessageBoxButton { OK, YesNo }
    public enum MessageBoxImage { Warning, Question }
    public enum MessageBoxResult { None = 0, OK = 1, Yes = 6, No = 7 }
    [Flags]
    public enum MessageBoxOptions { None = 0, RightAlign = 0x80000, RtlReading = 0x100000 }
    public static class MessageBox
    {
        public static MessageBoxResult Answer = MessageBoxResult.Yes;
        public static Action? BeforeAnswer;
        public static MessageBoxResult LastDefaultResult;
        public static MessageBoxOptions LastOptions;
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image)
        { var action = BeforeAnswer; BeforeAnswer = null; action?.Invoke(); return Answer; }
        public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons,
            MessageBoxImage image, MessageBoxResult defaultResult, MessageBoxOptions options)
        {
            LastDefaultResult = defaultResult; LastOptions = options;
            return Show(text, caption, buttons, image);
        }
    }
}

namespace Autodesk.AutoCAD.ApplicationServices
{
    public sealed class CommandEventArgs(string name) : EventArgs { public string GlobalCommandName => name; }
    public sealed class DocumentCollectionEventArgs(Document document) : EventArgs { public Document Document => document; }
    public sealed class TestDatabase
    {
        public IntPtr UnmanagedObject { get; set; } = new(17);
        public string? Filename { get; set; }
    }
    public sealed class Document
    {
        public string Name { get; set; } = "Drawing1.dwg";
        public TestDatabase Database { get; } = new();
        public event EventHandler<CommandEventArgs>? CommandEnded;
        public event EventHandler<CommandEventArgs>? CommandCancelled;
        public event EventHandler<CommandEventArgs>? CommandFailed;
        public readonly List<string> Sent = [];
        public bool ThrowOnSend { get; set; }
        public string CommandInProgress { get; set; } = "";
        public void SendStringToExecute(string text, bool activate, bool wrapUpInactiveDoc, bool echoCommand)
        { if (ThrowOnSend) throw new InvalidOperationException("simulated transport failure"); Sent.Add(text); }
        public int Subscriptions => (CommandEnded?.GetInvocationList().Length ?? 0) +
            (CommandCancelled?.GetInvocationList().Length ?? 0) + (CommandFailed?.GetInvocationList().Length ?? 0);
        public void End(string command = "QSAVE") => CommandEnded?.Invoke(this, new(command));
        public void Cancel(string command = "QSAVE") => CommandCancelled?.Invoke(this, new(command));
        public void Fail(string command = "QSAVE") => CommandFailed?.Invoke(this, new(command));
    }
}

namespace Autodesk.AutoCAD.ApplicationServices.Core
{
    public static class Application
    {
        public static event EventHandler? Idle;
        public static int IdleSubscriptions => Idle?.GetInvocationList().Length ?? 0;
        public static void RaiseIdle() => Idle?.Invoke(null, EventArgs.Empty);
        public static void ResetIdle() => Idle = null;
    }
}

namespace MahodAI.Civil3D.Plugin.CivilDelivery.Estimate
{
    // Host-service boundary only; the actual drawing-scoped ownership policy is linked below.
    // Match EstimateWorkflowService.DrawingIdentity's database filename / document name contract.
    public static class EstimateWorkflowService
    {
        public static string DrawingIdentity(Autodesk.AutoCAD.ApplicationServices.Document document) =>
            !string.IsNullOrWhiteSpace(document.Database.Filename) ? document.Database.Filename : document.Name;
    }
    public sealed record Snapshot(string? DrawingPath, string? DrawingHash, int? DbMod, string? Failure = null);
    public static class DrawingRevisionTracker
    {
        public static Snapshot Next = new(null, null, 1);
        public static int CaptureCalls;
        public static Snapshot CaptureLive(Autodesk.AutoCAD.ApplicationServices.Document document)
        { CaptureCalls++; return Next; }
    }
}

namespace MahodAI.Civil3D.Plugin.CivilDelivery.UI
{
    using Autodesk.AutoCAD.ApplicationServices;
    using System.Windows;
    public sealed class TestDispatcher
    {
        private readonly Queue<Action> _pending = new();
        public bool ThrowOnBegin;
        public void BeginInvoke(Action action)
        { if (ThrowOnBegin) throw new InvalidOperationException("dispatcher stopped"); _pending.Enqueue(action); }
        public int Count => _pending.Count;
        public void Invoke(Action action) => action();
        public void Flush()
        { for (int i = 0; _pending.Count > 0 && i < 100; i++) _pending.Dequeue()(); }
    }
    public sealed record TestRecord(string RecordId);
    public sealed record SectionRowViewModel(TestRecord Record);
    public sealed class TestGrid
    {
        public object? SelectedItem;
        public void ScrollIntoView(object value) { }
    }
    public sealed class TestTextBlock { public string Text { get; set; } = string.Empty; }
    public partial class CivilDeliveryControl
    {
        public Document? ActiveDocument;
        public readonly TestDispatcher Dispatcher = new();
        public readonly TestGrid SectionsGrid = new();
        public readonly TestTextBlock MeasurementDraftNotice = new();
        private readonly DrawingScopedNotice _exportNotice = new();
        private readonly List<SectionRowViewModel> _sectionRows = [];
        private object? _profile;
        private object? _plan;
        public string Status = "";
        public int Continuations, ProfileReloads, Plans, Refreshes, Errors;
        public bool ProfileCanLoad = true;
        public int DrawingChanges, LabelRefreshes;
        public string? Label;
        public object? ProfileReference => _profile;
        public object? PlanReference => _plan;
        public object? ScanReference = new object();
        public void SeedContext() { _profile = new object(); _plan = new object(); }
        public void Observe() => HookDrawingSaveContext();
        public void Activate(Document document) => OnObservedDocumentActivated(this, new(document));
        public void SeedNotice(string text, string? owner)
        { _exportNotice.Set(text, owner); MeasurementDraftNotice.Text = text; }
        public string NoticeText => _exportNotice.Text;
        public void Deactivate(Document document) => OnObservedDocumentDeactivating(this, new(document));
        public void Destroy(Document document) => OnObservedDocumentDestroying(this, new(document));
        private void ClearPreviewBeforeDocumentTransition(Document document) { }
        private void RefreshDrawingLabel() { LabelRefreshes++; Label = ActiveDocument?.Name; }
        // The actual OnDrawingChanged remains covered by Plugin source contracts;
        // here only its callback boundary and continuation composition are modeled.
        private void OnDrawingChanged()
        {
            DrawingChanges++; HookDrawingSaveContext();
            if (_pendingWorkflowSaveDocument != null && !ReferenceEquals(_pendingWorkflowSaveDocument, ActiveDocument))
                CancelWorkflowSave(_pendingWorkflowSaveDocument, "drawing switched");
            _plan = null; ScanReference = null; ReloadProfile(); RefreshDrawingLabel();
        }
        private Document? Doc() => ActiveDocument;
        private static string Ltr(string? value) => value ?? "";
        private void SetStatus(string value) => Status = value;
        private void RefreshGates() => Refreshes++;
        private void ShowError(string title, Exception error) => Errors++;
        private void ReloadProfile() { ProfileReloads++; _profile = ProfileCanLoad ? new object() : null; }
        private bool PlanIsStale() => false;
        private void OnPlan(object sender, RoutedEventArgs e) { Plans++; _plan = new object(); }
        public bool Start(bool requireProfile = false, bool replan = false) =>
            EnsureSavedForAction("בחירת פרופיל", (_, _) => Continuations++, replan, requireProfile);
        public string Token => _pendingWorkflowSaveToken ?? "";
        public bool IsPending => _pendingWorkflowSaveDocument != null;
        public void Stop() => CancelWorkflowSave(_pendingWorkflowSaveDocument, "cancelled by test");
    }
}
