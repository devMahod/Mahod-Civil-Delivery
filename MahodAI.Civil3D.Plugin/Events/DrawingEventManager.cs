using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using MahodAI.Civil3D.Plugin.WebSocket;

namespace MahodAI.Civil3D.Plugin.Events
{
    /// <summary>
    /// Central event orchestration for drawing change notifications.
    /// Singleton pattern - manages subscriptions across all open documents.
    /// </summary>
    public class DrawingEventManager : IDisposable
    {
        private static DrawingEventManager? _instance;
        private static readonly object _lock = new();

        private readonly Dictionary<string, DocumentEventSubscription> _subscriptions = new();
        private readonly EventDebouncer _debouncer;
        private MahodWebSocketClient? _webSocketClient;

        private bool _disposed;
        private bool _enabled = true;

        /// <summary>
        /// Gets the singleton instance.
        /// </summary>
        public static DrawingEventManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new DrawingEventManager();
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Event raised when a change batch is ready to be sent.
        /// </summary>
        public event EventHandler<ChangeBatch>? ChangeBatchReady;

        /// <summary>
        /// Event raised when a different document becomes active in AutoCAD.
        /// Provides the document name for tab switching.
        /// </summary>
        public event EventHandler<DocumentActivatedEventArgs>? DocumentActivated;

        /// <summary>
        /// Event raised when a document is about to be closed.
        /// </summary>
        public event EventHandler<DocumentActivatedEventArgs>? DocumentClosing;

        /// <summary>
        /// Whether event monitoring is enabled.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                if (!value)
                {
                    _debouncer.Clear();
                }
            }
        }

        private DrawingEventManager()
        {
            _debouncer = new EventDebouncer(OnBatchReady);
            SubscribeToDocumentManager();
        }

        /// <summary>
        /// Sets the WebSocket client for sending change notifications.
        /// </summary>
        public void SetWebSocketClient(MahodWebSocketClient client)
        {
            _webSocketClient = client;
        }

        /// <summary>
        /// Resolver for the session id that should receive drawing-event notifications.
        /// Typically returns the active tab's session id. If null, drawing events are
        /// dropped (no session to attribute them to).
        /// </summary>
        public Func<string?>? ActiveSessionIdResolver { get; set; }

        /// <summary>
        /// Initializes event monitoring for the current document.
        /// </summary>
        public void Initialize()
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                SubscribeToDocument(doc);
            }
        }

        private void SubscribeToDocumentManager()
        {
            try
            {
                var docMgr = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager;

                docMgr.DocumentActivated += DocMgr_DocumentActivated;
                docMgr.DocumentToBeDeactivated += DocMgr_DocumentToBeDeactivated;
                docMgr.DocumentToBeDestroyed += DocMgr_DocumentToBeDestroyed;
                docMgr.DocumentCreated += DocMgr_DocumentCreated;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SubscribeToDocumentManager error: {ex.Message}");
            }
        }

        private void DocMgr_DocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            SubscribeToDocument(e.Document);
        }

        private void DocMgr_DocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            // Flush pending changes from previous document
            _debouncer.Flush();

            // Subscribe to new document
            SubscribeToDocument(e.Document);

            // Notify listeners about document switch (for tab switching)
            try
            {
                var docName = e.Document?.Name ?? "unknown.dwg";
                var displayName = System.IO.Path.GetFileName(docName);
                DocumentActivated?.Invoke(this, new DocumentActivatedEventArgs(docName, displayName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DocumentActivated event error: {ex.Message}");
            }
        }

        private void DocMgr_DocumentToBeDeactivated(object sender, DocumentCollectionEventArgs e)
        {
            // Flush changes before switching
            _debouncer.Flush();
        }

        private void DocMgr_DocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            UnsubscribeFromDocument(e.Document);

            // Notify listeners about document closing (for tab removal)
            try
            {
                var docName = e.Document?.Name ?? "unknown.dwg";
                var displayName = System.IO.Path.GetFileName(docName);
                DocumentClosing?.Invoke(this, new DocumentActivatedEventArgs(docName, displayName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DocumentClosing event error: {ex.Message}");
            }
        }

        private void SubscribeToDocument(Document doc)
        {
            if (doc == null) return;

            var key = GetDocumentKey(doc);
            if (_subscriptions.ContainsKey(key)) return;

            try
            {
                var subscription = new DocumentEventSubscription(doc, OnChangeDetected);
                subscription.Subscribe();
                _subscriptions[key] = subscription;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SubscribeToDocument error: {ex.Message}");
            }
        }

        private void UnsubscribeFromDocument(Document doc)
        {
            if (doc == null) return;

            var key = GetDocumentKey(doc);
            if (_subscriptions.TryGetValue(key, out var subscription))
            {
                subscription.Dispose();
                _subscriptions.Remove(key);
            }
        }

        private string GetDocumentKey(Document doc)
        {
            return doc.Database.Filename ?? doc.Name ?? doc.GetHashCode().ToString();
        }

        private void OnChangeDetected(ChangeEvent change)
        {
            if (!_enabled || _disposed) return;

            _debouncer.AddChange(change);
        }

        private async void OnBatchReady(ChangeBatch batch)
        {
            if (_disposed || !batch.HasChanges) return;

            // Raise event for local handlers
            try
            {
                ChangeBatchReady?.Invoke(this, batch);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ChangeBatchReady handler error: {ex.Message}");
            }

            // Send to WebSocket if connected
            if (_webSocketClient?.IsConnected == true)
            {
                try
                {
                    var payload = new DrawingEventPayload
                    {
                        EventType = "objects_changed",
                        Changes = batch.Changes.Select(c => new ObjectChange
                        {
                            ObjectId = c.ObjectId,
                            ObjectType = c.Category,
                            ObjectName = c.ObjectName,
                            ChangeType = c.ChangeType.ToString(),
                            AffectedStations = c.AffectedStations != null
                                ? new WebSocket.StationRange { Start = c.AffectedStations.Start, End = c.AffectedStations.End }
                                : null,
                            AffectedProperties = c.ChangedProperties
                        }).ToList(),
                        Summary = new WebSocket.ChangeSummary
                        {
                            Added = batch.Summary.Added,
                            Modified = batch.Summary.Modified,
                            Deleted = batch.Summary.Deleted
                        }
                    };

                    var sessionId = ActiveSessionIdResolver?.Invoke();
                    await _webSocketClient.SendDrawingEventAsync(sessionId, payload);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"WebSocket send error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Forces immediate send of any pending changes.
        /// </summary>
        public void Flush()
        {
            _debouncer.Flush();
        }

        /// <summary>
        /// Clears pending changes without sending.
        /// </summary>
        public void Clear()
        {
            _debouncer.Clear();
        }

        /// <summary>
        /// Gets the count of pending changes.
        /// </summary>
        public int PendingCount => _debouncer.PendingCount;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debouncer.Dispose();

            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Dispose();
            }
            _subscriptions.Clear();

            try
            {
                var docMgr = Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager;
                docMgr.DocumentActivated -= DocMgr_DocumentActivated;
                docMgr.DocumentToBeDeactivated -= DocMgr_DocumentToBeDeactivated;
                docMgr.DocumentToBeDestroyed -= DocMgr_DocumentToBeDestroyed;
                docMgr.DocumentCreated -= DocMgr_DocumentCreated;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Dispose cleanup error: {ex.Message}"); }

            _instance = null;
        }
    }

    /// <summary>
    /// Event args for document activation (tab switching).
    /// </summary>
    public class DocumentActivatedEventArgs : EventArgs
    {
        public string DocumentName { get; }
        public string DisplayName { get; }

        public DocumentActivatedEventArgs(string documentName, string displayName)
        {
            DocumentName = documentName;
            DisplayName = displayName;
        }
    }
}
