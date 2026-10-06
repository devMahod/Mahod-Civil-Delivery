using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using MahodAI.Civil3D.Plugin.Models;
using MahodAI.Civil3D.Plugin.Services;
using MahodAI.Civil3D.Plugin.Services.Extraction;
using MahodAI.Civil3D.Plugin.Services.Extraction.Analyzers;
using MahodAI.Civil3D.Plugin.Services.Extraction.Models;
using MahodAI.Civil3D.Plugin.Tools;
using MahodAI.Civil3D.Plugin.ViewModels;
using MahodAI.Civil3D.Plugin.WebSocket;

using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

using WpfUserControl = System.Windows.Controls.UserControl;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfMessageBox = System.Windows.MessageBox;
using WpfCursors = System.Windows.Input.Cursors;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace MahodAI.Civil3D.Plugin
{
    public partial class NewChatControl : WpfUserControl
    {
        // === ViewModel + Services ===
        private readonly ChatViewModel _vm = new();
        private readonly FixWorkflowService _fixService;
        private readonly AgentCommunicationService _commService;

        // On-drawing problem overlay: clickable pins placed over the AutoCAD drawing for
        // each analysis finding. Populated when an analysis response with a findings table
        // arrives; cleared on the next turn (new message / new analysis / fix-then-anything).
        private readonly Services.Overlay.ProblemOverlayService _problemOverlay = new();
        private List<Models.ProblemMarker> _lastProblemMarkers = new();
        private string? _lastProblemDrawing;
        private string? _lastProblemSignature;

        // Markers are ephemeral: they reflect exactly ONE turn. Because UpdateBrowser does a
        // full NavigateToString reload on every message, the JS DOM scanner re-collects the
        // (still-present) findings table and would resurrect just-cleared pins. Two guards
        // stop that: (1) _suppressedProblemSignature — the signature we last cleared; an
        // incoming set that matches it is a stale re-collection and is ignored; (2) the Fix
        // phase, during which the marker set is owned by the fix flow (plan → result) and JS
        // reposts must not override the filtered/greened display.
        private string? _suppressedProblemSignature;
        private MarkerPhase _markerPhase = MarkerPhase.None;

        // Bumped whenever markers are cleared/hidden. A deferred (Background-priority) overlay
        // Show captures the generation it was queued under and no-ops if a clear superseded it,
        // so a just-cleared set can never repaint from an already-queued Show.
        private int _overlayGeneration;

        /// <summary>Which turn the current pins belong to. See <see cref="_markerPhase"/>.</summary>
        private enum MarkerPhase { None, Analysis, Fix }

        // === Per-message feedback (protocol v1.14) ===
        //
        // Reaction (👍/👎) and free text for each assistant answer. C# owns the
        // state because UpdateBrowser re-renders the whole page with
        // NavigateToString — anything held only in the DOM is lost on the next
        // message. The dictionary is serialized into the page as
        // window.MAHOD_FEEDBACK and the JS paints the buttons from it, so the
        // rendered state survives every reload and every tab switch.
        //
        // Keyed by a GUID stamped on the bubble (data-mahod-msg), which is
        // globally unique — one dictionary covers all tabs.
        private sealed class MessageFeedbackRecord
        {
            /// <summary>Supabase question_logs row for this answer (from stream_end), if any.</summary>
            public string? QuestionLogId { get; set; }

            /// <summary>1 = 👍, -1 = 👎, null = no reaction.</summary>
            public int? Rating { get; set; }

            /// <summary>Free text the engineer wrote, or null.</summary>
            public string? Feedback { get; set; }

            /// <summary>Request + answer text — sent only when there is no QuestionLogId to attach to.</summary>
            public string Question { get; set; } = string.Empty;
            public string Answer { get; set; } = string.Empty;
        }

        private readonly Dictionary<string, MessageFeedbackRecord> _messageFeedback =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Pre-click state for feedback whose save is still in flight, so a failed
        /// save can be visibly undone instead of leaving the UI claiming it stuck.
        /// </summary>
        private readonly Dictionary<string, MessageFeedbackRecord> _feedbackRollback =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Registers a rateable assistant message and returns the bubble attribute
        /// carrying its id. Bubbles WITHOUT this attribute (cards, approval
        /// prompts, fix results) get no feedback UI — see the JS in UpdateBrowser.
        /// </summary>
        private string RegisterFeedbackMessage(string? questionLogId, string question, string answer)
        {
            string id = "m" + Guid.NewGuid().ToString("N");
            string? logId = string.IsNullOrWhiteSpace(questionLogId) ? null : questionLogId;
            _messageFeedback[id] = new MessageFeedbackRecord
            {
                QuestionLogId = logId,
                // The text is ONLY sent when there is no row to attach to, so
                // keeping a copy of every answered message would be pure memory
                // cost in a long session.
                Question = logId == null ? question ?? string.Empty : string.Empty,
                Answer = logId == null ? answer ?? string.Empty : string.Empty,
            };
            return id;
        }

        /// <summary>
        /// The <c>{id: {rating, feedback}}</c> map injected into the page as
        /// <c>window.MAHOD_FEEDBACK</c>, limited to messages the given HTML shows.
        /// </summary>
        private string BuildFeedbackStateJson(string conversationHtml)
        {
            var visible = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kvp in _messageFeedback)
            {
                if (kvp.Value.Rating == null && string.IsNullOrEmpty(kvp.Value.Feedback))
                    continue;   // nothing to paint
                if (conversationHtml.IndexOf(kvp.Key, StringComparison.Ordinal) < 0)
                    continue;   // not on this page (another tab)
                visible[kvp.Key] = new { rating = kvp.Value.Rating, feedback = kvp.Value.Feedback };
            }
            try
            {
                return JsonSerializer.Serialize(visible);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Feedback] state serialize failed: {ex.Message}");
                return "{}";
            }
        }

        // Bot avatar — loaded once as a base64 data URI, embedded in chat HTML.
        // Emoji fallback used if the PNG is missing at load time.
        private string _botAvatarDataUri = "";
        private const string BotAvatarEmojiFallback = "🤖";

        // Thinking-indicator videos — loaded once as base64 data URIs. Inline
        // via data URI so the virtual-host-mapping race doesn't strand the
        // video as a broken placeholder the first time a stream starts.
        private string _thinkingShortDataUri = "";
        private BitmapImage? _headerLogoLight;
        private BitmapImage? _headerLogoDark;

        public NewChatControl()
        {
            InitializeComponent();

            _fixService = new FixWorkflowService(_vm);
            _commService = new AgentCommunicationService(_vm);

            LoadHeaderLogo();
            LoadBotAvatar();

            // Register CommandManager handlers for Paste —
            // This is the CORRECT way to intercept Ctrl+V in AutoCAD WPF palettes.
            System.Windows.Input.CommandManager.AddPreviewExecutedHandler(
                InputTextBox, OnPasteExecuted);
            System.Windows.Input.CommandManager.AddPreviewCanExecuteHandler(
                InputTextBox, OnPasteCanExecute);

            Loaded += async (_, __) =>
            {
                try
                {
                    await EnsureWebViewReadyAsync();
                    await EnsureAssistantReadyAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Loaded event error: {ex}");
                    UpdateConnectionStatus(false);
                    AddSystemMessage($"שגיאה באתחול: {ex.Message}");
                }
            };

            ShowWelcomeOnce();
        }

        #region Init / Logo / WebView2

        private void LoadHeaderLogo()
        {
            // Full "מהוד הנדסה · עוזר AI" banner shown in the header. Two variants:
            // the original (dark-grey text — light theme) and a dark-theme variant
            // whose neutral ink is brightened to near-white so it stays legible on
            // the dark header (the green mark keeps its brand color in both).
            // ApplyTheme() swaps HeaderLogo.Source between them.
            _headerLogoLight = LoadEmbeddedBitmap(
                "MahodAI.Civil3D.Plugin.assets.mahod_logo_full.png")
                ?? LoadEmbeddedBitmap("MahodAI.Civil3D.Plugin.assets.mahod_mark.png")
                ?? LoadEmbeddedBitmap("MahodAI.Civil3D.Plugin.assets.bot_avatar.png");
            _headerLogoDark = LoadEmbeddedBitmap(
                "MahodAI.Civil3D.Plugin.assets.mahod_logo_full_dark.png")
                ?? _headerLogoLight;

            if (_headerLogoLight == null)
            {
                System.Diagnostics.Debug.WriteLine("HeaderLogo: embedded logo assets not found");
                return;
            }
            HeaderLogo.Source = _vm.IsDarkMode ? _headerLogoDark : _headerLogoLight;
            System.Diagnostics.Debug.WriteLine("HeaderLogo: loaded from embedded resources");
        }

        private static BitmapImage? LoadEmbeddedBitmap(string resourceName)
        {
            try
            {
                using var rs = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resourceName);
                if (rs == null)
                    return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                // DO NOT set BitmapCreateOptions.IgnoreImageCache here: in the WPF
                // imaging stack it makes EndInit() throw "Key cannot be null" for these
                // embedded PNGs (verified for both mahod_logo_full and mahod_mark).
                // CacheOption.OnLoad already loads eagerly from the stream, so the
                // cache flag was never needed.
                bmp.StreamSource = rs;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadEmbeddedBitmap('{resourceName}') error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Locate the "assets/" folder next to the DLL (falls back to base/current dir).
        /// Returned path is what SetVirtualHostNameToFolderMapping will serve.
        /// </summary>
        private static string FindAssetsDirectory()
        {
            string[] candidates = new[]
            {
                Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "", "assets"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets"),
                Path.Combine(Environment.CurrentDirectory, "assets"),
            };
            foreach (var p in candidates)
            {
                if (Directory.Exists(p))
                    return p;
            }
            return string.Empty;
        }

        /// <summary>
        /// Load the bot avatar PNG and encode it as a base64 data URI. Embedded
        /// directly in chat HTML — sidesteps WebView2 virtual-host timing and
        /// cache issues. The PNG is ~20KB so the overhead is modest even when
        /// replicated per chat bubble.
        /// </summary>
        private void LoadBotAvatar()
        {
            // Assets are embedded into the DLL (see .csproj EmbeddedResource
            // entries) so we read them via Assembly.GetManifestResourceStream
            // rather than from disk. This avoids a class of failures where
            // Assembly.Location / AppDomain.BaseDirectory don't resolve to the
            // bundle's assets folder at runtime (shadow copy, NETLOAD from
            // memory, roaming vs machine-wide plugin paths, etc.).
            _botAvatarDataUri = LoadEmbeddedAsDataUri(
                "MahodAI.Civil3D.Plugin.assets.bot_avatar.png", "image/png");
            // Transparent animated WebP (replaced the old white-background MP4 —
            // H.264 has no alpha channel, so the white square glowed in dark mode).
            _thinkingShortDataUri = LoadEmbeddedAsDataUri(
                "MahodAI.Civil3D.Plugin.assets.Thinking.webp", "image/webp");
        }

        private static string LoadEmbeddedAsDataUri(string resourceName, string mimeType)
        {
            try
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[EmbeddedAsset] '{resourceName}' not found");
                    return "";
                }
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                byte[] bytes = ms.ToArray();
                System.Diagnostics.Debug.WriteLine($"[EmbeddedAsset] loaded '{resourceName}' ({bytes.Length} bytes)");
                return $"data:{mimeType};base64," + Convert.ToBase64String(bytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EmbeddedAsset] load failed for '{resourceName}': {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// HTML fragment for the bot avatar at the given pixel size. Uses the
        /// short URL served by WebView2's virtual host mapping (see WebView2 init).
        /// Falls back to the 🤖 emoji if the PNG isn't found at load time.
        /// </summary>
        private string BotAvatarHtml(int size = 32)
        {
            if (string.IsNullOrEmpty(_botAvatarDataUri))
                return BotAvatarEmojiFallback;
            return $"<img src=\"{_botAvatarDataUri}\" alt=\"MahodAI\" " +
                   $"style=\"width:{size}px;height:{size}px;object-fit:contain;display:block;\" />";
        }

        private async Task EnsureWebViewReadyAsync()
        {
            if (_vm.IsWebViewInitialized)
                return;

            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MahodAI_Civil3D_WebView2");

                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                await ChatBrowser.EnsureCoreWebView2Async(env);

                // Map the bundled "assets/" folder to https://mahod-assets/ so HTML can
                // reference images via short URLs instead of inlining base64 (which blows
                // past NavigateToString's ~2 MB limit once replicated per chat bubble).
                try
                {
                    string assetsDir = FindAssetsDirectory();
                    if (!string.IsNullOrEmpty(assetsDir))
                    {
                        ChatBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "mahod-assets",
                            assetsDir,
                            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                        System.Diagnostics.Debug.WriteLine($"[WebView2] Virtual host 'mahod-assets' → {assetsDir}");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[WebView2] assets/ folder not found — bot avatar URL will 404");
                    }

                    // v1.16: attached-image thumbnails are served from disk rather
                    // than inlined as data: URIs, which would blow the ~2MB
                    // NavigateToString ceiling the whole transcript shares.
                    try
                    {
                        Utilities.ImageAttachmentHelper.PrunePreviews();
                        ChatBrowser.CoreWebView2.SetVirtualHostNameToFolderMapping(
                            "mahod-attachments",
                            Utilities.ImageAttachmentHelper.PreviewFolder,
                            Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[WebView2] attachment host mapping failed: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[WebView2] SetVirtualHostNameToFolderMapping failed: {ex.Message}");
                }

                await ChatBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
document.addEventListener('dragover', e => { e.preventDefault(); e.stopPropagation(); }, true);
document.addEventListener('drop', e => { e.preventDefault(); e.stopPropagation(); }, true);
");

                await ChatBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  var _userScrolledUp = false;
  function isNearBottom() {
    return (document.body.scrollHeight - window.innerHeight - window.scrollY) < 150;
  }
  function smartScroll() {
    if (!_userScrolledUp) window.scrollTo(0, document.body.scrollHeight);
  }
  window.addEventListener('scroll', function() {
    _userScrolledUp = !isNearBottom();
  }, { passive: true });
  window.smartScroll = smartScroll;
  window.addEventListener('load', function(){ setTimeout(smartScroll, 50); });
  var obs = new MutationObserver(function(){ setTimeout(smartScroll, 10); });
  document.addEventListener('DOMContentLoaded', function(){
    var root = document.getElementById('messages') || document.body;
    obs.observe(root, {childList:true, subtree:true});
    smartScroll();
  });
})();
");

                // Typewriter + thinking-steps engine. Registered as a document-created
                // script so it is (re)defined on every page load — a NavigateToString
                // reload starts clean and any timer from the previous document dies with
                // that document's JS context. See StartAssistantStreamingMessage / TwPush /
                // TwFinish / TwStop / AppendThinkingStep on the C# side.
                await ChatBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  var TW_TICK = 30;      // ms between reveals
  var TW_CPS  = 40;      // baseline characters per second (deliberate, readable typing pace)
  var TW_MAX_MS = 9000;  // hard cap: even long answers fully reveal within this budget
  window.__tw = { target:'', shown:0, timer:null, startedAt:0, baseShown:0, pendingHtml:null, swapped:false, structured:false, doneLen:-1, doneHtml:'', msgId:null };

  function twEscape(s){
    return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/\n/g,'<br>');
  }
  // ── Progressive markdown styling ───────────────────────────────────────────
  // Completed lines (everything before the LAST newline of the revealed text) are
  // block-rendered to styled HTML the instant they finish; the line still being
  // typed stays raw, so styling appears row-by-row instead of all-at-once at the
  // end. Tables are NOT styled mid-stream (they read as broken until complete) —
  // those keep the raw reveal and swap to full HTML once (twDoSwap).
  function twEsc(s){ return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;'); }
  function twInline(s){
    // s MUST already be HTML-escaped. Bold runs before italic so a '*' inside a
    // '**' pair is never mistaken for an italic delimiter.
    s = s.replace(/\*\*([^*]+?)\*\*/g, '<strong>$1</strong>');
    s = s.replace(/__([^_]+?)__/g, '<strong>$1</strong>');
    s = s.replace(/`([^`]+?)`/g, '<code>$1</code>');
    s = s.replace(/(^|[^*\w])\*([^*\s][^*]*?)\*(?!\w)/g, '$1<em>$2</em>');
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g,
      '<a href=""$2"" target=""_blank"" style=""color:#2e9535;text-decoration:underline;"">$1</a>');
    return s;
  }
  // A line that is nothing but a fold tag. The agent wraps heavy detail in
  // <details>/<summary> (design.py _fold), and escaping those mid-stream is what
  // put literal '<details>' text in the bubble during a road draw.
  function twIsFoldTag(line){ return /^\s*<\/?(?:details|summary)[^>]*>\s*$/i.test(line); }
  function twMd(src){
    var lines = src.split('\n');
    var out = [], para = [], listType = null;
    function flushPara(){ if(para.length){ out.push('<p>'+para.join('<br/>')+'</p>'); para=[]; } }
    function closeList(){ if(listType){ out.push(listType==='ul'?'</ul>':'</ol>'); listType=null; } }
    for(var i=0;i<lines.length;i++){
      // Emit fold tags as real HTML instead of escaped text.
      if(twIsFoldTag(lines[i])){ flushPara(); closeList(); out.push(lines[i].trim()); continue; }
      // A <summary> written inline with its text: keep the tags, style the text.
      var sum = lines[i].match(/^\s*<summary[^>]*>(.*)<\/summary>\s*$/i);
      if(sum){ flushPara(); closeList(); out.push('<summary>'+twInline(twEsc(sum[1]))+'</summary>'); continue; }
      var esc = twEsc(lines[i]);
      var trimmed = esc.replace(/^\s+/,'').replace(/\s+$/,'');
      if(trimmed===''){ flushPara(); closeList(); continue; }
      var h = trimmed.match(/^(#{1,4})\s+(.*)$/);
      if(h){ flushPara(); closeList(); var lvl=h[1].length+1; out.push('<h'+lvl+'>'+twInline(h[2])+'</h'+lvl+'>'); continue; }
      if(/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)){ flushPara(); closeList(); out.push('<hr/>'); continue; }
      var ul = trimmed.match(/^[-*]\s+(.*)$/);
      if(ul){ flushPara(); if(listType!=='ul'){ closeList(); out.push('<ul>'); listType='ul'; } out.push('<li>'+twInline(ul[1])+'</li>'); continue; }
      var ol = trimmed.match(/^\d+\.\s+(.*)$/);
      if(ol){ flushPara(); if(listType!=='ol'){ closeList(); out.push('<ol>'); listType='ol'; } out.push('<li>'+twInline(ol[1])+'</li>'); continue; }
      closeList(); para.push(twInline(esc.replace(/\s+$/,'')));
    }
    flushPara(); closeList();
    return out.join('');
  }
  // Markdown link TARGETS are revealed atomically instead of being typed out.
  // A SharePoint citation URL is 300+ characters of percent-encoding; with four
  // of them an answer spent most of its reveal budget 'typing' text the reader
  // never sees, which is what made long answers crawl. Returns the position the
  // reveal should actually use.
  function twSkipLinkUrl(s, pos){
    var open = s.lastIndexOf('](', pos);
    if(open === -1 || pos <= open + 1) return pos;   // not inside a link target
    var close = s.indexOf(')', open + 2);
    if(close === -1) return open + 1;                // URL still streaming — wait at the boundary
    return pos > close ? pos : close + 1;            // inside it — jump the whole URL
  }
  function twRender(el){
    var tw = window.__tw;
    var revealed = tw.target.slice(0, tw.shown);
    if(tw.structured){
      // A structured answer (table / fold) must NEVER be dumped as raw source —
      // that is what showed '<details><summary>' and pipe-tables as literal text
      // while the answer was still generating. Style the completed lines and
      // drop the partial tail; the full HTML arrives at the final swap.
      var cut = revealed.lastIndexOf('\n');
      el.innerHTML = '<div class=""tw-md"">' + twMd(cut === -1 ? '' : revealed.slice(0, cut)) + '</div>';
      return;
    }
    // The line still being typed: hide markers that would otherwise flash as
    // literal text ('## כותרת', a half-written fold tag) before the line ends.
    function twTail(s){
      if(twIsFoldTag(s) || /^\s*<\/?(?:details|summary)/i.test(s)) return '';
      s = s.replace(/^\s*#{1,4}\s+/, '');
      // A half-written source citation must not flash its raw URL. twInline only
      // linkifies a COMPLETE [label](url), so until the ')' arrives the whole
      // percent-encoded SharePoint address (300+ chars) was rendered as literal
      // text, one character at a time. Show the label alone until it closes.
      s = s.replace(/\[([^\]]*)\]\([^)]*$/, '$1');
      s = s.replace(/\[([^\]]*)$/, '$1');
      return twInline(twEsc(s));
    }
    var nl = revealed.lastIndexOf('\n');
    if(nl === -1){
      el.innerHTML = '<span class=""tw-raw"">'+twTail(revealed)+'</span>';
      return;
    }
    var done = revealed.slice(0, nl);
    var tail = revealed.slice(nl+1);
    if(done.length !== tw.doneLen){ tw.doneHtml = twMd(done); tw.doneLen = done.length; } // recompute only when a new line completed
    el.innerHTML = '<div class=""tw-md"">'+tw.doneHtml+'</div>'+
                   '<span class=""tw-raw"">'+twTail(tail)+'</span>';
  }
  function twLastContent(){
    var b = document.getElementsByClassName('assistant-stream-content');
    return (b && b.length) ? b[b.length-1] : null;
  }
  function twLastText(){ var c = twLastContent(); return c ? c.querySelector('.tw-text') : null; }
  function twEnsureTimer(){
    if(window.__tw.timer) return;
    window.__tw.startedAt = Date.now();
    window.__tw.baseShown = window.__tw.shown;
    window.__tw.timer = setInterval(twStep, TW_TICK);
  }
  function twStep(){
    var tw = window.__tw;
    var len = tw.target.length;
    var elapsed = Date.now() - tw.startedAt;
    // Reveal at TW_CPS, but accelerate only as much as needed to honor TW_MAX_MS.
    var rate = Math.max(TW_CPS, Math.ceil(len / (TW_MAX_MS/1000)));
    var want = tw.baseShown + Math.floor(elapsed * rate / 1000);
    var newShown = Math.min(want, len);   // bulletproof clamp — never exceed target
    // Never sit inside a link URL. 'want' stays driven by elapsed time, so
    // holding at the '](' boundary costs nothing: the moment the ')' streams in,
    // want has already advanced past it and the whole URL reveals at once.
    newShown = Math.min(twSkipLinkUrl(tw.target, newShown), len);
    if(newShown < tw.shown) newShown = tw.shown;   // reveal only ever moves forward
    if(newShown !== tw.shown){
      tw.shown = newShown;
      var el = twLastText();
      if(el) twRender(el);
      if(window.smartScroll) window.smartScroll();
    }
    if(tw.shown >= len){
      // Caught up: perform the one-and-only finalize swap if one is pending.
      if(tw.pendingHtml !== null && !tw.swapped) twDoSwap(tw.pendingHtml);
      clearInterval(tw.timer); tw.timer = null;
    }
  }
  function twDoSwap(finalHtml, msgId){
    var tw = window.__tw;
    if(tw.swapped) return;            // idempotent
    tw.swapped = true;
    var c = twLastContent();
    if(c){                            // no-op if the page was reloaded/replaced
      c.innerHTML = finalHtml;
      c.classList.remove('assistant-stream-content');
      // v1.14: mark the just-finished answer as rateable so setupCopyButtons
      // adds its feedback row now, not only after the next full page rebuild.
      var id = msgId || tw.msgId;
      if(id){
        var bubble = c.closest('.assistant-bubble');
        if(bubble) bubble.setAttribute('data-mahod-msg', id);
      }
    }
    if(window.setupCopyButtons) window.setupCopyButtons();
    if(window.smartScroll) window.smartScroll();
  }
  window.twReset = function(){
    if(window.__tw.timer){ clearInterval(window.__tw.timer); }
    window.__tw = { target:'', shown:0, timer:null, startedAt:0, baseShown:0, pendingHtml:null, swapped:false, structured:false, doneLen:-1, doneHtml:'', msgId:null };
  };
  window.twPush = function(fullText){
    var tw = window.__tw;
    if(tw.swapped) return;            // already finalized; ignore late tokens
    // Structured reports (markdown/HTML tables) look like gibberish when their raw
    // source is revealed char-by-char. Detect them, skip the typewriter (keep the
    // thinking indicator up), and let twFinish swap in the rendered HTML directly.
    if(!tw.structured && /\|\s*:?-{2,}|<table|<details/i.test(fullText)){
      tw.structured = true;
      // Freeze whatever was already typed as RENDERED markdown and stop the raw
      // reveal. Long road-design runs stream one short status line per stage; the
      // moment a table/fold arrives, continuing to reveal raw source would dump
      // '<details><summary>' and pipe-tables into the bubble as literal text.
      // From here the live feedback is the thinking-steps log until the final swap.
      var frozen = twLastText();
      if(frozen) frozen.innerHTML = '<div class=""tw-md"">'+twMd(tw.target.slice(0, tw.shown))+'</div>';
      if(tw.timer){ clearInterval(tw.timer); tw.timer = null; }
    }
    if(tw.structured){ tw.target = fullText; return; }
    if(fullText === tw.target){ twEnsureTimer(); return; }
    var c = twLastContent();
    var ind = c ? c.querySelector('.thinking-indicator') : null;
    if(ind && fullText.length > 0) ind.style.display = 'none';   // hide the video once text starts
    tw.target = fullText;
    if(tw.shown > tw.target.length) tw.shown = tw.target.length; // clamp on divergent re-send
    tw.startedAt = Date.now(); tw.baseShown = tw.shown;          // re-base rate after growth
    twEnsureTimer();
  };
  window.twFinish = function(finalHtml, msgId){
    var tw = window.__tw;
    tw.pendingHtml = finalHtml;
    if(msgId) tw.msgId = msgId;      // twStep's deferred swap needs it too
    if(tw.swapped) return;
    var structured = tw.structured || /<table|<details/i.test(finalHtml);
    if(structured || tw.shown >= tw.target.length){   // report, or typewriter caught up
      if(tw.timer){ clearInterval(tw.timer); tw.timer = null; }
      twDoSwap(finalHtml, msgId);
    } else {
      twEnsureTimer();                           // keep typing; twStep swaps at the end
    }
  };
  window.twStop = function(){
    if(window.__tw.timer){ clearInterval(window.__tw.timer); window.__tw.timer = null; }
  };
  window.twAddStep = function(text){
    var c = twLastContent(); if(!c) return;
    var box = c.querySelector('.thinking-steps'); if(!box) return;
    var clean = text.replace(/[.\s…]+$/, '');   // strip trailing dots/ellipsis/space
    if(!clean) return;
    var last = box.lastElementChild;
    if(last && last.getAttribute('data-step') === clean) return;   // skip consecutive duplicate
    // Freeze the previous active step: its bouncing dots become static.
    if(last){
      var prev = last.querySelector('.tw-dots');
      if(prev){ prev.className = ''; prev.textContent = '...'; }
    }
    var line = document.createElement('div');
    line.className = 'step-line';
    line.setAttribute('data-step', clean);
    line.textContent = clean + ' ';
    // Only the newest (active) step shows the animated bouncing dots.
    var dots = document.createElement('span');
    dots.className = 'tw-dots';
    dots.innerHTML = '<span class=""td d1"">.</span><span class=""td d2"">.</span><span class=""td d3"">.</span>';
    line.appendChild(dots);
    box.appendChild(line);
    box.scrollTop = box.scrollHeight;   // keep the newest step in view (scroll the log itself)
    if(window.smartScroll) window.smartScroll();
  };
})();
");

                // Interactive-pick step line (2026-08-04). The instruction for the
                // CURRENT step is patched into the live assistant message and
                // replaced in place as the flow advances — a bubble per step left
                // every finished instruction on screen still reading as an action.
                // Registered on document-created like the typewriter helpers so it
                // survives UpdateBrowser's NavigateToString reloads.
                await ChatBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  function stepHost(){
    var nodes = document.querySelectorAll('.assistant-stream-content, .assistant-message .bubble-content');
    return nodes.length ? nodes[nodes.length - 1] : null;
  }
  window.mahodSetStepHint = function(html){
    var target = stepHost(); if(!target) return false;
    var line = target.querySelector('.mahod-step-hint');
    if(!line){
      line = document.createElement('div');
      line.className = 'mahod-step-hint';
      target.appendChild(line);
    }
    line.innerHTML = html;
    if(window.smartScroll) window.smartScroll();
    return true;
  };
  window.mahodClearStepHint = function(){
    var lines = document.querySelectorAll('.mahod-step-hint');
    for(var i = 0; i < lines.length; i++) lines[i].remove();
    return true;
  };
})();
");

                // Clickable analysis-report locations: a finding's location cell (alignment +
                // station range) posts a zoom_to_location message so the host focuses the
                // AutoCAD viewport on that stretch of the alignment. See ChatHtmlRenderer
                // (mahod-loc-link spans) and the WebMessageReceived 'zoom_to_location' case.
                await ChatBrowser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
(function(){
  window.mahodZoomTo = function(el){
    try{
      if(!el || !window.chrome || !window.chrome.webview) return;
      var align = el.getAttribute('data-align') || '';
      if(!align) return;
      var s1 = el.getAttribute('data-s1');
      var s2 = el.getAttribute('data-s2');
      var prob = el.getAttribute('data-prob') || '';
      var msg = { action: 'zoom_to_location', alignment: align };
      if(s1 !== null && s1 !== '') { var v1 = parseFloat(s1); if(!isNaN(v1)) msg.station_start = v1; }
      if(s2 !== null && s2 !== '') { var v2 = parseFloat(s2); if(!isNaN(v2)) msg.station_end = v2; }
      if(prob) msg.problem = prob;   // disambiguates several violations on one curve
      if(msg.station_start === undefined) return;
      window.chrome.webview.postMessage(JSON.stringify(msg));
    }catch(e){}
  };

  // Safety net: linkify location cells the C# renderer didn't wrap (e.g. a table
  // emitted by the server as raw HTML rather than markdown). Idempotent — cells
  // already carrying a .mahod-loc-link are skipped, so it never double-wraps.
  var LOC_RE = /ציר\s+(.+?)\s*[—–-]?\s*תחנה\s+(\d[\d.,+]*)(?:\s*(?:עד|to|[—–-])\s*(\d[\d.,+]*))?/;
  function parseStation(s){
    if(!s) return null;
    var v = parseFloat(String(s).replace(/\+/g,'').replace(/,/g,''));
    return isNaN(v) ? null : v;
  }
  function esc(s){ return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;'); }
  window.mahodLinkifyLocations = function(){
    try{
      var cells = document.querySelectorAll('table td');
      for(var i=0;i<cells.length;i++){
        var td = cells[i];
        if(td.querySelector && td.querySelector('.mahod-loc-link')) continue;
        var text = td.textContent || '';
        var m = LOC_RE.exec(text);
        if(!m) continue;
        var s1 = parseStation(m[2]);
        if(s1 === null) continue;
        var s2 = parseStation(m[3]);
        td.innerHTML = '<span class=""mahod-loc-link"" data-align=""'+esc(m[1].trim())+
          '"" data-s1=""'+s1+'"" data-s2=""'+(s2===null?'':s2)+
          '"" onclick=""window.mahodZoomTo && window.mahodZoomTo(this)"" '+
          'title=""לחץ למיקוד המקטע בשרטוט"" '+
          'style=""cursor:pointer; color:#2e9535; text-decoration:underline;"">📍 '+esc(text)+'</span>';
      }
    }catch(e){}
  };
  // Collect findings from the RENDERED table DOM (format-agnostic: works whether the
  // analysis table arrived as markdown or as server HTML) and post them to the host so
  // it can drop clickable problem pins on the drawing. Deduped by signature so it posts
  // only when the set of findings actually changes.
  function colIndexes(table){
    var headRow = table.querySelector('thead tr') || table.querySelector('tr');
    if(!headRow) return null;
    var ths = headRow.children, idx = {loc:-1,prob:-1,val:-1,req:-1,src:-1};
    for(var i=0;i<ths.length;i++){
      var t = ths[i].textContent || '';
      if(idx.loc<0 && t.indexOf('מיקום')>=0) idx.loc=i;
      if(idx.prob<0 && t.indexOf('בעיה')>=0) idx.prob=i;
      if(idx.val<0 && t.indexOf('בפועל')>=0) idx.val=i;
      if(idx.req<0 && t.indexOf('נדרש')>=0) idx.req=i;
      if(idx.src<0 && t.indexOf('מקור')>=0) idx.src=i;
    }
    return idx;
  }
  function cellTxt(cells, i){ return (i>=0 && i<cells.length) ? String(cells[i].textContent||'').replace(/\s+/g,' ').trim() : ''; }
  var _lastSig = null;
  window.mahodCollectProblems = function(){
    try{
      var tables = document.querySelectorAll('table'), markers = [];
      // An analysis renders ONE findings table per entity (alignment/profile/…), all inside a
      // single assistant message. Collect from EVERY findings table in the MOST-RECENT analysis
      // message (so every entity's pins show) but skip older analyses still scrolled up in the
      // transcript. Find the last findings table, take its owning assistant message, then
      // collect from all findings tables within that same message.
      var lastTbl = null;
      for(var ti=tables.length-1; ti>=0; ti--){
        var ci = colIndexes(tables[ti]);
        if(ci && ci.loc>=0 && ci.prob>=0){ lastTbl = tables[ti]; break; }
      }
      var scope = (lastTbl && lastTbl.closest) ? lastTbl.closest('.assistant-message') : null;
      for(var tj=0; tj<tables.length && lastTbl; tj++){
        var table = tables[tj], idx = colIndexes(table);
        if(!idx || idx.loc<0 || idx.prob<0) continue;
        // Restrict to the latest analysis: same message bubble (fallback: the single last
        // table when the DOM has no .assistant-message ancestry).
        if(scope){ if(!table.closest || table.closest('.assistant-message') !== scope) continue; }
        else if(table !== lastTbl) continue;
        var rows = table.querySelectorAll('tbody tr');
        if(!rows.length) rows = table.querySelectorAll('tr');
        for(var ri=0; ri<rows.length; ri++){
          var cells = rows[ri].children;
          if(!cells || cells.length <= idx.loc) continue;
          var locCell = cells[idx.loc];
          if(locCell.tagName === 'TH') continue;
          var link = locCell.querySelector ? locCell.querySelector('.mahod-loc-link') : null;
          // Read the location from the LINK when it exists, so the row number this
          // function injects below never leaks into the marker text (it would change
          // the signature on every re-collection and show up in the pin card).
          var locText = String((link ? link.textContent : locCell.textContent)||'')
            .replace(/^\s*\d+\.\s*/,'').replace(/^\s*📍\s*/,'').replace(/\s+/g,' ').trim();
          var align=null, s1=null, s2=null;
          if(link){
            align = link.getAttribute('data-align');
            var a1 = link.getAttribute('data-s1'); if(a1) s1 = parseFloat(a1);
            var a2 = link.getAttribute('data-s2'); if(a2) s2 = parseFloat(a2);
          }
          if(align===null || s1===null || isNaN(s1)){
            var m = LOC_RE.exec(locText);
            if(!m) continue;
            align = m[1].trim(); s1 = parseStation(m[2]); s2 = parseStation(m[3]);
          }
          if(align===null || s1===null || isNaN(s1)) continue;
          var mk = { alignment: align, station_start: s1, location: locText, problem: cellTxt(cells, idx.prob) };
          // Stamp the row's problem onto its location link so a click can jump to the EXACT
          // pin when one curve carries several violations (see mahodZoomTo / FocusMarker).
          if(link && mk.problem){ try{ link.setAttribute('data-prob', mk.problem); }catch(e){} }
          if(s2!==null && !isNaN(s2)) mk.station_end = s2;
          if(idx.val>=0) mk.value = cellTxt(cells, idx.val);
          if(idx.req>=0) mk.required = cellTxt(cells, idx.req);
          if(idx.src>=0) mk.source = cellTxt(cells, idx.src);
          markers.push(mk);
          // The drawing pins are numbered 1..N in exactly this order, but the table
          // showed no number — so a pin could not be matched to its row. Put the same
          // number in bold at the start of the location cell. Idempotent: this runs
          // again on every DOM mutation.
          try{
            if(locCell.querySelector && !locCell.querySelector('.mahod-vio-num')){
              var numEl = document.createElement('b');
              numEl.className = 'mahod-vio-num';
              numEl.textContent = markers.length + '.';
              numEl.style.cssText = 'margin-left:4px; color:#475569;';
              locCell.insertBefore(numEl, locCell.firstChild);
            }
          }catch(e){}
        }
      }
      var sig = JSON.stringify(markers);
      if(sig === _lastSig) return;
      _lastSig = sig;
      if(markers.length && window.chrome && window.chrome.webview){
        window.chrome.webview.postMessage(JSON.stringify({action:'problem_markers', markers:markers}));
      }
    }catch(e){}
  };
  var _pending = null;
  function schedule(){ if(_pending) return; _pending = setTimeout(function(){ _pending=null; window.mahodLinkifyLocations(); window.mahodCollectProblems(); if(window.setupCopyButtons) window.setupCopyButtons(); }, 40); }
  document.addEventListener('DOMContentLoaded', function(){
    schedule();
    try{
      var root = document.getElementById('messages') || document.body;
      new MutationObserver(schedule).observe(root, {childList:true, subtree:true});
    }catch(e){}
  });
  window.addEventListener('load', schedule);
})();
");

                // Open external links in default browser
                ChatBrowser.CoreWebView2.NavigationStarting += (s, args) =>
                {
                    if (args.IsUserInitiated &&
                        Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == "http" || uri.Scheme == "https"))
                    {
                        args.Cancel = true;
                        Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                    }
                };

                ChatBrowser.CoreWebView2.NewWindowRequested += (s, args) =>
                {
                    args.Handled = true;
                    Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true });
                };

                // Handle messages from JavaScript
                ChatBrowser.CoreWebView2.WebMessageReceived += async (s, args) =>
                {
                    try
                    {
                        string message = args.TryGetWebMessageAsString();
                        if (string.IsNullOrEmpty(message))
                            return;

                        if (message.TrimStart().StartsWith("{"))
                        {
                            try
                            {
                                using var jsonDoc = JsonDocument.Parse(message);
                                // Clone detaches the element from jsonDoc: the awaited
                                // Dispatcher.InvokeAsync(async …) below completes at the inner
                                // lambda's FIRST await, after which the using disposes jsonDoc —
                                // an async handler (zoom_to_location, fix_approval) that reads
                                // root after its own await would hit ObjectDisposedException.
                                var root = jsonDoc.RootElement.Clone();
                                // Handle "type" messages (e.g., undo_all from fix result card)
                                if (root.TryGetProperty("type", out var typeProp))
                                {
                                    var msgType = typeProp.GetString() ?? "";
                                    if (msgType == "undo_all")
                                    {
                                        await Dispatcher.InvokeAsync(() =>
                                        {
                                            try
                                            {
                                                var doc = Autodesk.AutoCAD.ApplicationServices.Application
                                                    .DocumentManager.MdiActiveDocument;
                                                if (doc != null)
                                                {
                                                    // Use ONLY the server-reported applied_count
                                                    // (items with status=='applied' — drawing actually
                                                    // mutated). Counting Success rows over-counts
                                                    // soft-skips, and a guessed/defaulted count undoes
                                                    // unrelated user operations. When missing (older
                                                    // agents) the result card renders the undo button
                                                    // disabled; this is defense-in-depth.
                                                    int? appliedCount = _vm.LastFixResult?.AppliedCount;
                                                    if (appliedCount == null)
                                                    {
                                                        AddAssistantMessage(
                                                            "לא ניתן לבטל אוטומטית — מספר השינויים שבוצעו לא דווח על ידי השרת. ניתן לבטל ידנית בפקודת UNDO.");
                                                        return;
                                                    }
                                                    if (appliedCount.Value < 1)
                                                    {
                                                        AddAssistantMessage("לא בוצעו שינויים בציור — אין מה לבטל.");
                                                        return;
                                                    }

                                                    doc.SendStringToExecute($"_UNDO {appliedCount.Value}\n", true, false, true);
                                                    AddAssistantMessage($"בוטלו {appliedCount.Value} שינויים בהצלחה.");
                                                }
                                            }
                                            catch (Exception undoEx)
                                            {
                                                System.Diagnostics.Debug.WriteLine($"Undo failed: {undoEx.Message}");
                                                AddAssistantMessage($"שגיאה בביטול: {undoEx.Message}");
                                            }
                                        });
                                        return;
                                    }
                                }

                                if (root.TryGetProperty("action", out var actionProp))
                                {
                                    var action = actionProp.GetString() ?? "";
                                    await Dispatcher.InvokeAsync(async () =>
                                    {
                                        switch (action)
                                        {
                                            case "download":
                                                var dlContent = root.TryGetProperty("content", out var cp) ? cp.GetString() ?? "" : "";
                                                await HandleDownloadAsync(dlContent);
                                                break;
                                            case "share":
                                                var shareContent = root.TryGetProperty("content", out var sp) ? sp.GetString() ?? "" : "";
                                                HandleShare(shareContent);
                                                break;
                                            case "fix_approval":
                                                await HandleFixApprovalAction(root);
                                                break;
                                            case "scope_selection":
                                                HandleScopeSelectionAction(root);
                                                break;
                                            case "switch_tab":
                                                var tabKey = root.TryGetProperty("key", out var tkp) ? tkp.GetString() ?? "" : "";
                                                if (!string.IsNullOrEmpty(tabKey) && _vm.DrawingTabs.TryGetValue(tabKey, out var newTab))
                                                {
                                                    _vm.SaveCurrentTabState();
                                                    _ = CaptureLivePendingEditsAsync(_vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey ?? "", out var curTab) ? curTab : null);
                                                    _vm.RestoreTabState(newTab);
                                                    RefreshApplyFixesButton();
                                                    if (_vm.AssistantInitialized) EnableInput(!newTab.IsSending);
                                                    ShowStopButton(newTab.IsSending);
                                                    UpdateBrowser();
                                                }
                                                break;
                                            case "close_tab":
                                                var closeKey = root.TryGetProperty("key", out var ckp) ? ckp.GetString() ?? "" : "";
                                                if (!string.IsNullOrEmpty(closeKey))
                                                {
                                                    CloseDrawingTab(closeKey);
                                                }
                                                break;
                                            case "new_tab":
                                                {
                                                    var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                                                    string? activeDrawingPath = doc?.Name;
                                                    _ = CaptureLivePendingEditsAsync(_vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey ?? "", out var prevTab) ? prevTab : null);
                                                    _vm.CreateTab(activeDrawingPath);
                                                    RefreshApplyFixesButton();
                                                    UpdateBrowser();
                                                }
                                                break;
                                            case "send_chat":
                                                var chatText = root.TryGetProperty("text", out var ctp) ? ctp.GetString() ?? "" : "";
                                                if (!string.IsNullOrEmpty(chatText))
                                                {
                                                    InputTextBox.Text = chatText;
                                                    _ = SendMessageAsync();
                                                }
                                                break;
                                            case "zoom_to_location":
                                                await HandleZoomToLocationAsync(root);
                                                break;
                                            case "problem_markers":
                                                HandleProblemMarkers(root);
                                                break;
                                            case "message_feedback":
                                                await HandleMessageFeedbackAsync(root);
                                                break;
                                        }
                                    });
                                    return;
                                }
                            }
                            catch (JsonException)
                            {
                                // Not valid JSON, treat as suggestion
                            }
                        }

                        // Existing suggestion handling (plain text)
                        Dispatcher.Invoke(() =>
                        {
                            InputTextBox.Text = message;
                            InputTextBox.Focus();
                            InputTextBox.CaretIndex = InputTextBox.Text.Length;
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"WebMessageReceived error: {ex.Message}");
                    }
                };

                // Set initial background color to prevent flash
                ChatBrowser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 232, 238, 243);

                // Always start with one active tab so the strip is visible from first paint.
                EnsureActiveTab();

                _vm.IsWebViewInitialized = true;
                UpdateBrowser();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("WebView2 init failed: " + ex);
            }
        }

        private async Task EnsureAssistantReadyAsync()
        {
            // Initialize extraction services first (works offline)
            _vm.InitializeExtractionServices();

            if (_vm.AssistantInitialized)
                return;

            try
            {
                await _commService.InitializeAsync();

                // Initialize WebSocket client for streaming
                if (_vm.UseWebSocket)
                {
                    var baseUrl = AgentCommunicationService.GetBaseUrl();
                    var apiKey = AgentCommunicationService.GetApiKey();
                    await InitializeWebSocketAsync(baseUrl, apiKey);
                }

                EnableInput(true);
                UpdateConnectionStatus(true);

                // Enable the analyze button now that connection is confirmed
                Dispatcher.Invoke(() =>
                {
                    if (AnalyzeDrawingButton != null)
                    {
                        AnalyzeDrawingLabel.Text = "קריאת שרטוט";
                        AnalyzeDrawingButton.IsEnabled = true;
                    }
                });

                // Subscribe to document events for auto-switching/closing tabs
                try
                {
                    Events.DrawingEventManager.Instance.DocumentActivated += OnDocumentActivated;
                    Events.DrawingEventManager.Instance.DocumentClosing += OnDocumentClosing;
                    Events.DrawingEventManager.Instance.ChangeBatchReady += OnDrawingChangeBatchReady;
                    // Drawing-changed events are scoped to the active tab's session
                    Events.DrawingEventManager.Instance.ActiveSessionIdResolver = () => _vm.ActiveTab?.SessionId;
                }
                catch (Exception evtEx)
                {
                    System.Diagnostics.Debug.WriteLine($"DrawingEventManager subscribe error: {evtEx.Message}");
                }
            }
            catch (Exception ex)
            {
                _vm.AssistantInitialized = false;
                foreach (var t in _vm.DrawingTabs.Values) t.AgentSessionActive = false;
                EnableInput(false);
                UpdateConnectionStatus(false);

                // Show error on the analyze button itself
                Dispatcher.Invoke(() =>
                {
                    if (AnalyzeDrawingButton != null)
                    {
                        AnalyzeDrawingLabel.Text = "לא מחובר";
                        AnalyzeDrawingButton.IsEnabled = false;
                    }
                });

                // Name the SOURCE of the address, not just the address. A stale
                // machine-wide MAHOD_AGENT_API_URL from an old setup-server.bat
                // silently outranks the URL compiled into the build, and from
                // inside Civil 3D the two are indistinguishable — every
                // occurrence so far cost a support round-trip to discover.
                var agentUrl = AgentCommunicationService.GetBaseUrl();
                var source = Config.PluginConstants.AgentApiUrlIsOverridden
                    ? $"הכתובת מגיעה ממשתנה הסביבה {Config.PluginConstants.AgentApiUrlVariable} — " +
                      "הוא גובר על הכתובת המובנית בגרסה. אם זו אינה הכתובת הנכונה, יש להסירו " +
                      "ולהפעיל מחדש את Civil 3D."
                    : "הכתובת מובנית בגרסה המותקנת.";

                AddSystemMessage(
                    "לא ניתן להתחבר לשרת MahodAI Agent.\n" +
                    $"כתובת השרת: {agentUrl}\n" +
                    source + "\n" +
                    "ניתן לעבוד במצב אופליין עם ניתוח DWG/SHP ותיקונים.\n" +
                    $"שגיאה: {ex.Message}"
                );

                System.Diagnostics.Debug.WriteLine("Agent init failed: " + ex);
            }
        }

        private async Task InitializeWebSocketAsync(string baseUrl, string apiKey)
        {
            try
            {
                // Check if already connected
                if (_vm.WsClient != null && _vm.WsClient.IsConnected)
                {
                    System.Diagnostics.Debug.WriteLine("WebSocket already connected, skipping initialization");
                    return;
                }

                // Dispose old client if exists
                if (_vm.WsClient != null)
                {
                    try
                    {
                        UnsubscribeWebSocketEvents();
                        await _vm.WsClient.DisconnectAsync();
                        _vm.WsClient.Dispose();
                    }
                    catch { /* Ignore cleanup errors */ }
                    _vm.WsClient = null;
                }

                _vm.WsClient = _commService.CreateWebSocketClient(baseUrl, apiKey);

                // Wire up event handlers (UI-specific, must stay in code-behind)
                SubscribeWebSocketEvents();

                await _vm.WsClient.ConnectAsync();

                System.Diagnostics.Debug.WriteLine("WebSocket client initialized and connected");

                // Auto-create session so chat is available before analysis
                _ = Task.Run(async () => await InitializeQuickSessionAsync());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebSocket initialization failed: {ex.Message}");
                _vm.UseWebSocket = false;
                _vm.WsClient = null;
            }
        }

        private void SubscribeWebSocketEvents()
        {
            if (_vm.WsClient == null) return;
            _vm.WsClient.ConnectionStateChanged += OnConnectionStateChanged;
            _vm.WsClient.StreamStarted += OnStreamStarted;
            _vm.WsClient.StreamTokenReceived += OnStreamTokenReceived;
            _vm.WsClient.StreamEnded += OnStreamEnded;
            _vm.WsClient.StatusReceived += OnStatusReceived;
            _vm.WsClient.ToolCallReceived += OnToolCallReceived;

            // Interactive-tool chat hints + render flush ("hint one step late" fix):
            // hints append an assistant bubble from inside a running tool; the flush
            // is an ordering barrier the ToolExecutor awaits BEFORE starting a modal
            // prompt — dispatcher drain at Background priority (runs after all queued
            // render work) followed by a browser round-trip (WebView2 executes
            // scripts in order, so every queued DOM append has committed).
            Tools.ToolUiNotifier.PostChatHint = text =>
                Dispatcher.BeginInvoke(new Action(() => AddAssistantMessage(text)));
            // Per-step pick instructions patch ONE line inside the live message
            // (2026-08-04) instead of adding a bubble per step — completed steps
            // used to stay on screen still reading like something to do.
            Tools.ToolUiNotifier.SetStepHint = text =>
                Dispatcher.BeginInvoke(new Action(() => SetStepHint(text)));
            Tools.ToolUiNotifier.ClearStepHint = () =>
                Dispatcher.BeginInvoke(new Action(ClearStepHint));
            Tools.ToolUiNotifier.FlushChat = async () =>
            {
                await Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                try
                {
                    var roundTrip = await Dispatcher.InvokeAsync(
                        () => ChatBrowser.ExecuteScriptAsync("void 0"));
                    await roundTrip;
                }
                catch
                {
                    // Browser not ready yet — nothing rendered, nothing to flush.
                }
            };
            _vm.WsClient.ErrorOccurred += OnWebSocketError;
            _vm.WsClient.FixPlanReceived += OnFixPlanReceived;
            _vm.WsClient.FixResultReceived += OnFixResultReceived;
            _vm.WsClient.FixVerifyResultReceived += OnFixVerifyResultReceived;
            _vm.WsClient.PostFixValidationReceived += OnPostFixValidationReceived;
            _vm.WsClient.CheckStartReceived += OnCheckStartReceived;
            _vm.WsClient.AnalysisCompleteReceived += OnAnalysisCompleteReceived;
            _vm.WsClient.FeedbackAckReceived += OnFeedbackAckReceived;
        }

        private void UnsubscribeWebSocketEvents()
        {
            if (_vm.WsClient == null) return;
            _vm.WsClient.ConnectionStateChanged -= OnConnectionStateChanged;
            _vm.WsClient.StreamStarted -= OnStreamStarted;
            _vm.WsClient.StreamTokenReceived -= OnStreamTokenReceived;
            _vm.WsClient.StreamEnded -= OnStreamEnded;
            _vm.WsClient.StatusReceived -= OnStatusReceived;
            _vm.WsClient.ToolCallReceived -= OnToolCallReceived;
            _vm.WsClient.ErrorOccurred -= OnWebSocketError;
            _vm.WsClient.FixPlanReceived -= OnFixPlanReceived;
            _vm.WsClient.FixResultReceived -= OnFixResultReceived;
            _vm.WsClient.FixVerifyResultReceived -= OnFixVerifyResultReceived;
            _vm.WsClient.PostFixValidationReceived -= OnPostFixValidationReceived;
            _vm.WsClient.CheckStartReceived -= OnCheckStartReceived;
            _vm.WsClient.AnalysisCompleteReceived -= OnAnalysisCompleteReceived;
            _vm.WsClient.FeedbackAckReceived -= OnFeedbackAckReceived;
        }

        #region WebSocket Event Handlers

        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Match MahodWebSocketClient.IsConnected: Authenticated OR SessionActive = connected.
                // "Connected" is a transient state (TCP connected, not yet authenticated).
                bool connected = e.NewState == WebSocketConnectionState.Connected ||
                                 e.NewState == WebSocketConnectionState.Authenticated ||
                                 e.NewState == WebSocketConnectionState.SessionActive;
                UpdateConnectionStatus(connected);
                System.Diagnostics.Debug.WriteLine($"WebSocket state changed: {e.OldState} -> {e.NewState} (UI connected={connected})");
            });
        }

        private void OnStreamStarted(object? sender, StreamStartedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string streamId = e.StreamStart.StreamId;
                // Route by session_id from the envelope. The agent stamps every
                // outbound stream/token/end message with the originating session,
                // so we can find the owning tab without any FIFO claim.
                var owner = _vm.FindTabBySessionId(e.SessionId);
                if (owner == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Stream started without owner: stream={streamId} session={e.SessionId ?? "(null)"}");
                    return;
                }

                // Stop was pressed before the agent assigned a stream id. Now that
                // we know it, cancel the stream and drop it — don't set up the UI.
                if (owner.CancelRequested)
                {
                    if (_vm.WsClient != null && _vm.WsClient.IsConnected)
                        _ = _vm.WsClient.CancelStreamAsync(e.SessionId, streamId);
                    System.Diagnostics.Debug.WriteLine(
                        $"Stream started for a cancelled request — cancelling stream {streamId}");
                    return;
                }

                owner.CurrentStreamId = streamId;
                owner.CurrentStreamContent.Clear();
                System.Diagnostics.Debug.WriteLine(
                    $"Stream started: {streamId} (owner tab: {owner.DisplayName}, session: {e.SessionId})");
            });
        }

        private void OnStreamTokenReceived(object? sender, StreamTokenReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var owner = _vm.FindTabBySessionId(e.SessionId);
                if (owner == null) return;
                if (owner.CancelRequested) return;   // user stopped — ignore late tokens
                bool isActive = _vm.ActiveTabKey == owner.TabId;

                // Accumulate full-text-so-far (handles both incremental tokens and
                // FullText snapshots), then feed the typewriter. The typewriter reveals
                // the raw text gradually — no more mid-stream markdown re-renders, which
                // (together with the no-reload finish in OnStreamEnded) removes the
                // "answer pops in all at once" effect regardless of backend granularity.
                if (!string.IsNullOrEmpty(e.Token.FullText))
                {
                    owner.CurrentStreamContent.Clear();
                    owner.CurrentStreamContent.Append(e.Token.FullText);
                }
                else
                {
                    owner.CurrentStreamContent.Append(e.Token.Token);
                }

                if (isActive)
                    TwPush(owner.CurrentStreamContent.ToString());
                // Background tabs only accumulate; they finalize on stream end.
            });
        }

        private void OnStreamEnded(object? sender, StreamEndedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string streamId = e.StreamEnd.StreamId;
                var owner = _vm.FindTabBySessionId(e.SessionId);
                if (owner == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Stream ended for unknown session: stream={streamId} session={e.SessionId ?? "(null)"}");
                    return;
                }

                // The stream we already cancelled has now wound down on the agent.
                // Just clear state — the UI was finalized when Stop was pressed.
                if (owner.CancelRequested)
                {
                    owner.CurrentStreamId = null;
                    owner.CurrentStreamContent.Clear();
                    owner.IsSending = false;
                    owner.CurrentLoaderId = null;
                    owner.CancelRequested = false;
                    return;
                }

                bool isActive = _vm.ActiveTabKey == owner.TabId;

                var finalContent = !string.IsNullOrEmpty(e.StreamEnd.FinalText)
                    ? e.StreamEnd.FinalText!
                    : owner.CurrentStreamContent.ToString();

                // v1.14: this answer becomes rateable. The agent's question_logs
                // row id (when it managed to stamp one) binds the feedback to the
                // same analytics row the web chat rates; without it the question +
                // answer text travel with the feedback and the agent creates the row.
                string feedbackMsgId = RegisterFeedbackMessage(
                    e.StreamEnd.QuestionLogId,
                    owner.LastUserMessage ?? string.Empty,
                    finalContent);

                if (isActive)
                {
                    // Render the final markdown once. Keep the persistence buffer and the
                    // live DOM in sync WITHOUT a page reload: write the canonical block to
                    // the buffer, and hand the same inner HTML to the typewriter, which
                    // swaps the typed raw text for it only after the reveal catches up.
                    string finalInnerHtml = BuildFinalAssistantInnerHtml(finalContent, e.StreamEnd.References, e.StreamEnd.LispRecommendations);
                    string finalBlockHtml = BuildFinalAssistantBlockHtml(finalContent, e.StreamEnd.References, e.StreamEnd.LispRecommendations, feedbackMsgId);

                    ReplaceLastAssistantStreamBlockNoReload(finalBlockHtml);

                    // Guarantee the typewriter has a target even if the whole answer
                    // arrived only at stream_end (final_text, no tokens), then finalize.
                    if (!string.IsNullOrEmpty(finalContent))
                        TwPush(finalContent);
                    TwFinish(finalInnerHtml, feedbackMsgId);

                    if (_vm.AssistantInitialized)
                        EnableInput(true);
                    RefreshApplyFixesButton();

                    // If this response carried an analysis findings table, drop clickable
                    // problem pins onto the drawing. Non-analysis replies parse to zero
                    // findings and leave any existing pins untouched.
                    // A fresh analysis must re-pin even when it repeats a just-cleared drawing
                    // (identical signature). The detail tables are HTML the markdown parser
                    // below can't see, so lift suppression here and let the JS DOM-scan
                    // repopulate. Gated on the response actually being a findings report so an
                    // ordinary chat reply never un-suppresses a stale table.
                    if (LooksLikeFindingsReport(finalContent))
                        _suppressedProblemSignature = null;
                    TryShowProblemOverlay(finalContent);
                }
                else
                {
                    // Background tab: bake the finalized HTML into the tab's buffer so
                    // switching back will show the completed response.
                    string finalizedHtml = BuildFinalAssistantBlockHtml(finalContent, e.StreamEnd.References, e.StreamEnd.LispRecommendations, feedbackMsgId);
                    string current = owner.ConversationHtml.ToString();
                    string replaced = ChatHtmlRenderer.ReplaceLastStreamBlock(current, finalizedHtml);
                    owner.ConversationHtml.Clear();
                    owner.ConversationHtml.Append(replaced);
                }

                owner.CurrentStreamId = null;
                owner.CurrentStreamContent.Clear();
                owner.IsSending = false;
                owner.CurrentLoaderId = null;

                System.Diagnostics.Debug.WriteLine(
                    $"Stream ended: {streamId} owner={owner.DisplayName} active={isActive} tokens={e.StreamEnd.TotalTokens}");
            });
        }

        /// <summary>
        /// The engineer reacted to (or wrote feedback about) one assistant answer
        /// in the WebView (protocol v1.14). The JS has already painted the new
        /// state optimistically; we record it, forward it to the agent, and let
        /// <see cref="OnFeedbackAckReceived"/> confirm or roll it back.
        /// </summary>
        private async Task HandleMessageFeedbackAsync(JsonElement root)
        {
            string messageId = root.TryGetProperty("message_id", out var midProp)
                ? midProp.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(messageId) ||
                !_messageFeedback.TryGetValue(messageId, out var record))
            {
                System.Diagnostics.Debug.WriteLine($"[Feedback] unknown message id '{messageId}' — ignored");
                return;
            }

            int? rating = null;
            if (root.TryGetProperty("rating", out var ratingProp) &&
                ratingProp.ValueKind == JsonValueKind.Number &&
                ratingProp.TryGetInt32(out var r) && (r == 1 || r == -1))
            {
                rating = r;
            }

            string? feedback = root.TryGetProperty("feedback", out var fbProp)
                ? fbProp.GetString() : null;
            if (string.IsNullOrWhiteSpace(feedback)) feedback = null;

            // Snapshot for rollback — the UI already shows the new state, so a
            // failed save has to be visibly undone rather than silently kept.
            _feedbackRollback[messageId] = new MessageFeedbackRecord
            {
                QuestionLogId = record.QuestionLogId,
                Rating = record.Rating,
                Feedback = record.Feedback,
                Question = record.Question,
                Answer = record.Answer,
            };
            record.Rating = rating;
            record.Feedback = feedback;

            var sessionId = _vm.ActiveTab?.SessionId;
            if (_vm.WsClient == null || string.IsNullOrEmpty(sessionId))
            {
                await SettleFeedbackAsync(messageId, ok: false);
                return;
            }

            await _vm.WsClient.SendMessageFeedbackAsync(sessionId, new MessageFeedbackPayload
            {
                MessageId = messageId,
                QuestionLogId = record.QuestionLogId,
                Rating = rating,
                Feedback = feedback,
                // Only used when the answer has no question_logs row yet
                // (analyze reports, design steps) — the agent creates one.
                Question = record.QuestionLogId == null ? record.Question : null,
                Answer = record.QuestionLogId == null ? record.Answer : null,
            });
        }

        private void OnFeedbackAckReceived(object? sender, FeedbackAckReceivedEventArgs e)
        {
            // InvokeAsync (not Invoke) with an async body: Invoke would make this
            // async void, and an exception on the continuation would come back on
            // the AutoCAD UI thread with nowhere to be caught — i.e. take Civil 3D
            // down over a feedback badge. The try/catch is the same guard.
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    string messageId = e.Ack.MessageId ?? "";
                    if (!string.IsNullOrEmpty(messageId) &&
                        e.Ack.Success &&
                        !string.IsNullOrEmpty(e.Ack.QuestionLogId) &&
                        _messageFeedback.TryGetValue(messageId, out var record))
                    {
                        // The agent may have created the analytics row on our behalf —
                        // remember it so the NEXT edit targets the same row.
                        record.QuestionLogId = e.Ack.QuestionLogId;
                    }

                    if (!e.Ack.Success)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Feedback] save failed: message={messageId} error={e.Ack.Error ?? "(none)"}");
                    }

                    await SettleFeedbackAsync(messageId, e.Ack.Success);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Feedback] ack handling failed: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// Confirms the optimistic UI, or restores the pre-click state when the
        /// agent could not save. Either way the WebView is told, so the engineer
        /// never sees feedback presented as saved when it wasn't.
        /// </summary>
        private async Task SettleFeedbackAsync(string messageId, bool ok)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            if (!ok && _feedbackRollback.TryGetValue(messageId, out var previous) &&
                _messageFeedback.TryGetValue(messageId, out var current))
            {
                current.Rating = previous.Rating;
                current.Feedback = previous.Feedback;
            }
            _feedbackRollback.Remove(messageId);

            _messageFeedback.TryGetValue(messageId, out var settled);
            string stateJson = JsonSerializer.Serialize(new
            {
                rating = settled?.Rating,
                feedback = settled?.Feedback,
            });

            try
            {
                if (ChatBrowser.CoreWebView2 != null)
                {
                    string js = "window.mahodFeedbackAck && window.mahodFeedbackAck(" +
                                $"{JsonSerializer.Serialize(messageId)}, {(ok ? "true" : "false")}, {stateJson});";
                    await ChatBrowser.ExecuteScriptAsync(js);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Feedback] ack script failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Build the final assistant message block HTML used when a background tab's
        /// stream ends — mirrors ReplaceStreamingBlockWithPlainText's body so we can
        /// finalize tab buffers without touching the live DOM.
        /// </summary>
        /// <summary>
        /// Inner HTML of a finalized assistant message (rendered markdown + approval
        /// buttons + sources). Shared verbatim by the live typewriter swap (TwFinish)
        /// and the persisted block, so the on-screen DOM and _vm.ConversationHtml stay
        /// byte-identical in content.
        /// </summary>
        private string BuildFinalAssistantInnerHtml(string content, List<RagReference>? references, List<LispRecommendation>? lispRecommendations)
        {
            string displayContent = HideInternalMarkers(content);
            string body = ChatHtmlRenderer.ConvertMarkdownToHtml(displayContent);
            string sourcesHtml = ChatHtmlRenderer.BuildReferencesHtml(references);
            string approvalButtons = BuildApprovalButtonsIfNeeded(content);
            return $"{body}{approvalButtons}{sourcesHtml}";
        }

        private string BuildFinalAssistantBlockHtml(
            string content,
            List<RagReference>? references,
            List<LispRecommendation>? lispRecommendations,
            string? feedbackMessageId = null)
        {
            string ts = DateTime.Now.ToString("HH:mm");
            string inner = BuildFinalAssistantInnerHtml(content, references, lispRecommendations);
            string msgAttr = string.IsNullOrEmpty(feedbackMessageId)
                ? ""
                : $" data-mahod-msg='{feedbackMessageId}'";
            return $@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'{msgAttr}>
    <div class='bubble-content response-content' dir='auto'>{inner}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>";
        }

        private void OnStatusReceived(object? sender, StatusReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                string displayMessage = e.Status.Status switch
                {
                    StatusTypes.UsingTool when !string.IsNullOrEmpty(e.Status.ToolName)
                        => $"משתמש: {e.Status.Message}",
                    _ => e.Status.Message
                };

                // Show the live thinking process for the active tab's stream. A status
                // without a session id (older agent that doesn't stamp it) is treated as
                // belonging to the active tab — only that tab has a live steps box anyway.
                if (string.IsNullOrEmpty(e.SessionId) || _vm.ActiveTab?.SessionId == e.SessionId)
                    AppendThinkingStep(displayMessage);
                System.Diagnostics.Debug.WriteLine($"Status: {e.Status.Status} - {e.Status.Message} (session={e.SessionId ?? "(null)"})");
            });
        }

        private void OnCheckStartReceived(object? sender, CheckStartReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var cs = e.CheckStart;
                if (string.IsNullOrEmpty(e.SessionId) || _vm.ActiveTab?.SessionId == e.SessionId)
                    AppendThinkingStep($"בודק {cs.EntityName} ({cs.ItemIndex}/{cs.TotalItems})...");
                System.Diagnostics.Debug.WriteLine($"Check start: {cs.EntityName} ({cs.ItemIndex}/{cs.TotalItems})");
            });
        }

        private void OnAnalysisCompleteReceived(object? sender, AnalysisCompleteReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var ac = e.AnalysisComplete;

                // Record on the ORIGINATING tab (not the visible one) that this
                // drawing now has an analysis — the fix flow gates on it.
                var owner = string.IsNullOrEmpty(e.SessionId)
                    ? _vm.ActiveTab
                    : _vm.FindTabBySessionId(e.SessionId!) ?? _vm.ActiveTab;
                if (owner != null)
                    owner.HasCompletedAnalysis = true;

                if (string.IsNullOrEmpty(e.SessionId) || _vm.ActiveTab?.SessionId == e.SessionId)
                    AppendThinkingStep($"ניתוח הושלם — {ac.TotalChecks} בדיקות, {ac.TotalFindings} ממצאים");
                System.Diagnostics.Debug.WriteLine($"Analysis complete: {ac.TotalChecks} checks, {ac.TotalFindings} findings");
            });
        }

        private async void OnToolCallReceived(object? sender, ToolCallReceivedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"Tool call received: {e.ToolCall.ToolName} (ID: {e.ToolCall.ToolCallId}, session: {e.SessionId ?? "(null)"})");

            if (_vm.ToolExecutor == null || _vm.WsClient == null)
            {
                System.Diagnostics.Debug.WriteLine("Tool call skipped: toolExecutor or wsClient is null");
                return;
            }

            // The tool result must be tagged with the originating session id so the
            // agent routes it back to the right pipeline run, regardless of which
            // tab is currently visible. We honor the envelope session_id verbatim;
            // if it is missing (older agents) we fall back to the active tab.
            string? sessionId = e.SessionId ?? _vm.ActiveTab?.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                System.Diagnostics.Debug.WriteLine("Tool call skipped: no session_id available");
                return;
            }

            try
            {
                Dispatcher.Invoke(() =>
                {
                    // Only show the step if this tool belongs to the active tab.
                    if (_vm.ActiveTab?.SessionId == sessionId)
                        AppendThinkingStep($"מריץ כלי: {e.ToolCall.ToolName}...");
                });

                System.Diagnostics.Debug.WriteLine($"Executing tool: {e.ToolCall.ToolName} for session {sessionId}");

                // Execute with session→document affinity (the executor activates
                // the session's bound drawing, or fails with a Hebrew
                // drawing_unavailable error when it is closed) and send the FULL
                // result payload — including the P0-01 outcome contract fields
                // that the old success-only send silently dropped.
                var payload = await _vm.ToolExecutor.ExecuteToolCallAsync(
                    e.ToolCall,
                    sessionId,
                    CancellationToken.None);

                System.Diagnostics.Debug.WriteLine(
                    $"Tool executed: {e.ToolCall.ToolName} - Success: {payload.Success}, Outcome: {payload.Outcome}");
                if (payload.Error != null)
                    System.Diagnostics.Debug.WriteLine($"Tool error: {payload.Error.Code} - {payload.Error.Message}");

                System.Diagnostics.Debug.WriteLine($"Sending tool result for: {e.ToolCall.ToolCallId}");
                await _vm.WsClient.SendToolResultAsync(sessionId!, payload);

                System.Diagnostics.Debug.WriteLine($"Tool result sent successfully: {e.ToolCall.ToolName}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Tool execution error: {ex.Message}\n{ex.StackTrace}");

                try
                {
                    var errorObj = new ToolError
                    {
                        Code = "EXECUTION_ERROR",
                        Message = ex.Message
                    };
                    await _vm.WsClient.SendToolResultAsync(
                        sessionId!,
                        e.ToolCall.ToolCallId,
                        false,
                        null,
                        errorObj);
                    System.Diagnostics.Debug.WriteLine("Error result sent to agent");
                }
                catch (Exception sendEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to send error result: {sendEx.Message}");
                }
            }
        }

        private void OnWebSocketError(object? sender, WebSocketErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                System.Diagnostics.Debug.WriteLine($"WebSocket error: {e.Code} - {e.Message}");

                // Clear all in-flight stream state across all tabs — the connection died.
                bool activeWasSending = _vm.ActiveTab?.IsSending == true;
                foreach (var tab in _vm.DrawingTabs.Values)
                {
                    if (!tab.IsSending) continue;
                    tab.IsSending = false;
                    tab.CurrentStreamId = null;
                    tab.CurrentStreamContent.Clear();
                }

                if (activeWasSending)
                {
                    ReplaceStreamingBlockWithPlainText($"שגיאה: {e.Message}");
                    if (_vm.AssistantInitialized)
                        EnableInput(true);
                }
            });
        }

        #endregion

        #region Fix Workflow Events

        private void OnFixPlanReceived(object? sender, FixPlanReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Fix-plan card HTML is built by FixPlanHtmlRenderer, which HTML-encodes
                // every model-supplied free-text field (plan.Summary, item.Description, …).
                // That encoding turns raw "**bold**" the agent put in those fields into
                // literal asterisks in the card. Run the inline-only markdown pass over
                // the rendered HTML so ** / _ / ` always render in those summaries.
                string fixHtml = ChatHtmlRenderer.ApplyInlineMarkdown(
                    _fixService.ProcessFixPlanReceived(e.Plan));

                // Append directly so embedded <script> survives
                string ts = DateTime.Now.ToString("HH:mm");
                _vm.ConversationHtml.Append($@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{fixHtml}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");
                UpdateBrowser();

                // Narrow the on-drawing pins to only the findings this plan can fix. Set
                // AFTER UpdateBrowser so the reload's async DOM re-scan (which reposts the
                // full analysis set) is ignored by the Fix-phase guard in ApplyProblemMarkers.
                EnterFixPhaseForPlan(e.Plan);
            });
        }

        private void OnFixResultReceived(object? sender, FixResultReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Same reasoning as OnFixPlanReceived — verify.SummaryText, item.Description
                // and ErrorMessage all flow through FixPlanHtmlRenderer's HtmlEncode, which
                // leaves raw inline markers as literal asterisks. The inline pass converts
                // them without touching the card's structural HTML.
                string html = ChatHtmlRenderer.ApplyInlineMarkdown(
                    _fixService.ProcessFixResultReceived(e.Result));
                ReplaceOrAppendFixCard(e.Result.PlanId ?? "", html);
                SetApplyFixesBusy(false);

                // Turn each applied fix's pin green; leave failed ones their severity color.
                ApplyFixResultToMarkers(e.Result);
            });
        }

        private void OnFixVerifyResultReceived(object? sender, FixVerifyResultReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var mergedHtml = _fixService.ProcessFixVerifyResultReceived(e.Payload);
                    ReplaceOrAppendFixCard(e.Payload.PlanId ?? "", mergedHtml);

                    // Verification can demote an "applied" pin: the fix ran but the read-back
                    // value is wrong. Recolor those pins red so the drawing agrees with the
                    // card's "בוצע — האימות נכשל" rows and the summary's separate count.
                    ApplyFixVerifyToMarkers(e.Payload);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] OnFixVerifyResultReceived failed: {ex.Message}");
                }
            });
        }

        private void OnPostFixValidationReceived(object? sender, PostFixValidationReceivedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    var mergedHtml = _fixService.ProcessPostFixValidationReceived(e.Payload);
                    ReplaceOrAppendFixCard(e.Payload.PlanId ?? "", mergedHtml);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] OnPostFixValidationReceived failed: {ex.Message}");
                }
            });
        }

        #endregion

        private void UpdateLoaderText(string text)
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;

            string safe = System.Net.WebUtility.HtmlEncode(text);
            string script = $@"
(function(){{
  var indicators = document.getElementsByClassName('typing-indicator');
  if(!indicators || indicators.length === 0) return;
  var indicator = indicators[indicators.length - 1];
  if(!indicator) return;
  var statusText = indicator.querySelector('.status-text');
  if(statusText) {{
    statusText.textContent = '{safe}';
  }} else {{
    var dot = document.createElement('span');
    dot.className = 'pulse-dot';
    var textSpan = document.createElement('span');
    textSpan.className = 'status-text';
    textSpan.textContent = '{safe}';
    indicator.innerHTML = '';
    indicator.appendChild(dot);
    indicator.appendChild(textSpan);
  }}
  window.scrollTo(0, document.body.scrollHeight);
}})();";

            _ = ChatBrowser.ExecuteScriptAsync(script);
        }

        private void RemoveTypingIndicator()
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;

            string script = @"
(function(){
  var blocks = document.getElementsByClassName('assistant-stream-content');
  if(!blocks || blocks.length === 0) return;
  var target = blocks[blocks.length - 1];
  var typing = target.querySelector('.typing-indicator');
  if(typing) typing.remove();
})();";

            _ = ChatBrowser.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// Appends one line to the live "thinking process" log in the active stream
        /// block. No-ops if there is no thinking block on screen. Consecutive
        /// duplicate lines are skipped (handled JS-side).
        /// </summary>
        private void AppendThinkingStep(string text)
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            if (string.IsNullOrWhiteSpace(text))
                return;
            string js = $"window.twAddStep && window.twAddStep({JsonSerializer.Serialize(text)});";
            _ = ChatBrowser.ExecuteScriptAsync(js);
        }

        /// <summary>
        /// Feeds the full answer-so-far to the JS typewriter, which reveals it
        /// gradually. Idempotent target updates; safe to call on every token.
        /// </summary>
        private void TwPush(string fullText)
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            string js = $"window.twPush && window.twPush({JsonSerializer.Serialize(HideInternalMarkers(fullText))});";
            _ = ChatBrowser.ExecuteScriptAsync(js);
        }

        /// <summary>
        /// Hands the typewriter the final rendered markdown. The JS engine swaps the
        /// typed raw text for this HTML exactly once, only after the reveal catches
        /// up — so the answer never "pops" in all at once.
        /// </summary>
        /// <param name="feedbackMessageId">
        /// v1.14: stamped onto the live bubble at swap time so the feedback row
        /// appears on the answer immediately, instead of only after the next
        /// NavigateToString rebuilds the page from the buffer.
        /// </param>
        private void TwFinish(string finalInnerHtml, string? feedbackMessageId = null)
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            string msgIdArg = string.IsNullOrEmpty(feedbackMessageId)
                ? "null"
                : JsonSerializer.Serialize(feedbackMessageId);
            string js = $"window.twFinish && window.twFinish({JsonSerializer.Serialize(finalInnerHtml)}, {msgIdArg});";
            _ = ChatBrowser.ExecuteScriptAsync(js);
        }

        /// <summary>Stops the typewriter timer (used when the user cancels mid-stream).</summary>
        private void TwStop()
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            _ = ChatBrowser.ExecuteScriptAsync("window.twStop && window.twStop();");
        }

        /// <summary>
        /// Shows the current interactive-pick step as ONE line at the bottom of
        /// the live assistant message, replacing whatever step was there.
        ///
        /// Deliberately a DOM patch and NOT part of ConversationHtml: the step
        /// line is transient UI, not conversation history, so it must not be
        /// replayed on tab switch or persisted to the session log.
        /// </summary>
        private void SetStepHint(string markdown)
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            if (string.IsNullOrWhiteSpace(markdown))
                return;

            string html = ChatHtmlRenderer.ApplyInlineMarkdown(
                System.Net.WebUtility.HtmlEncode(markdown));
            string js =
                $"window.mahodSetStepHint && window.mahodSetStepHint({JsonSerializer.Serialize(html)});";
            _ = ChatBrowser.ExecuteScriptAsync(js);
        }

        /// <summary>
        /// Makes the ACTIVE CHAT TAB's bound drawing the active Civil 3D
        /// document, so a UI action runs on the drawing the engineer is talking
        /// to rather than on whichever drawing happens to be in front.
        ///
        /// Returns false when the bound drawing is not open; the caller shows
        /// <paramref name="reason"/> and must abort — silently falling back to
        /// the active document is what analysed the wrong drawing and orphaned
        /// the loader (owner report, 2026-08-04).
        /// </summary>
        private async Task<bool> TryFocusTabDrawingAsync(DrawingTab? tab, Action<string>? onWaiting = null)
        {
            var docMgr = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager;

            var open = new List<string>();
            foreach (Autodesk.AutoCAD.ApplicationServices.Document d in docMgr)
                open.Add(d.Name);

            var plan = Tools.ToolTargetPlanner.PlanForBoundDrawing(
                tab?.DrawingPath, open, docMgr.MdiActiveDocument?.Name);

            switch (plan.Kind)
            {
                case Tools.ToolTargetKind.UseActive:
                    _focusFailureReason = null;
                    return true;

                case Tools.ToolTargetKind.Fail:
                    // Say what to do AND promise where it will run, so the
                    // engineer isn't left wondering which drawing they'd get.
                    _focusFailureReason = plan.ErrorMessageHe +
                        " הפעולה תרוץ על השרטוט של השיחה הזו ברגע שהוא יהיה פתוח ופעיל.";
                    return false;
            }

            // Open but not active — bring it forward for the engineer, and SAY so:
            // the Civil 3D window is about to change drawings under them, and an
            // unexplained switch reads like the plugin misbehaving.
            onWaiting?.Invoke(plan.TargetDocument!);
            AddSystemMessage(
                $"עברתי לשרטוט '{Tools.ToolTargetPlanner.DisplayName(plan.TargetDocument!)}' — " +
                "הפעולה תמיד רצה על השרטוט של השיחה הזו.");
            try
            {
                foreach (Autodesk.AutoCAD.ApplicationServices.Document d in docMgr)
                {
                    if (string.Equals(d.Name, plan.TargetDocument, StringComparison.OrdinalIgnoreCase))
                    {
                        docMgr.MdiActiveDocument = d;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // Activation is refused while a command is running.
                System.Diagnostics.Debug.WriteLine($"[FocusTabDrawing] activation failed: {ex.Message}");
            }

            // The switch completes on a later message pump — wait for it to take
            // instead of extracting from the drawing that is still in front.
            for (int i = 0; i < 40; i++)
            {
                if (string.Equals(docMgr.MdiActiveDocument?.Name, plan.TargetDocument,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _focusFailureReason = null;
                    return true;
                }
                await Task.Delay(50);
            }

            _focusFailureReason =
                $"לא הצלחתי לעבור לשרטוט '{Tools.ToolTargetPlanner.DisplayName(plan.TargetDocument!)}' " +
                "(ייתכן שפקודה פעילה ב-Civil 3D). עברו אליו ידנית ונסו שוב.";
            return false;
        }

        /// <summary>Why the last <see cref="TryFocusTabDrawingAsync"/> failed (Hebrew, user-facing).</summary>
        private string? _focusFailureReason;

        /// <summary>Removes the live step line (pick sequence finished or aborted).</summary>
        private void ClearStepHint()
        {
            if (!_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;
            _ = ChatBrowser.ExecuteScriptAsync(
                "window.mahodClearStepHint && window.mahodClearStepHint();");
        }

        private void ShowWelcomeOnce()
        {
            if (_vm.WelcomeShown)
                return;

            _vm.WelcomeShown = true;
        }

        #region Multi-Tab Management

        /// <summary>
        /// Save current tab state including live pending edits from WebView.
        /// </summary>
        private void SaveCurrentTabState()
        {
            _vm.SaveCurrentTabState();

            // Fire-and-forget: scrape live in-progress edits from the WebView
            if (_vm.ActiveTabKey != null && _vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey, out var tab))
            {
                _ = CaptureLivePendingEditsAsync(tab);
            }
        }

        /// <summary>
        /// Scrapes window.__currentFixEdits from the WebView and merges into the tab.
        /// </summary>
        private async Task CaptureLivePendingEditsAsync(DrawingTab? tab)
        {
            if (tab == null) return;
            try
            {
                if (ChatBrowser?.CoreWebView2 == null) return;
                var raw = await ChatBrowser.CoreWebView2.ExecuteScriptAsync(
                    FixPlanHtmlRenderer.BuildCaptureEditsScript());
                if (string.IsNullOrEmpty(raw) || raw == "null") return;

                string payloadJson;
                try
                {
                    payloadJson = JsonSerializer.Deserialize<string>(raw) ?? "";
                }
                catch
                {
                    payloadJson = raw;
                }
                if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson == "{}") return;

                using var doc = JsonDocument.Parse(payloadJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

                foreach (var planProp in doc.RootElement.EnumerateObject())
                {
                    if (planProp.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<FixItemEdit>();
                    foreach (var entry in planProp.Value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object) continue;
                        var edit = new FixItemEdit();
                        if (entry.TryGetProperty("item_id", out var idProp))
                            edit.ItemId = idProp.GetString() ?? "";
                        if (string.IsNullOrEmpty(edit.ItemId)) continue;
                        if (entry.TryGetProperty("tool_params", out var paramsProp) &&
                            paramsProp.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var p in paramsProp.EnumerateObject())
                                edit.ToolParams[p.Name] = p.Value.Clone();
                        }
                        list.Add(edit);
                    }
                    tab.PendingFixEdits[planProp.Name] = list;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] CaptureLivePendingEditsAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Guarantee at least one chat tab exists, so the tab strip is always present and
        /// there is always an active chat to type into (Claude-style). Binds the new tab to
        /// the active drawing when one is open, so a later analyze reuses it instead of
        /// forking a second tab.
        /// </summary>
        private void EnsureActiveTab()
        {
            if (_vm.DrawingTabs.Count > 0)
                return;

            string? activeDrawingPath = null;
            try
            {
                activeDrawingPath = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument?.Name;
            }
            catch { }
            _vm.CreateTab(activeDrawingPath);
        }

        private void CloseDrawingTab(string tabId)
        {
            // Capture the closing tab's session id + active stream so we can ask
            // the agent to stop generating for it (saves LLM tokens). Then drop
            // the session→tab mapping so any further stream events are ignored.
            string? closingSessionId = null;
            string? closingStreamId = null;
            if (_vm.DrawingTabs.TryGetValue(tabId, out var closingTab))
            {
                closingSessionId = closingTab.SessionId;
                closingStreamId = closingTab.CurrentStreamId;
            }

            _vm.UnregisterTabSessions(tabId);

            if (_vm.WsClient != null && _vm.WsClient.IsConnected && !string.IsNullOrEmpty(closingStreamId))
            {
                _ = _vm.WsClient.CancelStreamAsync(closingSessionId, closingStreamId!);
            }

            _vm.CloseDrawingTab(tabId);

            // Never strand the user with zero tabs — the strip would vanish and there
            // would be no active chat to type into. Like Claude's UI, closing the last
            // tab immediately opens a fresh empty chat so a tab is always present.
            EnsureActiveTab();

            // Always re-render so the tab strip reflects the deletion immediately,
            // regardless of whether we stayed on the same tab or switched.
            RefreshApplyFixesButton();
            if (_vm.AssistantInitialized)
            {
                EnableInput(!_vm.ActiveTabSending);
            }
            ShowStopButton(_vm.ActiveTabSending);
            UpdateBrowser();
            System.Diagnostics.Debug.WriteLine(
                $"Closed tab: {tabId} ({_vm.DrawingTabs.Count} tabs remaining; cancelled stream={closingStreamId})");
        }

        private void OnDrawingChangeBatchReady(object? sender, Events.ChangeBatch batch)
        {
            // Event fires on a background thread — marshal to UI thread
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnDrawingChangeBatchReady(sender, batch)));
                return;
            }

            try
            {
                _vm.ToolExecutor?.Cache.Clear();
                System.Diagnostics.Debug.WriteLine($"[MahodAI] Tool cache cleared due to drawing changes ({batch.Changes?.Count ?? 0} objects)");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] Cache invalidation on drawing change error: {ex.Message}");
            }
        }

        private void OnDocumentActivated(object? sender, Events.DocumentActivatedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                var target = _vm.FindMostRecentTabFor(e.DocumentName);
                if (target != null && _vm.ActiveTabKey != target.TabId)
                {
                    SaveCurrentTabState();
                    _vm.RestoreTabState(target);
                    RefreshApplyFixesButton();
                    if (_vm.AssistantInitialized) EnableInput(!target.IsSending);
                    ShowStopButton(target.IsSending);
                    UpdateBrowser();
                    System.Diagnostics.Debug.WriteLine($"Auto-switched tab to: {target.DisplayName}");
                }
                // No tab for this drawing yet: do nothing — user must click "+" to bind.
            });
        }

        private void OnDocumentClosing(object? sender, Events.DocumentActivatedEventArgs e)
        {
            // Tabs survive drawing close per user decision — they keep working in detached mode
            // (the agent still holds the drawing summary in Redis for the session TTL).
            // The problem overlay, however, holds world coordinates from a specific drawing;
            // tear it down when that drawing closes so no stale pins linger.
            if (!string.IsNullOrEmpty(_lastProblemDrawing) &&
                string.Equals(e.DocumentName, _lastProblemDrawing, StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.Invoke(() =>
                {
                    ClearProblemOverlay();
                    _lastProblemMarkers = new();
                    _lastProblemDrawing = null;
                    _lastProblemSignature = null;
                    _suppressedProblemSignature = null;
                    _markerPhase = MarkerPhase.None;
                });
            }
            _ = sender;
        }

        #endregion

        private void UpdateConnectionStatus(bool connected)
        {
            Action updateAction = () =>
            {
                if (connected)
                {
                    ConnectionDot.Fill = new WpfSolidColorBrush(WpfColor.FromRgb(34, 197, 94));
                    ConnectionText.Text = "מחובר";
                }
                else
                {
                    ConnectionDot.Fill = new WpfSolidColorBrush(WpfColor.FromRgb(220, 38, 38));
                    ConnectionText.Text = "מנותק";
                }
            };

            if (Dispatcher.CheckAccess())
                updateAction();
            else
                Dispatcher.Invoke(updateAction);
        }

        #endregion

        #region Input / Free Chat

        private void EnableInput(bool enabled)
        {
            if (InputTextBox == null || SendButton == null)
                return;

            bool wasDisabled = !InputTextBox.IsEnabled;
            InputTextBox.IsEnabled = enabled;
            SendButton.IsEnabled = enabled;
            SendButton.Cursor = enabled ? WpfCursors.Hand : WpfCursors.Arrow;

            // Re-enabling means the request is over (stream ended / error / idle):
            // always restore Send and hide Stop. Disabling does NOT auto-show Stop
            // (it's also used for the "disconnected" state) — the send/analyze flows
            // show Stop explicitly via ShowStopButton(true).
            if (enabled)
                ShowStopButton(false);

            // After re-enabling (e.g. stream ended), put focus back in the textbox
            // so the user can keep typing without manually clicking it again.
            if (enabled && wasDisabled)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (InputTextBox != null && InputTextBox.IsEnabled)
                    {
                        InputTextBox.Focus();
                        System.Windows.Input.Keyboard.Focus(InputTextBox);
                        InputTextBox.CaretIndex = InputTextBox.Text?.Length ?? 0;
                    }
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            _ = SendMessageAsync();
        }

        /// <summary>
        /// Toggle the composer's primary action between Send (idle) and Stop
        /// (a request is in flight). The two buttons share one grid cell, so only
        /// one is ever visible. Safe to call from any thread.
        /// </summary>
        private void ShowStopButton(bool show)
        {
            Action apply = () =>
            {
                if (SendButton != null)
                    SendButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
                if (StopButton != null)
                    StopButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            };

            if (Dispatcher.CheckAccess())
                apply();
            else
                Dispatcher.Invoke(apply);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _ = CancelCurrentRequestAsync();
        }

        /// <summary>
        /// Aborts the active tab's in-flight request. Resets the composer to idle
        /// immediately (keeping whatever text already streamed in), then asks the
        /// agent to stop generating. Late stream events for this generation are
        /// ignored via <see cref="Models.DrawingTab.CancelRequested"/>.
        /// </summary>
        private async Task CancelCurrentRequestAsync()
        {
            var tab = _vm.ActiveTab;
            if (tab == null || !tab.IsSending)
                return;

            tab.CancelRequested = true;
            string? streamId = tab.CurrentStreamId;
            string? sessionId = tab.SessionId;

            // HTTP-fallback path: abort the in-flight POST. No-op on the WS path.
            try { tab.RequestCts?.Cancel(); } catch { /* already disposed/completed */ }

            // Finalize the UI right away — don't wait for a stream_ended that may
            // never arrive after a cancel. Preserve any partial answer received so far.
            string partial = tab.CurrentStreamContent.ToString();
            tab.IsSending = false;
            tab.CurrentStreamId = null;
            tab.CurrentStreamContent.Clear();
            tab.CurrentLoaderId = null;

            TwStop();              // stop the typewriter timer before the finalize reload
            RemoveTypingIndicator();
            if (!string.IsNullOrWhiteSpace(partial))
                ReplaceStreamingBlockWithPlainText(partial + "\n\n_⏹ הופסק על ידי המשתמש._");
            else
                ReplaceStreamingBlockWithPlainText("⏹ הבקשה הופסקה.");

            if (_vm.AssistantInitialized)
                EnableInput(true);   // restores Send, hides Stop

            // WebSocket path: tell the agent to stop generating so we don't keep
            // burning LLM tokens. Best-effort — fire after the UI is already reset.
            if (_vm.WsClient != null && _vm.WsClient.IsConnected && !string.IsNullOrEmpty(streamId))
            {
                try { await _vm.WsClient.CancelStreamAsync(sessionId, streamId!); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MahodAI] CancelStream failed: {ex.Message}"); }
            }
        }

        private void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "מסמכים ותמונות|*.pdf;*.docx;*.doc;*.txt;*.md;*.csv;*.xlsx;*.xls;*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|תמונות|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.bmp|מסמכים|*.pdf;*.docx;*.doc;*.txt;*.md;*.csv;*.xlsx;*.xls|כל הקבצים|*.*",
                Multiselect = true,
                Title = "בחר קבצים או תמונות לצירוף"
            };

            if (dlg.ShowDialog() == true)
            {
                foreach (var path in dlg.FileNames)
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Length > 10 * 1024 * 1024)
                    {
                        AddSystemMessage($"הקובץ {fi.Name} גדול מדי (מקסימום 10MB).");
                        continue;
                    }

                    AttachOnePath(path);
                }

                RefreshAttachmentHint();
            }
        }

        /// <summary>
        /// Routes one file to the right channel.
        /// </summary>
        /// <remarks>
        /// Three destinations, and the difference is not cosmetic:
        /// images (v1.16) and PDF/text documents (v1.17) travel WHOLE and reach
        /// the model as real files; everything else — DOCX/XLS, or a document
        /// too large to inline — keeps the older upload route where the server
        /// extracts text.
        /// </remarks>
        private void AttachOnePath(string path)
        {
            if (Utilities.ImageAttachmentHelper.IsImagePath(path))
            {
                AttachImageFile(path);
                return;
            }

            if (Utilities.ImageAttachmentHelper.IsNativeDocument(path))
            {
                AttachDocumentFile(path);
                return;
            }

            _vm.PendingAttachmentPaths.Add(path);
        }

        /// <summary>Adds one document to the pending attachments, with limits.</summary>
        private void AttachDocumentFile(string path)
        {
            if (_vm.PendingDocuments.Count >= Utilities.ImageAttachmentHelper.MaxDocumentsPerMessage)
            {
                AddSystemMessage($"ניתן לצרף עד {Utilities.ImageAttachmentHelper.MaxDocumentsPerMessage} מסמכים בהודעה.");
                return;
            }

            var doc = Utilities.ImageAttachmentHelper.DocumentFromFile(path, out var error);
            if (doc == null)
            {
                // Not fatal — fall back to the extract-to-text upload route so
                // the engineer's file still reaches the agent somehow.
                System.Diagnostics.Debug.WriteLine($"[MahodAI] Document attach failed: {error}");
                _vm.PendingAttachmentPaths.Add(path);
                return;
            }
            _vm.PendingDocuments.Add(doc);
        }

        /// <summary>Adds one image file to the pending attachments, with limits.</summary>
        private void AttachImageFile(string path)
        {
            if (_vm.PendingImages.Count >= Utilities.ImageAttachmentHelper.MaxImagesPerMessage)
            {
                AddSystemMessage($"ניתן לצרף עד {Utilities.ImageAttachmentHelper.MaxImagesPerMessage} תמונות בהודעה.");
                return;
            }

            var image = Utilities.ImageAttachmentHelper.FromFile(path, out var error);
            if (image == null)
            {
                AddSystemMessage(error ?? "לא ניתן לקרוא את התמונה.");
                return;
            }
            _vm.PendingImages.Add(image);
        }

        /// <summary>
        /// Rewrites the 📎 hint line at the top of the input box.
        /// </summary>
        /// <remarks>
        /// The input is a plain WPF TextBox with no chip strip, so the pending
        /// attachment list is surfaced as a line of text that
        /// <see cref="SendMessageAsync"/> strips again before sending.
        /// </remarks>
        private void RefreshAttachmentHint()
        {
            var names = _vm.PendingAttachmentPaths
                .Select(p => System.IO.Path.GetFileName(p))
                .Concat(_vm.PendingDocuments.Select(d => "📕 " + d.Name))
                .Concat(_vm.PendingImages.Select(i => "🖼️ " + i.Name))
                .ToList();
            if (names.Count == 0) return;

            var body = InputTextBox.Text ?? string.Empty;
            // Drop any hint line we wrote earlier so they don't accumulate.
            if (body.StartsWith("📎 "))
            {
                var nl = body.IndexOf('\n');
                body = nl >= 0 ? body.Substring(nl + 1) : string.Empty;
            }

            InputTextBox.Text = $"📎 {string.Join(", ", names)}\n{body}";
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            InputTextBox.Focus();
        }

        private void OnPasteCanExecute(object sender, System.Windows.Input.CanExecuteRoutedEventArgs e)
        {
            if (e.Command == System.Windows.Input.ApplicationCommands.Paste)
            {
                e.CanExecute = true;
                e.Handled = true;
            }
        }

        private void OnPasteExecuted(object sender, System.Windows.Input.ExecutedRoutedEventArgs e)
        {
            if (e.Command == System.Windows.Input.ApplicationCommands.Paste)
            {
                // v1.16: a pasted screenshot becomes an image attachment. Checked
                // BEFORE text because a screenshot copied from some apps also
                // carries a junk text flavour that would win otherwise.
                if (System.Windows.Clipboard.ContainsImage())
                {
                    try
                    {
                        var source = System.Windows.Clipboard.GetImage();
                        if (source != null)
                        {
                            if (_vm.PendingImages.Count >= Utilities.ImageAttachmentHelper.MaxImagesPerMessage)
                            {
                                AddSystemMessage($"ניתן לצרף עד {Utilities.ImageAttachmentHelper.MaxImagesPerMessage} תמונות בהודעה.");
                            }
                            else
                            {
                                var name = $"screenshot-{DateTime.Now:HHmmss}.png";
                                var image = Utilities.ImageAttachmentHelper.FromBitmapSource(source, name, out var error);
                                if (image == null)
                                    AddSystemMessage(error ?? "לא ניתן לקרוא את התמונה שהודבקה.");
                                else
                                {
                                    _vm.PendingImages.Add(image);
                                    RefreshAttachmentHint();
                                }
                            }
                            e.Handled = true;
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MahodAI] Paste image failed: {ex.Message}");
                    }
                }

                if (System.Windows.Clipboard.ContainsText())
                {
                    string text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                    text = text.Replace("\r\n", "\n").Replace("\r", "\n");

                    var textBox = InputTextBox;
                    int caretIndex = textBox.CaretIndex;
                    string currentText = textBox.Text ?? "";

                    if (textBox.SelectionLength > 0)
                    {
                        currentText = currentText.Remove(textBox.SelectionStart, textBox.SelectionLength);
                        caretIndex = textBox.SelectionStart;
                    }

                    textBox.Text = currentText.Insert(caretIndex, text);
                    textBox.CaretIndex = caretIndex + text.Length;

                    System.Diagnostics.Debug.WriteLine($"Paste OK: {text.Length} chars, {text.Count(c => c == '\n')} lines");
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// Drop files onto the input box to attach them (v1.16).
        /// </summary>
        /// <remarks>
        /// The drop target is the WPF TextBox, not the WebView2 transcript: the
        /// transcript hard-disables drag/drop (AllowDrop=False plus a
        /// document-created script that preventDefaults both events), and
        /// re-enabling it there would also let a stray drop navigate the browser
        /// away from the chat.
        /// </remarks>
        private void InputTextBox_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                // WPF's TextBox insists on DragDropEffects.None for file drops
                // unless the preview handler marks the event handled.
                e.Handled = true;
            }
        }

        private void InputTextBox_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;

            var paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;
            e.Handled = true;

            foreach (var path in paths)
            {
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (!fi.Exists) continue;
                    if (fi.Length > 10 * 1024 * 1024)
                    {
                        AddSystemMessage($"הקובץ {fi.Name} גדול מדי (מקסימום 10MB).");
                        continue;
                    }

                    AttachOnePath(path);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] Drop failed for {path}: {ex.Message}");
                }
            }

            RefreshAttachmentHint();
        }

        private void InputTextBox_PreviewKeyDown(object sender, WpfKeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                {
                    var tb = (System.Windows.Controls.TextBox)sender;
                    int caretIndex = tb.CaretIndex;
                    tb.Text = tb.Text.Insert(caretIndex, Environment.NewLine);
                    tb.CaretIndex = caretIndex + Environment.NewLine.Length;
                    e.Handled = true;
                }
                else
                {
                    e.Handled = true;
                    _ = SendMessageAsync();
                }
            }
        }

        private void UserMenuButton_Click(object sender, RoutedEventArgs e)
        {
            UserMenuPopup.IsOpen = !UserMenuPopup.IsOpen;
        }

        private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            UserMenuPopup.IsOpen = false;
            WpfMessageBox.Show(
                "הגדרות המערכת יהיו זמינות בגרסה הבאה.\n\nלהגדרת מפתחות API, ערוך את קובץ ההגדרות או השתמש במשתני סביבה.",
                "הגדרות MahodAI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ProblemsOverlayMenuItem_Click(object sender, RoutedEventArgs e)
        {
            UserMenuPopup.IsOpen = false;
            ToggleProblemOverlay();
        }

        private void ClearChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            UserMenuPopup.IsOpen = false;

            var result = WpfMessageBox.Show(
                "האם לנקות את כל השיחה הנוכחית?",
                "ניקוי צ'אט",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _vm.ConversationHtml.Clear();
                _vm.ScopeCardHtmlStart = -1;
                _vm.LastReportHtml = null;
                _vm.LastOperationsSummary = null;
                _vm.WelcomeShown = false;
                _vm.HasUserSentMessage = false;

                // Drop the on-drawing pins too — the analysis they belonged to is gone.
                ClearProblemOverlay();
                _lastProblemMarkers = new();
                _lastProblemSignature = null;
                _suppressedProblemSignature = null;
                _markerPhase = MarkerPhase.None;

                _vm.CurrentDrawingForSession = null;

                // Also reset the active tab's state
                if (_vm.ActiveTabKey != null && _vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey, out var tab))
                {
                    tab.ConversationHtml.Clear();
                    if (!string.IsNullOrEmpty(tab.SessionId))
                        _vm.SessionIdToTabId.Remove(tab.SessionId!);
                    tab.AgentSessionActive = false;
                    tab.SessionId = null;
                    tab.WelcomeShown = false;
                    tab.HasUserSentMessage = false;
                    tab.LastReportHtml = null;
                    tab.LastOperationsSummary = null;
                    tab.ScopeCardHtmlStart = -1;
                    tab.CurrentFixPlan = null;
                }

                UpdateBrowser();
            }
        }

        private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
        {
            UserMenuPopup.IsOpen = false;
            WpfMessageBox.Show(
                "MahodAI Assistant\n" +
                "גרסה 2.0\n\n" +
                "מערכת AI חכמה להנדסת תחבורה ותשתיות\n" +
                "מהוד הנדסה ©2026\n\n" +
                "מופעל על ידי MahodAI Agent API",
                "אודות MahodAI",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void DarkModeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _vm.IsDarkMode = !_vm.IsDarkMode;
            ApplyTheme();
            UpdateBrowser();
            UserMenuPopup.IsOpen = false;
        }

        /// <summary>
        /// Replace a SolidColorBrush resource so every <c>DynamicResource</c>
        /// consumer (action buttons, connection pill, transcribing overlay,
        /// template hover triggers, secondary text) follows the theme without
        /// being listed here one by one.
        ///
        /// NOTE: mutating <c>brush.Color</c> in place does NOT work — WPF SEALS
        /// (freezes) brushes referenced from Style setters when the style seals,
        /// so the mutation would throw/no-op and the element would keep its light
        /// colour in dark mode. Replacing the dictionary entry is what the
        /// existing MenuHoverBrush swap already relies on; it only reaches
        /// elements that reference the key via DynamicResource (StaticResource
        /// consumers captured the original brush instance and never re-resolve).
        /// </summary>
        private void SetBrushResource(string key, WpfColor color)
        {
            Resources[key] = new WpfSolidColorBrush(color);
        }

        private void ApplyTheme()
        {
            if (_vm.IsDarkMode)
            {
                // Bumped text brightness so the WPF shell matches the brighter web
                // contrast the user asked for — primary is near-white, secondary
                // is a high-key grey, never the dim mid-grey we used before.
                var bgPrimary = new WpfSolidColorBrush(WpfColor.FromRgb(15, 23, 42));
                var bgSecondary = new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59));
                var bgCard = new WpfSolidColorBrush(WpfColor.FromRgb(51, 65, 85));
                var textPrimary = new WpfSolidColorBrush(WpfColor.FromRgb(248, 250, 252));
                var textSecondary = new WpfSolidColorBrush(WpfColor.FromRgb(214, 222, 228));
                var borderColor = new WpfSolidColorBrush(WpfColor.FromRgb(100, 116, 139));

                // Retint the shared brush resources so every StaticResource
                // consumer (the three action buttons' white cards, the green
                // connection pill, transcribing overlay, hovers, placeholder
                // text) goes dark too — these were the "white boxes" that
                // survived earlier dark-mode passes.
                SetBrushResource("PrimaryBackgroundBrush", WpfColor.FromRgb(15, 23, 42));
                SetBrushResource("SecondaryBackgroundBrush", WpfColor.FromRgb(30, 41, 59));
                SetBrushResource("CardBackgroundBrush", WpfColor.FromRgb(51, 65, 85));
                SetBrushResource("PrimaryTextBrush", WpfColor.FromRgb(248, 250, 252));
                SetBrushResource("SecondaryTextBrush", WpfColor.FromRgb(214, 222, 228));
                SetBrushResource("BorderBrush", WpfColor.FromRgb(100, 116, 139));
                SetBrushResource("GreenTintBrush", WpfColor.FromRgb(36, 64, 46));
                SetBrushResource("GreenTintBorderBrush", WpfColor.FromRgb(46, 106, 70));
                // Green labels (action buttons, connection text) need a brighter
                // green to stay legible on the dark tint.
                SetBrushResource("AccentGreenDarkBrush", WpfColor.FromRgb(74, 222, 128));
                SetBrushResource("SubtleHoverBrush", WpfColor.FromRgb(51, 65, 85));

                MainGrid.Background = bgPrimary;
                HeaderBorder.Background = bgCard;
                InputAreaBorder.Background = bgCard;
                InputTextBorder.Background = bgSecondary;
                InputTextBox.Foreground = textPrimary;
                MenuPopupBorder.Background = bgCard;
                MenuPopupBorder.BorderBrush = borderColor;

                // Header logo: swap to the dark-theme variant (neutral ink
                // brightened to near-white) so the wordmark stays visible.
                if (_headerLogoDark != null)
                    HeaderLogo.Source = _headerLogoDark;

                // Header user pill + dropdown — bright text on dark so nothing washes out.
                UserMenuButton.Background = bgCard;
                UserMenuButton.Foreground = textPrimary;
                MenuUserName.Foreground = textPrimary;
                MenuUserPlan.Foreground = textSecondary;
                // Brighter (not darker) hover highlight for the dropdown rows.
                // Lifted from a dim slate-blue (RGB 72,94,116) to a brand-green
                // tint that reads clearly against the dark popup background.
                Resources["MenuHoverBrush"] = new WpfSolidColorBrush(WpfColor.FromRgb(46, 106, 70));

                if (_vm.IsWebViewInitialized && ChatBrowser.CoreWebView2 != null)
                {
                    ChatBrowser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 15, 23, 42);
                }

                // Update only the row's inner texts — assigning Content here used
                // to replace the icon+label Grid with a plain string, breaking the
                // row layout after the first toggle.
                DarkModeLabel.Text = "מצב בהיר";
                DarkModeIcon.Text = "☀️";
                DarkModeMenuItem.Foreground = textPrimary;
                SettingsMenuItem.Foreground = textPrimary;
                ProblemsOverlayMenuItem.Foreground = textPrimary;
                ClearChatMenuItem.Foreground = textPrimary;
                AboutMenuItem.Foreground = textPrimary;
            }
            else
            {
                var bgPrimary = new WpfSolidColorBrush(WpfColor.FromRgb(232, 238, 243));
                var bgSecondary = new WpfSolidColorBrush(WpfColor.FromRgb(245, 247, 250));
                var bgCard = new WpfSolidColorBrush(WpfColor.FromRgb(255, 255, 255));
                var textPrimary = new WpfSolidColorBrush(WpfColor.FromRgb(30, 41, 59));
                var textSecondary = new WpfSolidColorBrush(WpfColor.FromRgb(107, 124, 130));
                var borderColor = new WpfSolidColorBrush(WpfColor.FromRgb(226, 232, 240));

                // Restore the XAML-original light palette on the shared brushes.
                SetBrushResource("PrimaryBackgroundBrush", WpfColor.FromRgb(237, 241, 241));
                SetBrushResource("SecondaryBackgroundBrush", WpfColor.FromRgb(242, 245, 244));
                SetBrushResource("CardBackgroundBrush", WpfColor.FromRgb(255, 255, 255));
                SetBrushResource("PrimaryTextBrush", WpfColor.FromRgb(21, 35, 43));
                SetBrushResource("SecondaryTextBrush", WpfColor.FromRgb(107, 124, 130));
                SetBrushResource("BorderBrush", WpfColor.FromRgb(227, 233, 232));
                SetBrushResource("GreenTintBrush", WpfColor.FromRgb(234, 247, 238));
                SetBrushResource("GreenTintBorderBrush", WpfColor.FromRgb(207, 233, 214));
                SetBrushResource("AccentGreenDarkBrush", WpfColor.FromRgb(28, 124, 56));
                SetBrushResource("SubtleHoverBrush", WpfColor.FromRgb(231, 236, 235));

                MainGrid.Background = bgPrimary;
                HeaderBorder.Background = bgCard;
                InputAreaBorder.Background = bgCard;
                InputTextBorder.Background = bgSecondary;
                InputTextBox.Foreground = textPrimary;
                MenuPopupBorder.Background = bgCard;
                MenuPopupBorder.BorderBrush = borderColor;

                if (_headerLogoLight != null)
                    HeaderLogo.Source = _headerLogoLight;

                UserMenuButton.Background = bgCard;
                UserMenuButton.Foreground = textPrimary;
                MenuUserName.Foreground = textPrimary;
                MenuUserPlan.Foreground = textSecondary;
                Resources["MenuHoverBrush"] = new WpfSolidColorBrush(WpfColor.FromRgb(234, 247, 238));

                if (_vm.IsWebViewInitialized && ChatBrowser.CoreWebView2 != null)
                {
                    ChatBrowser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 232, 238, 243);
                }

                DarkModeLabel.Text = "מצב כהה";
                DarkModeIcon.Text = "🌙";
                DarkModeMenuItem.Foreground = textPrimary;
                SettingsMenuItem.Foreground = textPrimary;
                ProblemsOverlayMenuItem.Foreground = textPrimary;
                ClearChatMenuItem.Foreground = textPrimary;
                AboutMenuItem.Foreground = textPrimary;
            }
        }

        private async Task SendMessageAsync()
        {
            // Per-tab guard: only block re-send while THIS tab is itself streaming.
            // Other tabs may be mid-stream — they don't block this one.
            var sendingTab = _vm.ActiveTab;
            if (sendingTab == null)
            {
                AddSystemMessage("שגיאה: אין טאב פעיל.");
                return;
            }
            if (sendingTab.IsSending)
                return;

            string userText = InputTextBox.Text.Trim();

            // Strip the attachment indicator line from the text
            bool hasPending = _vm.PendingAttachmentPaths.Count > 0
                || _vm.PendingImages.Count > 0
                || _vm.PendingDocuments.Count > 0;
            if (hasPending && userText.StartsWith("📎"))
            {
                int newlineIdx = userText.IndexOf('\n');
                userText = newlineIdx >= 0 ? userText.Substring(newlineIdx + 1).Trim() : "";
            }

            if (string.IsNullOrEmpty(userText) && !hasPending)
                return;

            // A chat message can extract the drawing (quick-session) and can drive
            // tools, so it must target THIS tab's drawing too — asking "how many
            // alignments are here?" from a tab whose drawing isn't in front used
            // to answer about a different drawing entirely.
            if (!await TryFocusTabDrawingAsync(sendingTab))
            {
                AddSystemMessage(
                    _focusFailureReason
                    ?? "השרטוט המשויך לשיחה זו אינו פתוח — פתחו אותו ב-Civil 3D ונסו שוב.");
                return;
            }

            // "remove the violation markers" is a local UI command — clear the pins and
            // acknowledge without a round-trip to the agent. Only treat it as such when pins
            // are actually present, so a road-marking / piping removal request (which shares
            // the overloaded Hebrew root סימון) still reaches the agent when there's nothing
            // on the drawing to clear.
            bool pinsPresent = _lastProblemMarkers.Count > 0 || _problemOverlay.IsVisible;
            if (!hasPending && pinsPresent && IsClearMarkersIntent(userText))
            {
                InputTextBox.Text = string.Empty;
                AddUserMessage(userText);
                ClearMarkersForNewTurn();
                AddSystemMessage("סימוני הבעיות הוסרו מהשרטוט.");
                return;
            }

            // A new turn supersedes the previous analysis: drop its pins now so the browser
            // reload's DOM re-scan can't resurrect them. A fresh analysis re-pins on stream end.
            ClearMarkersForNewTurn();

            InputTextBox.Text = string.Empty;

            var attachments = new List<string>(_vm.PendingAttachmentPaths);
            _vm.PendingAttachmentPaths.Clear();

            // v1.16 image attachments ride the `chat` message itself, so they are
            // snapshotted separately from the document list above.
            var pendingImages = new List<Utilities.PendingImage>(_vm.PendingImages);
            _vm.PendingImages.Clear();

            var pendingDocuments = new List<Utilities.PendingDocument>(_vm.PendingDocuments);
            _vm.PendingDocuments.Clear();

            if (_vm.AgentService == null || !_vm.AssistantInitialized)
            {
                AddSystemMessage("שגיאה: שירות Agent לא מאותחל.");
                return;
            }

            if (!sendingTab.AgentSessionActive)
            {
                await InitializeQuickSessionAsync();
                if (!sendingTab.AgentSessionActive)
                {
                    AddSystemMessage("לא ניתן לאתחל את שיחת ה-AI. ודא שיש שרטוט פעיל וחיבור לשרת.");
                    return;
                }
            }

            string? sessionId = sendingTab.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                AddSystemMessage("שגיאה: לטאב הזה אין session פעיל. נסה לפתוח מחדש.");
                return;
            }

            try
            {
                sendingTab.IsSending = true;
                sendingTab.CancelRequested = false;
                sendingTab.RequestCts?.Dispose();
                sendingTab.RequestCts = new CancellationTokenSource();
                sendingTab.SendStartedAt = DateTime.Now;
                _vm.SendStartedAt = sendingTab.SendStartedAt;
                EnableInput(false);
                ShowStopButton(true);

                // `attachments` here is the LEGACY upload route (DOCX/XLS, or a
                // file too big to inline): the server extracts it to text and it
                // re-enters as its own chat. Attachments proper (images, PDFs,
                // text files) travel WITH this question. Mixing the two would
                // produce two unrelated answers, so refuse rather than surprise.
                if (attachments.Count > 0 && (pendingImages.Count > 0 || pendingDocuments.Count > 0))
                {
                    AddSystemMessage("לא ניתן לשלוח קובץ מסוג זה יחד עם תמונות או מסמכי PDF באותה הודעה. שלחו אותם בנפרד.");
                    // Give them back so the engineer doesn't have to re-attach.
                    _vm.PendingImages.AddRange(pendingImages);
                    _vm.PendingDocuments.AddRange(pendingDocuments);
                    sendingTab.IsSending = false;
                    EnableInput(true);
                    return;
                }

                // Upload attachments first
                if (attachments.Count > 0)
                {
                    foreach (var filePath in attachments)
                    {
                        var fileName = System.IO.Path.GetFileName(filePath);
                        var fileSize = new System.IO.FileInfo(filePath).Length / 1024;
                        AddUserMessage($"📎 {fileName} ({fileSize}KB)");
                    }

                    if (!string.IsNullOrEmpty(userText))
                        AddUserMessage(userText);

                    StartAssistantStreamingMessage();

                    if (_vm.WsClient != null && _vm.WsClient.IsConnected)
                    {
                        foreach (var filePath in attachments)
                        {
                            var fileInfo = new System.IO.FileInfo(filePath);
                            string fileName = fileInfo.Name;

                            if (fileInfo.Length > 1 * 1024 * 1024)
                            {
                                AddSystemMessage($"מעלה {fileName} ({fileInfo.Length / 1024}KB) דרך HTTP...");
                                var extractedText = await _commService.UploadLargeFileAsync(filePath, sessionId);
                                if (extractedText != null)
                                {
                                    var content = $"[קובץ מצורף: {fileName}]\n\n{extractedText}";
                                    if (!string.IsNullOrEmpty(userText))
                                        content = $"{userText}\n\n{content}";
                                    await _vm.WsClient.ChatAsync(sessionId!, content);
                                }
                                else
                                {
                                    AddSystemMessage($"שגיאה: לא הצלחתי לעבד את {fileName}.");
                                }
                            }
                            else
                            {
                                byte[] fileBytes = System.IO.File.ReadAllBytes(filePath);
                                string base64Data = Convert.ToBase64String(fileBytes);
                                await _vm.WsClient.SendFileUploadAsync(sessionId!, fileName, base64Data, userText);
                            }
                            userText = null;
                        }
                        return;
                    }
                    else
                    {
                        AddSystemMessage("שגיאה: אין חיבור WebSocket פעיל.");
                        sendingTab.IsSending = false;
                        EnableInput(true);
                        return;
                    }
                }

                if (pendingImages.Count > 0 || pendingDocuments.Count > 0)
                {
                    // Attaching a file with no question is a clear request on its
                    // own; the agent applies the same default server-side.
                    if (string.IsNullOrEmpty(userText))
                    {
                        userText = (pendingDocuments.Count > 0 && pendingImages.Count == 0)
                            ? "נתח את המסמך המצורף."
                            : "נתח את התמונה המצורפת.";
                    }

                    AddUserAttachmentMessage(userText, pendingImages, pendingDocuments);
                    StartAssistantStreamingMessage();

                    if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
                    {
                        // Documents first — the question is usually about them,
                        // and the model reads the trailing text as the ask.
                        var wire = Utilities.ImageAttachmentHelper.ToAttachments(pendingDocuments);
                        wire.AddRange(Utilities.ImageAttachmentHelper.ToAttachments(pendingImages));

                        await _vm.WsClient.ChatAsync(sessionId!, userText, attachments: wire);
                        return;
                    }

                    // The legacy REST path has no attachment channel at all.
                    AddSystemMessage("צירוף קבצים דורש חיבור WebSocket פעיל.");
                    sendingTab.IsSending = false;
                    EnableInput(true);
                    return;
                }

                AddUserMessage(userText);
                StartAssistantStreamingMessage();

                if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
                {
                    await _vm.WsClient.ChatAsync(sessionId!, userText);
                    return;
                }

                // Fallback to HTTP
                var result = await _vm.AgentService.ChatAsync(
                    sessionId!, userText, sendingTab.RequestCts.Token);

                // User pressed Stop while the POST was in flight — the cancel
                // handler already reset the UI, so drop the (now stale) response.
                if (sendingTab.CancelRequested)
                    return;

                if (result.DetailRequests != null && result.DetailRequests.Count > 0)
                {
                    await HandleDetailRequestsAsync(sessionId!, result.DetailRequests);
                }

                if (!string.IsNullOrEmpty(result.Content))
                {
                    ReplaceStreamingBlockWithPlainText(result.Content);
                }
                else
                {
                    ReplaceStreamingBlockWithPlainText("לא התקבלה תשובה מהשרת.");
                }
            }
            catch (OperationCanceledException)
            {
                // User pressed Stop — CancelCurrentRequestAsync already reset the UI.
            }
            catch (MahodAgentException ex)
            {
                ReplaceStreamingBlockWithPlainText($"שגיאת Agent: {ex.Message}");
            }
            catch (Exception ex)
            {
                ReplaceStreamingBlockWithPlainText($"שגיאה: {ex.Message}");
                System.Diagnostics.Debug.WriteLine("SendMessageAsync error: " + ex);
            }
            finally
            {
                if (!_vm.UseWebSocket || _vm.WsClient == null || !_vm.WsClient.IsConnected)
                {
                    sendingTab.IsSending = false;
                    if (_vm.AssistantInitialized)
                        EnableInput(true);
                }
            }
        }

        #endregion

        /// <summary>
        /// Creates a lightweight session for chat without requiring a full analysis run.
        /// </summary>
        private async Task InitializeQuickSessionAsync()
        {
            try
            {
                bool success = await _commService.InitializeQuickSessionAsync(() =>
                {
                    return Dispatcher.Invoke(() =>
                    {
                        var extractor = new DrawingSummaryExtractor();
                        return extractor.ExtractSummaryAsJson();
                    });
                });

                if (success)
                {
                    // Update browser if tab was switched
                    UpdateBrowser();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InitializeQuickSessionAsync failed: {ex.Message}");
            }
        }

        #region Analyze DWG

        private async void AnalyzeDrawingButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[AnalyzeClick] fired at {DateTime.Now:HH:mm:ss}");

            RefreshApplyFixesButton();

            if (_vm.AgentService == null || !_vm.AssistantInitialized)
            {
                string state = $"_vm.AgentService={(_vm.AgentService == null ? "null" : "OK")}, _vm.AssistantInitialized={_vm.AssistantInitialized}";
                try { AddSystemMessage("שגיאה: שירות Agent לא מאותחל. " + state + ". בדוק חיבור לשרת."); }
                catch { }
                return;
            }

            var button = sender as System.Windows.Controls.Button;
            if (button != null) button.IsEnabled = false;

            // Immediate feedback: drawing extraction can take 2-3 seconds before the
            // scope card appears. Hide the welcome and show the same rich thinking
            // block (bot video + "חושב…") that the chat flow uses.
            _vm.HasUserSentMessage = true;
            if (_vm.ActiveTabKey != null && _vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey, out var preTab))
                preTab.HasUserSentMessage = true;
            StartAssistantStreamingMessage();

            // Force WebView2 to paint the thinking block before we hijack the UI thread
            // for extraction. Render priority isn't enough — UpdateBrowser's NavigateToString
            // is async, and ApplicationIdle waits until the dispatcher queue (incl. layout
            // and the 100ms Task.Delay inside UpdateBrowser) is fully drained.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);

            try
            {
                // Analyse the drawing THIS chat tab is bound to, not whatever is
                // in front. Reading MdiActiveDocument here used to analyse the
                // active drawing, create a second tab for it, and leave this
                // tab's loader spinning forever (owner report, 2026-08-04).
                var boundTab = _vm.ActiveTab;
                if (!await TryFocusTabDrawingAsync(boundTab))
                {
                    ReplaceLastAssistantStreamBlock("");
                    AddAssistantMessage(
                        _focusFailureReason
                        ?? "השרטוט המשויך לשיחה זו אינו פתוח — פתחו אותו ב-Civil 3D ונסו שוב.");
                    return;
                }

                // Fast path for the scope dialog: skip the alignment-pair
                // intersection scan (DetectCrossing was the dominant cost,
                // throwing thousands of PointNotOnEntityException on real
                // drawings — the AABB pre-filter in DrawingSummaryExtractor
                // makes it fast on the analyze side too). The scope card
                // only needs entity counts and per-alignment metadata;
                // intersections are re-extracted just before sending the
                // analyze message (see HandleScopeSelectionAction).
                string json = await Task.Run(() =>
                {
                    return Dispatcher.Invoke(() =>
                    {
                        var extractor = new DrawingSummaryExtractor();
                        return extractor.ExtractSummaryAsJson(includeIntersections: false);
                    });
                });

                // Check extraction quality and retry if incomplete
                if (!string.IsNullOrEmpty(json))
                {
                    var quickCheck = ParseEntityCounts(json);
                    System.Diagnostics.Debug.WriteLine(
                        $"First extraction (fast): alignments={quickCheck.Alignments}, profiles={quickCheck.DesignProfiles}, " +
                        $"corridors={quickCheck.Corridors}, pipes={quickCheck.PipeNetworks}, json_length={json.Length}");

                    bool hasProfilesButNoAlignments = quickCheck.DesignProfiles > 0 && quickCheck.Alignments == 0;
                    bool allEmpty = quickCheck.Alignments == 0 && quickCheck.DesignProfiles == 0
                        && quickCheck.Corridors == 0 && quickCheck.PipeNetworks == 0;
                    if (hasProfilesButNoAlignments || allEmpty)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"Extraction incomplete, retrying after 2s delay...");
                        await Task.Delay(2000);
                        json = await Task.Run(() =>
                        {
                            return Dispatcher.Invoke(() =>
                            {
                                var extractor = new DrawingSummaryExtractor();
                                return extractor.ExtractSummaryAsJson(includeIntersections: false);
                            });
                        });
                        var retryCheck = ParseEntityCounts(json);
                        System.Diagnostics.Debug.WriteLine(
                            $"Retry extraction (fast): alignments={retryCheck.Alignments}, profiles={retryCheck.DesignProfiles}, " +
                            $"corridors={retryCheck.Corridors}");
                    }
                }

                if (_vm.ExtractionEngine == null)
                {
                    ReplaceLastAssistantStreamBlock("");
                    AddAssistantMessage("לא נמצאו נתוני ניתוח. ודא שיש שרטוט פעיל.");
                    _vm.LastSummary = null;
                    _vm.LastDwgJson = null;
                    return;
                }

                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                string drawingName = doc?.Name ?? "unknown.dwg";

                // Ensure the active tab is bound to this drawing. Bindings are immutable, so
                // if the active tab is bound to a different drawing (or none), create a fresh tab.
                DrawingTab? activeTab = null;
                if (_vm.ActiveTabKey != null)
                    _vm.DrawingTabs.TryGetValue(_vm.ActiveTabKey, out activeTab);

                if (activeTab == null ||
                    !string.Equals(activeTab.DrawingPath, drawingName, StringComparison.OrdinalIgnoreCase))
                {
                    var existing = _vm.FindMostRecentTabFor(drawingName);
                    activeTab = existing ?? _vm.CreateTab(drawingName);
                    if (existing != null) _vm.SwitchToTab(existing.TabId);
                    UpdateBrowser();
                }

                _vm.LastDwgJson = json;
                activeTab.LastDwgJson = json;

                if (activeTab.SessionId == null)
                {
                    // Prefer the WebSocket session-create when available — that's the
                    // session the streaming chat/analyze pipelines will run against.
                    // Fall back to REST only if the WS is offline.
                    string? newSessionId = null;
                    if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
                    {
                        try
                        {
                            var summaryJson = JsonDocument.Parse(json);
                            var drawingId = Guid.NewGuid().ToString("N");
                            var summary = summaryJson.RootElement;
                            var wsSession = await _vm.WsClient.CreateSessionAsync(drawingId, drawingName, summary);
                            newSessionId = wsSession.SessionId;
                            System.Diagnostics.Debug.WriteLine(
                                $"WebSocket session created: {newSessionId} for {drawingName}");
                        }
                        catch (Exception wsEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"WebSocket session creation failed: {wsEx.Message}");
                        }
                    }

                    if (string.IsNullOrEmpty(newSessionId))
                    {
                        var session = await _vm.AgentService.CreateSessionAsync(json, drawingName: drawingName);
                        newSessionId = session.SessionId;
                        System.Diagnostics.Debug.WriteLine(
                            $"REST session created: {newSessionId} for tab {activeTab.DisplayName}");
                    }

                    activeTab.SessionId = newSessionId;
                    activeTab.AgentSessionActive = true;
                    _vm.RegisterSession(newSessionId!, activeTab.TabId);
                    _vm.CurrentDrawingForSession = drawingName;
                }

                // Show inline scope selection card — first remove the thinking block.
                ReplaceLastAssistantStreamBlock("");
                _vm.ScopeCardHtmlStart = _vm.ConversationHtml.Length;
                var counts = ParseEntityCounts(_vm.LastDwgJson);
                System.Diagnostics.Debug.WriteLine(
                    $"Scope dialog counts: alignments={counts.Alignments}, profiles={counts.DesignProfiles}, " +
                    $"corridors={counts.Corridors}, pipes={counts.PipeNetworks}, " +
                    $"signs={counts.Signs}, markings={counts.Markings}, ramps={counts.Ramps}, " +
                    $"alignmentDetails={counts.AlignmentDetails?.Count ?? 0}");
                string scopeHtml = AnalysisScopeHtmlRenderer.Render(counts);
                string ts = DateTime.Now.ToString("HH:mm");
                _vm.ConversationHtml.Append($@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{scopeHtml}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");
                UpdateBrowser();
            }
            catch (MahodAgentException ex)
            {
                ReplaceLastAssistantStreamBlock("");
                AddSystemMessage($"שגיאת Agent: {ex.Message}");
                _vm.LastSummary = null;
                _vm.LastDwgJson = null;
                RefreshApplyFixesButton();
            }
            catch (Exception ex)
            {
                ReplaceLastAssistantStreamBlock("");
                AddSystemMessage($"שגיאה בניתוח השרטוט: {ex.Message}");
                _vm.LastSummary = null;
                _vm.LastDwgJson = null;
                RefreshApplyFixesButton();
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async Task HandleDetailRequestsAsync(string sessionId, System.Collections.Generic.List<DetailRequest>? requests)
        {
            if (requests == null || requests.Count == 0 || _vm.AgentService == null)
                return;

            foreach (var req in requests)
            {
                System.Diagnostics.Debug.WriteLine($"Agent requesting details: {req.Type} - {req.EntityName}");

                object? detailData = req.Type switch
                {
                    "surface" => ExtractSurfaceDetails(req.EntityName),
                    "alignment" => ExtractAlignmentDetails(req.EntityName),
                    "profile" => ExtractProfileDetails(req.EntityName),
                    _ => null
                };

                if (detailData != null)
                {
                    try
                    {
                        var response = await _vm.AgentService.ProvideDetailsAsync(sessionId, new
                        {
                            detail_type = req.Type,
                            entity_name = req.EntityName,
                            data = detailData
                        });

                        if (!string.IsNullOrEmpty(response.Content))
                            AddAssistantMessage(response.Content);
                    }
                    catch (Exception ex)
                    {
                        AddSystemMessage($"שגיאה בשליחת פרטים: {ex.Message}");
                    }
                }
                else
                {
                    AddSystemMessage($"לא ניתן לחלץ פרטים עבור {req.Type}: {req.EntityName}");
                }
            }
        }

        private object? ExtractSurfaceDetails(string surfaceName)
        {
            try
            {
                return new { name = surfaceName, message = "Surface details extraction not yet implemented" };
            }
            catch
            {
                return null;
            }
        }

        private object? ExtractAlignmentDetails(string alignmentName)
        {
            try
            {
                return new { name = alignmentName, message = "Alignment details extraction not yet implemented" };
            }
            catch
            {
                return null;
            }
        }

        private object? ExtractProfileDetails(string profileName)
        {
            try
            {
                return new { name = profileName, message = "Profile details extraction not yet implemented" };
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Analyze Layers / SHP

        // The visual sheet scan has no button of its own: it is reached by asking for it
        // ("סריקה ויזואלית", "scan visuals"), which the agent's VisualScanPipeline routes.
        // One door, so there is no second copy of the behaviour to keep in step.

        private async void AnalyzeLayersButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Same rule as analyse: report on THIS tab's drawing, never on
                // whichever drawing is in front (LayersAnalyzer reads the active
                // document, so without this it silently reports the wrong one).
                if (!await TryFocusTabDrawingAsync(_vm.ActiveTab))
                {
                    AddAssistantMessage(
                        _focusFailureReason
                        ?? "השרטוט המשויך לשיחה זו אינו פתוח — פתחו אותו ב-Civil 3D ונסו שוב.");
                    return;
                }

                if (!_vm.HasShownDwgLayersReport && !_vm.HasAnalyzedShapefile)
                {
                    await Task.Yield();

                    var layersSummary = LayersAnalyzer.Analyze();
                    string dwgHtml = LayersAnalyzer.BuildHtmlReport(layersSummary);

                    if (string.IsNullOrEmpty(layersSummary.Error) && layersSummary.TotalLayerCount > 0)
                    {
                        AddAssistantHtmlMessage(dwgHtml);
                        _vm.LastReportHtml = dwgHtml;
                        _vm.LastOperationsSummary = "בוצע דו\"ח שכבות וישויות וקטוריות על השרטוט הפעיל.";
                        _vm.HasShownDwgLayersReport = true;
                        return;
                    }

                    string? shpPath1 = ShapefileAnalyzer.PromptForShapefilePath();
                    if (string.IsNullOrWhiteSpace(shpPath1))
                        return;

                    var shpSummary1 = ShapefileAnalyzer.Analyze(shpPath1);
                    _vm.LastShapefileSummary = shpSummary1;
                    _vm.LastShpJson = ChatHtmlRenderer.SafeSerialize(shpSummary1);

                    string shpHtml1 = ShapefileAnalyzer.BuildHtmlReport(shpSummary1, false);
                    AddAssistantHtmlMessage(shpHtml1);
                    _vm.LastReportHtml = shpHtml1;

                    _vm.LastOperationsSummary =
                        $"בוצע ניתוח SHP עבור {Path.GetFileName(shpPath1)} (ללא דו\"ח DWG).";
                    _vm.HasAnalyzedShapefile = true;
                    return;
                }

                if (_vm.HasShownDwgLayersReport && !_vm.HasAnalyzedShapefile)
                {
                    string? shpPath2 = ShapefileAnalyzer.PromptForShapefilePath();
                    if (string.IsNullOrWhiteSpace(shpPath2))
                        return;

                    var shpSummary2 = ShapefileAnalyzer.Analyze(shpPath2);
                    _vm.LastShapefileSummary = shpSummary2;
                    _vm.LastShpJson = ChatHtmlRenderer.SafeSerialize(shpSummary2);

                    string shpHtml2 = ShapefileAnalyzer.BuildHtmlReport(shpSummary2, false);
                    AddAssistantHtmlMessage(shpHtml2);
                    _vm.LastReportHtml = shpHtml2;

                    _vm.LastOperationsSummary =
                        $"בוצע ניתוח SHP עבור {Path.GetFileName(shpPath2)} (בנוסף לדו\"ח ה-DWG).";
                    _vm.HasAnalyzedShapefile = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"שגיאה בניתוח שכבות/SHP: {ex.Message}");
            }
        }

        #endregion

        #region Apply Fixes

        private async void ApplyFixesButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // The button is always pressable, so the "you haven't analysed
                // yet" case is answered HERE, in words, instead of by a dead
                // control. A fix plan that already arrived also counts — it is
                // proof an analysis ran, even after a tab restore.
                var tab = _vm.ActiveTab;
                bool hasAnalysis = tab?.HasCompletedAnalysis == true || _vm.CurrentFixPlan != null;
                if (!hasAnalysis)
                {
                    AddSystemMessage(
                        "כדי לתקן צריך קודם לנתח את השרטוט — לחצו על \"נתח שרטוט\", " +
                        "ואחרי שהניתוח יסתיים אפשר לבצע תיקונים.");
                    return;
                }

                var sessionId = tab?.SessionId;
                if (string.IsNullOrEmpty(sessionId))
                {
                    AddSystemMessage("אין ניתוח זמין לתיקונים בטאב הזה.");
                    return;
                }

                // Fixes mutate geometry — make sure the tab's drawing is the one
                // in front before any of it starts.
                if (!await TryFocusTabDrawingAsync(tab))
                {
                    AddSystemMessage(
                        _focusFailureReason
                        ?? "השרטוט המשויך לשיחה זו אינו פתוח — פתחו אותו ב-Civil 3D ונסו שוב.");
                    return;
                }

                if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
                {
                    SetApplyFixesBusy(true);
                    await _vm.WsClient.SendFixRequestAsync(sessionId!);
                    return;
                }

                AddSystemMessage("אין ניתוח זמין לביצוע תיקונים. נדרש חיבור WebSocket.");
                RefreshApplyFixesButton();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"שגיאה בביצוע תיקונים: {ex.Message}");
                SetApplyFixesBusy(false);
            }
        }

        private void ReplaceOrAppendFixCard(string planId, string innerHtml, bool suppressBrowserUpdate = false)
        {
            var marker = $"id='fix-card-{planId}'";
            var conv = _vm.ConversationHtml.ToString();
            int idx = conv.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                AddAssistantHtmlMessage(innerHtml);
                return;
            }

            int bubbleStart = conv.LastIndexOf("<div class='message assistant-message'", idx, StringComparison.Ordinal);
            if (bubbleStart < 0)
            {
                AddAssistantHtmlMessage(innerHtml);
                return;
            }

            // Replace everything from this bubble's start up to the next bubble
            // (or end of conversation if this is the last one). Finding the
            // matching </div> by character search is fragile — fix cards contain
            // many nested </div> tags. Slicing at the next "<div class='message"
            // gives the correct boundary regardless of line endings or indent.
            int nextBubble = conv.IndexOf("<div class='message", bubbleStart + 1, StringComparison.Ordinal);
            int replaceEnd = nextBubble >= 0 ? nextBubble : conv.Length;
            while (replaceEnd > bubbleStart && char.IsWhiteSpace(conv[replaceEnd - 1]))
                replaceEnd--;

            string ts = DateTime.Now.ToString("HH:mm");
            string newBubble = $@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{innerHtml}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>";

            _vm.ConversationHtml.Remove(bubbleStart, replaceEnd - bubbleStart);
            _vm.ConversationHtml.Insert(bubbleStart, newBubble);
            _vm.LastReportHtml = innerHtml;

            if (!suppressBrowserUpdate)
                UpdateBrowser();
        }

        private async Task HandleFixApprovalAction(JsonElement root)
        {
            var decision = root.TryGetProperty("decision", out var dec) ? dec.GetString() ?? "" : "";
            var planId = root.TryGetProperty("plan_id", out var pid) ? pid.GetString() ?? "" : "";

            if (decision == "accept")
            {
                // Replace the fix plan card with a loader — keeps everything in ONE bubble.
                // The id='fix-card-{planId}' marker is preserved so OnFixResultReceived
                // can later replace this loader with the actual result card.
                var planIdEnc = System.Net.WebUtility.HtmlEncode(planId);
                var loaderHtml = $@"<div id='fix-card-{planIdEnc}' class='fix-result' dir='rtl' style='text-align:right; font-family:""Segoe UI"",Arial,sans-serif;'>
                    <div class='typing-indicator' style='display:flex; align-items:center; gap:8px; padding:12px;'>
                        <span class='pulse-dot'></span>
                        <span class='status-text' style='color:#374151;'>מתחיל לבצע תיקונים...</span>
                    </div>
                </div>";
                if (!string.IsNullOrEmpty(planId))
                    ReplaceOrAppendFixCard(planId, loaderHtml);

                await _fixService.HandleFixApprovalAction(root);
            }
            else if (decision == "decline")
            {
                // Remove the fix plan card from chat (like analysis cancel)
                if (!string.IsNullOrEmpty(planId))
                    RemoveFixCardFromChat(planId);

                await _fixService.HandleFixApprovalAction(root);
                SetApplyFixesBusy(false);

                // Back to the full finding set — the engineer chose not to fix.
                RestoreAnalysisMarkers();
            }
            else
            {
                await _fixService.HandleFixApprovalAction(root);
            }
        }

        /// <summary>
        /// Remove a fix plan/result card and its enclosing assistant bubble from conversation HTML.
        /// </summary>
        private void RemoveFixCardFromChat(string planId)
        {
            var marker = $"id='fix-card-{planId}'";
            var conv = _vm.ConversationHtml.ToString();
            int idx = conv.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;

            // Find the enclosing assistant-message div
            int bubbleStart = conv.LastIndexOf("<div class='message assistant-message'", idx, StringComparison.Ordinal);
            if (bubbleStart < 0) return;

            // Find the end: look for the next message div or end of string
            int nextMessage = conv.IndexOf("<div class='message ", bubbleStart + 10, StringComparison.Ordinal);
            int removeEnd = nextMessage > 0 ? nextMessage : conv.Length;

            _vm.ConversationHtml.Remove(bubbleStart, removeEnd - bubbleStart);
            UpdateBrowser();
        }

        /// <summary>True only while a fix request is in flight (double-submit guard).</summary>
        private bool _applyFixesBusy;

        /// <summary>
        /// Applies the fix button's enablement.
        ///
        /// 2026-08-04 (owner request): "no analysis yet" NO LONGER disables the
        /// button. A dead control teaches nothing — the engineer cannot tell a
        /// missing analysis from a broken plugin. It now stays pressable and
        /// <see cref="ApplyFixesButton_Click"/> answers with what to do instead.
        /// The only state that still disables is an in-flight fix request.
        /// </summary>
        private void RefreshApplyFixesButton()
        {
            if (ApplyFixesButton == null)
                return;

            // Visual state (card background, disabled dimming) is owned by
            // ActionButtonStyle's triggers — just toggle enablement here.
            bool enabled = !_applyFixesBusy;
            ApplyFixesButton.IsEnabled = enabled;
            ApplyFixesButton.Cursor = enabled ? WpfCursors.Hand : WpfCursors.Arrow;
        }

        /// <summary>Marks a fix request in flight (or finished) and re-applies enablement.</summary>
        private void SetApplyFixesBusy(bool busy)
        {
            _applyFixesBusy = busy;
            RefreshApplyFixesButton();
        }

        private void RemoveScopeCardFromChat()
        {
            if (_vm.RemoveScopeCardFromChat())
            {
                UpdateBrowser();
            }
        }

        private async void HandleScopeSelectionAction(JsonElement root)
        {
            var mode = root.TryGetProperty("mode", out var mp) ? mp.GetString() ?? "" : "";

            RemoveScopeCardFromChat();

            if (mode == "cancel")
                return;

            // Extract focus_areas
            List<string>? focusAreas = null;
            if (root.TryGetProperty("focus_areas", out var faProp) && faProp.ValueKind == JsonValueKind.Array)
            {
                focusAreas = new List<string>();
                foreach (var fa in faProp.EnumerateArray())
                {
                    var s = fa.GetString();
                    if (!string.IsNullOrEmpty(s))
                        focusAreas.Add(s);
                }
            }

            // Extract selected_alignments
            List<string>? selectedAlignments = null;
            if (root.TryGetProperty("selected_alignments", out var saProp) && saProp.ValueKind == JsonValueKind.Array)
            {
                selectedAlignments = new List<string>();
                foreach (var sa in saProp.EnumerateArray())
                {
                    var s = sa.GetString();
                    if (!string.IsNullOrEmpty(s))
                        selectedAlignments.Add(s);
                }
            }

            // Extract selected_profiles (per-design-profile checkboxes)
            List<string>? selectedProfiles = null;
            if (root.TryGetProperty("selected_profiles", out var spProp) && spProp.ValueKind == JsonValueKind.Array)
            {
                selectedProfiles = new List<string>();
                foreach (var sp in spProp.EnumerateArray())
                {
                    var s = sp.GetString();
                    if (!string.IsNullOrEmpty(s))
                        selectedProfiles.Add(s);
                }
            }

            // Extract road_type_overrides
            Dictionary<string, string>? roadTypeOverrides = null;
            if (root.TryGetProperty("road_type_overrides", out var rtProp) && rtProp.ValueKind == JsonValueKind.Object)
            {
                roadTypeOverrides = new Dictionary<string, string>();
                foreach (var prop in rtProp.EnumerateObject())
                {
                    var val = prop.Value.GetString() ?? "";
                    if (!string.IsNullOrEmpty(val))
                        roadTypeOverrides[prop.Name] = val;
                }
                if (roadTypeOverrides.Count == 0) roadTypeOverrides = null;
            }

            // Extract road_classification_overrides
            Dictionary<string, string>? roadClassificationOverrides = null;
            if (root.TryGetProperty("road_classification_overrides", out var rcProp) && rcProp.ValueKind == JsonValueKind.Object)
            {
                roadClassificationOverrides = new Dictionary<string, string>();
                foreach (var prop in rcProp.EnumerateObject())
                {
                    var val = prop.Value.GetString() ?? "";
                    if (!string.IsNullOrEmpty(val))
                        roadClassificationOverrides[prop.Name] = val;
                }
                if (roadClassificationOverrides.Count == 0) roadClassificationOverrides = null;
            }

            // Extract cross_section_overrides (added 2026-05-04 per engineer feedback).
            Dictionary<string, string>? crossSectionOverrides = null;
            if (root.TryGetProperty("cross_section_overrides", out var csProp) && csProp.ValueKind == JsonValueKind.Object)
            {
                crossSectionOverrides = new Dictionary<string, string>();
                foreach (var prop in csProp.EnumerateObject())
                {
                    var val = prop.Value.GetString() ?? "";
                    if (!string.IsNullOrEmpty(val))
                        crossSectionOverrides[prop.Name] = val;
                }
                if (crossSectionOverrides.Count == 0) crossSectionOverrides = null;
            }

            // Extract topography (project-wide scalar)
            string? topography = null;
            if (root.TryGetProperty("topography", out var topoProp) && topoProp.ValueKind == JsonValueKind.String)
            {
                var t = topoProp.GetString();
                if (!string.IsNullOrEmpty(t)) topography = t;
            }

            if (_vm.UseWebSocket && _vm.WsClient != null && _vm.WsClient.IsConnected)
            {
                var analyzeTab = _vm.ActiveTab;
                var analyzeSessionId = analyzeTab?.SessionId;
                if (analyzeTab == null || string.IsNullOrEmpty(analyzeSessionId))
                {
                    AddSystemMessage("שגיאה: אין session פעיל לטאב הזה.");
                    return;
                }
                analyzeTab.IsSending = true;
                analyzeTab.CancelRequested = false;
                analyzeTab.SendStartedAt = DateTime.Now;
                EnableInput(false);
                ShowStopButton(true);

                // New analysis supersedes the previous one's pins — clear now; the fresh
                // findings table re-pins when this stream ends.
                ClearMarkersForNewTurn();

                StartAssistantStreamingMessage();

                // Yield so WebView2 can paint the thinking bubble before we
                // tie up the UI thread with drawing-summary extraction. Without
                // this delay engineers reported a long blank gap between
                // pressing "ניתוח" and seeing any feedback.
                await Task.Delay(50);

                // Re-extract the drawing summary right before sending so any
                // entity added after the session was first opened (new
                // alignment, corridor, etc.) is visible to the backend. The
                // backend overwrites its stored session summary when this
                // field is non-null. Falls back to whatever the last scan
                // produced (LastDwgJson) if the fresh extraction fails.
                // This is the analyze-side full extraction — must include
                // intersections (the scope-dialog one above skips them).
                object? freshSummary = null;
                try
                {
                    var freshJson = await Task.Run(() =>
                    {
                        return Dispatcher.Invoke(() =>
                        {
                            var extractor = new DrawingSummaryExtractor();
                            return extractor.ExtractSummaryAsJson(includeIntersections: true);
                        });
                    });
                    if (!string.IsNullOrEmpty(freshJson))
                    {
                        _vm.LastDwgJson = freshJson;
                        using var doc = JsonDocument.Parse(freshJson);
                        freshSummary = doc.RootElement.Clone();
                    }
                    else if (!string.IsNullOrEmpty(_vm.LastDwgJson))
                    {
                        using var doc = JsonDocument.Parse(_vm.LastDwgJson);
                        freshSummary = doc.RootElement.Clone();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] Summary refresh before analyze failed: {ex.Message}");
                }

                await _vm.WsClient.AnalyzeAsync(
                    analyzeSessionId!,
                    "",
                    focusAreas,
                    selectedAlignments,
                    selectedProfiles,
                    roadTypeOverrides,
                    roadClassificationOverrides,
                    crossSectionOverrides,
                    topography,
                    freshSummary);
            }
        }

        /// <summary>
        /// Parse entity counts from drawing summary JSON for scope dialog.
        /// </summary>
        private static EntityCounts ParseEntityCounts(string? json)
        {
            var counts = new EntityCounts();
            if (string.IsNullOrEmpty(json))
                return counts;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("alignments", out var alignments) && alignments.ValueKind == JsonValueKind.Array)
                {
                    counts.Alignments = alignments.GetArrayLength();

                    var details = new List<AlignmentScopeInfo>();
                    foreach (var a in alignments.EnumerateArray())
                    {
                        var info = new AlignmentScopeInfo();
                        if (a.TryGetProperty("name", out var nameProp))
                            info.Name = nameProp.GetString() ?? "";
                        if (a.TryGetProperty("length", out var lenProp) && lenProp.TryGetDouble(out var len))
                            info.Length = len;
                        if (!string.IsNullOrEmpty(info.Name))
                            details.Add(info);
                    }
                    if (details.Count > 0)
                        counts.AlignmentDetails = details;
                }

                if (root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                {
                    var profDetails = new List<ProfileScopeInfo>();
                    foreach (var p in profiles.EnumerateArray())
                    {
                        var isDesign = false;
                        if (p.TryGetProperty("is_design_profile", out var designProp))
                            isDesign = designProp.GetBoolean();
                        if (!isDesign && p.TryGetProperty("profile_type", out var typeProp))
                        {
                            var ptype = (typeProp.GetString() ?? "").ToLowerInvariant();
                            isDesign = ptype == "design" || ptype == "layout";
                        }
                        if (!isDesign && p.TryGetProperty("type", out var rawTypeProp))
                        {
                            var ptype = (rawTypeProp.GetString() ?? "").ToLowerInvariant();
                            isDesign = ptype == "design" || ptype == "layout";
                        }
                        if (isDesign)
                        {
                            counts.DesignProfiles++;

                            var info = new ProfileScopeInfo();
                            if (p.TryGetProperty("name", out var pNameProp))
                                info.Name = pNameProp.GetString() ?? "";
                            if (p.TryGetProperty("alignment_name", out var alnProp))
                                info.AlignmentName = alnProp.GetString() ?? "";
                            if (!string.IsNullOrEmpty(info.Name))
                                profDetails.Add(info);
                        }
                    }
                    if (profDetails.Count > 0)
                        counts.ProfileDetails = profDetails;
                }

                if (root.TryGetProperty("signs", out var signs) && signs.ValueKind == JsonValueKind.Array)
                    counts.Signs = signs.GetArrayLength();

                if (root.TryGetProperty("markings", out var markings) && markings.ValueKind == JsonValueKind.Array)
                    counts.Markings = markings.GetArrayLength();

                if (root.TryGetProperty("corridors", out var corridors) && corridors.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in corridors.EnumerateArray())
                    {
                        var isRamp = false;
                        if (c.TryGetProperty("corridor_type", out var ctProp))
                            isRamp = ctProp.GetString() == "Ramp";
                        if (isRamp)
                            counts.Ramps++;
                        else
                            counts.Corridors++;
                    }
                }

                if (root.TryGetProperty("pipe_networks", out var pipeNetworks) && pipeNetworks.ValueKind == JsonValueKind.Array)
                    counts.PipeNetworks = pipeNetworks.GetArrayLength();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ParseEntityCounts error: {ex.Message}");
            }

            return counts;
        }

        #endregion

        #region Conversation Rendering

        private void AddSystemMessage(string message)
        {
            string ts = DateTime.Now.ToString("HH:mm");
            string safe = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br/>");

            _vm.ConversationHtml.Append($@"
<div class='message system-message'>
  <div class='message-content system-content'>
    <span class='system-icon'>ℹ️</span>
    <span>{safe}</span>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");

            UpdateBrowser();
        }

        private void AddUserMessage(string message)
        {
            _vm.HasUserSentMessage = true;
            // Remembered only so per-message feedback (v1.14) on an answer the
            // agent never logged as a question still records WHAT was asked.
            if (_vm.ActiveTab != null)
                _vm.ActiveTab.LastUserMessage = message;

            string ts = DateTime.Now.ToString("HH:mm");
            string safe = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br/>");

            _vm.ConversationHtml.Append($@"
<div class='message user-message'>
  <div class='avatar user-avatar'>👤</div>
  <div class='bubble user-bubble'>
    <div class='bubble-content' dir='auto'>{safe}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");

            UpdateBrowser();
        }

        /// <summary>
        /// User bubble showing the question plus thumbnails of the images
        /// attached to it (PROTOCOL v1.16).
        /// </summary>
        /// <remarks>
        /// Thumbnails are referenced through the <c>mahod-attachments</c> virtual
        /// host, NOT inlined as data: URIs. The whole transcript is re-serialised
        /// into one string and re-navigated on every update, and
        /// <c>NavigateToString</c> caps out around 2MB — a couple of inlined
        /// screenshots would blow that and blank the chat.
        /// </remarks>
        private void AddUserAttachmentMessage(
            string message,
            List<Utilities.PendingImage> images,
            List<Utilities.PendingDocument> documents)
        {
            _vm.HasUserSentMessage = true;
            if (_vm.ActiveTab != null)
                _vm.ActiveTab.LastUserMessage = message;

            string ts = DateTime.Now.ToString("HH:mm");
            string safe = System.Net.WebUtility.HtmlEncode(message).Replace("\n", "<br/>");

            var thumbs = new StringBuilder();
            // Documents have no preview to render, so they show as a named chip.
            foreach (var doc in documents)
            {
                var label = System.Net.WebUtility.HtmlEncode(doc.Name);
                thumbs.Append($"<span class='attached-doc' title='{label}'>📕 {label}</span>");
            }
            foreach (var img in images)
            {
                if (string.IsNullOrEmpty(img.PreviewPath)) continue;
                var url = "https://mahod-attachments/" +
                          Uri.EscapeDataString(System.IO.Path.GetFileName(img.PreviewPath));
                var alt = System.Net.WebUtility.HtmlEncode(img.Name);
                thumbs.Append(
                    $"<img class='attached-image' src='{url}' alt='{alt}' title='{alt}' />");
            }

            string block = thumbs.Length > 0
                ? $"<div class='attached-images'>{thumbs}</div>"
                : string.Empty;

            _vm.ConversationHtml.Append($@"
<div class='message user-message'>
  <div class='avatar user-avatar'>👤</div>
  <div class='bubble user-bubble'>
    <div class='bubble-content' dir='auto'>{safe}</div>
    {block}
  </div>
  <div class='timestamp'>{ts}</div>
</div>");

            UpdateBrowser();
        }

        private void AddAssistantMessage(string message)
        {
            string ts = DateTime.Now.ToString("HH:mm");
            string body = ChatHtmlRenderer.ConvertMarkdownToHtml(message);

            _vm.ConversationHtml.Append($@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{body}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");

            _vm.LastReportHtml = body;
            UpdateBrowser();
        }

        private void AddAssistantHtmlMessage(string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent))
                return;

            string ts = DateTime.Now.ToString("HH:mm");
            string body = ChatHtmlRenderer.ExtractBodyContent(htmlContent);

            // Convert markdown when the content isn't HTML, OR when it still carries
            // raw markdown markers (mixed markdown+HTML). Otherwise we still run the
            // inline-only pass so any **bold** / _italic_ / `code` that survived in
            // genuinely-HTML payloads still renders correctly instead of leaking
            // literal asterisks (the agent's fix-card summaries hit this path).
            if (!string.IsNullOrWhiteSpace(body) &&
                (!ChatHtmlRenderer.ContentLooksLikeHtml(body) || ChatHtmlRenderer.HasMarkdownFormatting(body)))
            {
                body = ChatHtmlRenderer.ConvertMarkdownToHtml(body);
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                body = ChatHtmlRenderer.ApplyInlineMarkdown(body);
            }

            _vm.ConversationHtml.Append($@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{body}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>");

            _vm.LastReportHtml = body;
            UpdateBrowser();
        }

        private void StartAssistantStreamingMessage()
        {
            string labelId = "think-l-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            // Animated WebP with real transparency — loops natively in an <img>,
            // no autoplay policy involved (the old <video> MP4 had a baked-in
            // white background that glowed on the dark theme).
            string thinkSrc = string.IsNullOrEmpty(_thinkingShortDataUri) ? "" : _thinkingShortDataUri;
            string thinkImg = string.IsNullOrEmpty(thinkSrc)
                ? ""
                : $"<img class='thinking-video' src='{thinkSrc}' alt=''/>";

            _vm.ConversationHtml.Append($@"
<div class='message assistant-message assistant-stream-block'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content assistant-stream-content'>
      <div class='thinking-indicator'>
        {thinkImg}
        <span id='{labelId}' class='thinking-label'>חושב<span class='td d1'>.</span><span class='td d2'>.</span><span class='td d3'>.</span></span>
      </div>
      <div class='thinking-steps' dir='auto'></div>
      <div class='tw-text' dir='auto'></div>
      <script>
        (function(){{
          // After 10s of still-thinking, soften the label.
          setTimeout(function(){{
            var lbl = document.getElementById('{labelId}');
            if (lbl && lbl.isConnected && lbl.firstChild) {{
              lbl.firstChild.nodeValue = 'עדיין חושב';
            }}
          }}, 10000);
        }})();
      </script>
    </div>
  </div>
</div>");

            UpdateBrowser();
            // Belt-and-suspenders: the document-created script re-inits __tw on every
            // load, but reset explicitly in case this stream block is created without a
            // fresh navigation.
            _ = ChatBrowser.ExecuteScriptAsync("window.twReset && window.twReset();");
        }

        private void AppendStreamingAssistantChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk) || !_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;

            string safe = System.Net.WebUtility.HtmlEncode(chunk).Replace("\n", "<br/>");

            string script = $@"
(function(){{
  var blocks = document.getElementsByClassName('assistant-stream-content');
  if(!blocks || blocks.length === 0) return;
  var target = blocks[blocks.length - 1];
  if(!target) return;
  var typing = target.querySelector('.typing-indicator');
  var typingHtml = typing ? typing.outerHTML : '';
  if(typing) typing.remove();
  target.innerHTML = target.innerHTML + '{safe}' + typingHtml;
  if(window.smartScroll) window.smartScroll();
}})();";

            _ = ChatBrowser.ExecuteScriptAsync(script);
        }

        private void UpdateStreamingContentWithMarkdown(string fullContent)
        {
            if (string.IsNullOrEmpty(fullContent) || !_vm.IsWebViewInitialized || ChatBrowser.CoreWebView2 == null)
                return;

            string htmlContent = ChatHtmlRenderer.ConvertMarkdownToHtml(HideInternalMarkers(fullContent));

            string safeHtml = htmlContent
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            string script = $@"
(function(){{
  var blocks = document.getElementsByClassName('assistant-stream-content');
  if(!blocks || blocks.length === 0) return;
  var target = blocks[blocks.length - 1];
  if(!target) return;
  var typing = target.querySelector('.typing-indicator');
  var typingHtml = typing ? typing.outerHTML : '';
  if(typing) typing.remove();
  target.innerHTML = '{safeHtml}' + typingHtml;
  if(window.smartScroll) window.smartScroll();
}})();";

            _ = ChatBrowser.ExecuteScriptAsync(script);
        }

        private void ReplaceStreamingBlockWithPlainText(
            string content,
            List<RagReference>? references = null,
            List<LispRecommendation>? lispRecommendations = null)
        {
            string ts = DateTime.Now.ToString("HH:mm");
            // Hide internal protocol markers from displayed text
            string displayContent = HideInternalMarkers(content);
            string body = ChatHtmlRenderer.ConvertMarkdownToHtml(displayContent);
            string sourcesHtml = ChatHtmlRenderer.BuildReferencesHtml(references);
            string approvalButtons = BuildApprovalButtonsIfNeeded(content);

            string html = $@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{body}{approvalButtons}{sourcesHtml}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>";

            ReplaceLastAssistantStreamBlock(html);
        }

        /// <summary>
        /// Build HTML for RAG reference sources section.
        /// </summary>
        private static string BuildReferencesHtml(List<RagReference>? references)
        {
            if (references == null || references.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.Append("<div style='margin-top:12px; padding-top:10px; border-top:1px solid var(--border-color);'>");
            sb.Append("<div style='font-size:12px; font-weight:600; color:var(--accent-green); margin-bottom:6px;'>📚 מקורות</div>");
            sb.Append("<ul style='list-style:none; padding:0; margin:0; font-size:12px;'>");

            foreach (var r in references)
            {
                var docName = System.Net.WebUtility.HtmlEncode(
                    !string.IsNullOrEmpty(r.DocumentName) ? r.DocumentName
                    : !string.IsNullOrEmpty(r.Filename) ? r.Filename
                    : "מסמך לא ידוע"
                );
                var simPct = (int)(r.Similarity * 100);
                var simBadge = simPct > 0
                    ? $" <span style='color:var(--text-muted); font-size:11px;'>({simPct}%)</span>"
                    : "";
                var chunkInfo = r.ChunksUsed > 1
                    ? $" <span style='color:var(--text-muted); font-size:11px;'>[{r.ChunksUsed} קטעים]</span>"
                    : "";

                if (!string.IsNullOrEmpty(r.Url))
                {
                    if (r.Pages.Count > 0)
                    {
                        var pageLinks = r.Pages.Select(p =>
                        {
                            var pageUrl = System.Net.WebUtility.HtmlEncode($"{r.Url}#page={p}");
                            return $"<a href='{pageUrl}' style='color:#2e9535; text-decoration:none;'>עמ' {p}</a>";
                        });
                        sb.Append($"<li style='margin-bottom:3px;'>📄 <a href='{System.Net.WebUtility.HtmlEncode(r.Url)}' style='color:#2e9535; text-decoration:none;'>{docName}</a> ({string.Join(", ", pageLinks)}){simBadge}{chunkInfo}</li>");
                    }
                    else
                    {
                        sb.Append($"<li style='margin-bottom:3px;'>📄 <a href='{System.Net.WebUtility.HtmlEncode(r.Url)}' style='color:#2e9535; text-decoration:none;'>{docName}</a>{simBadge}{chunkInfo}</li>");
                    }
                }
                else
                {
                    sb.Append($"<li style='margin-bottom:3px;'>📄 {docName}{simBadge}{chunkInfo}</li>");
                }
            }

            sb.Append("</ul></div>");
            return sb.ToString();
        }

        /// <summary>
        /// If message contains approval prompt (מחכה לאישור), add clickable buttons.
        /// </summary>
        /// <summary>
        /// Remove internal protocol markers (ASSEMBLY_SELECT:...) from displayed text.
        /// The markers are still parsed for button creation but hidden from the user.
        /// </summary>
        // Inline [RAG-N] citation tags the chat LLM is prompted to emit. The
        // server means to resolve them to clickable links via a content_replace
        // event, but the WS layer drops that event, so the raw tags otherwise
        // reach the user verbatim (e.g. "...מטר [RAG-1]."). Sources are already
        // shown separately in the 📚 footer, so strip the inline tags entirely.
        // Matches numeric ([RAG-1]), the literal template ([RAG-N]) and empty
        // ([RAG-] / [RAG- ]) forms, consuming one leading space so surrounding
        // words don't end up double-spaced.
        private static readonly System.Text.RegularExpressions.Regex _RagCitationTagRe =
            new System.Text.RegularExpressions.Regex(@"[ \t]?\[RAG-(?:\d+|[Nn]|\s*)\]");

        private static string HideInternalMarkers(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            // Remove ASSEMBLY_SELECT:... line and the label line before it
            var lines = content.Split('\n').ToList();
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (lines[i].TrimStart().StartsWith("ASSEMBLY_SELECT:"))
                {
                    lines.RemoveAt(i);
                    // Also remove "בחר Assembly מהרשימה:" line if it's right before
                    if (i > 0 && lines[i - 1].Contains("בחר Assembly"))
                        lines.RemoveAt(i - 1);
                }
            }
            string joined = string.Join("\n", lines);
            // Strip inline [RAG-N] citation tags (see _RagCitationTagRe).
            joined = _RagCitationTagRe.Replace(joined, "");
            return joined;
        }

        private static string BuildApprovalButtonsIfNeeded(string content)
        {
            if (!content.Contains("מחכה לאישור") && !content.Contains("await_input") &&
                !content.Contains("אשר") && !content.Contains("מוכן") &&
                !content.Contains("ASSEMBLY_SELECT:"))
                return string.Empty;

            // Don't add buttons to final summary/completion messages
            // (but allow buttons on intermediate steps that also contain "נוצרו בהצלחה")
            if (content.Contains("דוח תכנון") ||
                (content.Contains("סיכום:") && content.Contains("Corridor נוצר בהצלחה")))
                return string.Empty;

            // Assembly selection — parse ASSEMBLY_SELECT:name1,name2 marker
            const string ASSEMBLY_MARKER = "ASSEMBLY_SELECT:";
            int markerIdx = content.IndexOf(ASSEMBLY_MARKER);
            if (markerIdx >= 0)
            {
                int startIdx = markerIdx + ASSEMBLY_MARKER.Length;
                // Read until end of line
                int endIdx = content.IndexOf('\n', startIdx);
                if (endIdx < 0) endIdx = content.Length;
                var namesCsv = content.Substring(startIdx, endIdx - startIdx).Trim();
                var names = namesCsv.Split(',')
                    .Select(n => n.Trim())
                    .Where(n => n.Length > 0)
                    .ToList();

                if (names.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append("<div style='margin-top:14px; display:flex; gap:8px; flex-wrap:wrap; justify-content:flex-end;'>");
                    foreach (var asmName in names)
                    {
                        var escaped = System.Net.WebUtility.HtmlEncode(asmName);
                        var jsEscaped = asmName.Replace("'", "\\'").Replace("\"", "\\\"");
                        sb.Append($@"
  <button onclick=""window.chrome.webview.postMessage(JSON.stringify({{action:'send_chat', text:'{jsEscaped}'}}))""
    style='padding:8px 20px; border:none; border-radius:8px; background:#3b82f6; color:white;
           font-size:14px; font-weight:600; cursor:pointer; transition:background 0.2s;'
    onmouseover=""this.style.background='#2563eb'"" onmouseout=""this.style.background='#3b82f6'"">
    🔧 {escaped}
  </button>");
                    }
                    sb.Append("</div>");
                    return sb.ToString();
                }
            }

            // Assembly ready prompt — show "Assembly מוכן" button
            if (content.Contains("Assembly מוכן") && content.Contains("Assembly"))
            {
                return @"
<div style='margin-top:14px; display:flex; gap:8px; flex-wrap:wrap; justify-content:flex-end;'>
  <button onclick=""window.chrome.webview.postMessage(JSON.stringify({action:'send_chat', text:'Assembly מוכן'}))""
    style='padding:8px 20px; border:none; border-radius:8px; background:#3b82f6; color:white;
           font-size:14px; font-weight:600; cursor:pointer; transition:background 0.2s;'
    onmouseover=""this.style.background='#2563eb'"" onmouseout=""this.style.background='#3b82f6'"">
    🔧 Assembly מוכן
  </button>
  <button onclick=""window.chrome.webview.postMessage(JSON.stringify({action:'send_chat', text:'בטל'}))""
    style='padding:8px 20px; border:none; border-radius:8px; background:#ef4444; color:white;
           font-size:14px; font-weight:600; cursor:pointer; transition:background 0.2s;'
    onmouseover=""this.style.background='#dc2626'"" onmouseout=""this.style.background='#ef4444'"">
    ❌ בטל
  </button>
</div>";
            }

            // Standard approval prompt — show אשר/בטל buttons
            if (!content.Contains("בטל"))
                return string.Empty;

            return @"
<div style='margin-top:14px; display:flex; gap:8px; flex-wrap:wrap; justify-content:flex-end;'>
  <button onclick=""window.chrome.webview.postMessage(JSON.stringify({action:'send_chat', text:'אשר'}))""
    style='padding:8px 20px; border:none; border-radius:8px; background:#22c55e; color:white;
           font-size:14px; font-weight:600; cursor:pointer; transition:background 0.2s;'
    onmouseover=""this.style.background='#16a34a'"" onmouseout=""this.style.background='#22c55e'"">
    ✅ אשר
  </button>
  <button onclick=""window.chrome.webview.postMessage(JSON.stringify({action:'send_chat', text:'בטל'}))""
    style='padding:8px 20px; border:none; border-radius:8px; background:#ef4444; color:white;
           font-size:14px; font-weight:600; cursor:pointer; transition:background 0.2s;'
    onmouseover=""this.style.background='#dc2626'"" onmouseout=""this.style.background='#ef4444'"">
    ❌ בטל
  </button>
</div>";
        }

        private void ReplaceStreamingBlockWithHtml(string htmlContent)
        {
            string ts = DateTime.Now.ToString("HH:mm");
            string body = ChatHtmlRenderer.ExtractBodyContent(htmlContent);

            if (!string.IsNullOrWhiteSpace(body) &&
                (!ChatHtmlRenderer.ContentLooksLikeHtml(body) || ChatHtmlRenderer.HasMarkdownFormatting(body)))
            {
                body = ChatHtmlRenderer.ConvertMarkdownToHtml(body);
            }
            else if (!string.IsNullOrWhiteSpace(body))
            {
                // Server payload looks like HTML but may still carry inline
                // markdown markers — make sure ** / _ / ` always render.
                body = ChatHtmlRenderer.ApplyInlineMarkdown(body);
            }

            string html = $@"
<div class='message assistant-message'>
  <div class='avatar assistant-avatar'>{BotAvatarHtml(32)}</div>
  <div class='bubble assistant-bubble'>
    <div class='bubble-content response-content' dir='auto'>{body}</div>
  </div>
  <div class='timestamp'>{ts}</div>
</div>";

            ReplaceLastAssistantStreamBlock(html);
        }

        private void ReplaceLastAssistantStreamBlock(string newInnerHtml)
        {
            ReplaceLastAssistantStreamBlockNoReload(newInnerHtml);
            UpdateBrowser();
        }

        /// <summary>
        /// Same balancing logic as <see cref="ReplaceLastAssistantStreamBlock"/> but does
        /// NOT reload the page. Used when the live DOM is updated separately (e.g. the
        /// typewriter's in-place swap at stream end), so the buffer stays consistent
        /// without a NavigateToString that would interrupt the animation.
        /// </summary>
        private void ReplaceLastAssistantStreamBlockNoReload(string newInnerHtml)
        {
            string all = _vm.ConversationHtml.ToString();
            int marker = all.LastIndexOf("assistant-stream-block", StringComparison.Ordinal);
            if (marker == -1)
            {
                _vm.ConversationHtml.Append(newInnerHtml);
                return;
            }

            int start = all.LastIndexOf("<div", marker, StringComparison.Ordinal);
            if (start == -1)
            {
                _vm.ConversationHtml.Append(newInnerHtml);
                return;
            }

            int depth = 0;
            int pos = start;
            int end = -1;

            while (pos < all.Length)
            {
                int open = all.IndexOf("<div", pos, StringComparison.Ordinal);
                int close = all.IndexOf("</div>", pos, StringComparison.Ordinal);

                if (open != -1 && open < close)
                {
                    depth++;
                    pos = open + 4;
                }
                else if (close != -1)
                {
                    depth--;
                    pos = close + 6;
                    if (depth == 0)
                    {
                        end = pos;
                        break;
                    }
                }
                else
                {
                    break;
                }
            }

            if (end != -1)
            {
                all = all.Remove(start, end - start).Insert(start, newInnerHtml);
                _vm.ConversationHtml.Clear();
                _vm.ConversationHtml.Append(all);
            }
            else
            {
                _vm.ConversationHtml.Append(newInnerHtml);
            }
        }

        private void ShowLoader(string? text = null)
        {
            if (!_vm.IsWebViewInitialized)
                return;

            RemoveLoader();

            _vm.CurrentLoaderId = "loader_" + Guid.NewGuid().ToString("N");

            _vm.ConversationHtml.Append($@"
<div id='{_vm.CurrentLoaderId}' class='loader-message'>
  <div class='typing-indicator'>
    <span></span><span></span><span></span>
  </div>
</div>");

            UpdateBrowser();
        }

        private void RemoveLoader()
        {
            if (string.IsNullOrEmpty(_vm.CurrentLoaderId))
                return;

            string all = _vm.ConversationHtml.ToString();
            string loaderStart = $"<div id='{_vm.CurrentLoaderId}'";
            int startIdx = all.IndexOf(loaderStart, StringComparison.Ordinal);
            if (startIdx != -1)
            {
                int depth = 0;
                int pos = startIdx;
                int endIdx = -1;

                while (pos < all.Length)
                {
                    int openDiv = all.IndexOf("<div", pos, StringComparison.Ordinal);
                    int closeDiv = all.IndexOf("</div>", pos, StringComparison.Ordinal);

                    if (openDiv != -1 && (closeDiv == -1 || openDiv < closeDiv))
                    {
                        depth++;
                        pos = openDiv + 4;
                    }
                    else if (closeDiv != -1)
                    {
                        depth--;
                        pos = closeDiv + 6;
                        if (depth == 0)
                        {
                            endIdx = pos;
                            break;
                        }
                    }
                    else
                    {
                        break;
                    }
                }

                if (endIdx != -1)
                {
                    _vm.ConversationHtml.Clear();
                    _vm.ConversationHtml.Append(all.Remove(startIdx, endIdx - startIdx));
                }
            }

            if (_vm.IsWebViewInitialized && ChatBrowser.CoreWebView2 != null)
            {
                string script = $@"
(function(){{
  var el = document.getElementById('{_vm.CurrentLoaderId}');
  if(el && el.parentNode) el.parentNode.removeChild(el);
}})();";
                _ = ChatBrowser.ExecuteScriptAsync(script);
            }

            _vm.CurrentLoaderId = null;
        }

        #endregion

        #region Browser Update

        private async void UpdateBrowser()
        {
            // Ensure we're on the UI thread — WebView2 requires it
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => UpdateBrowser());
                return;
            }

            if (!_vm.IsWebViewInitialized)
                return;

            string messages = _vm.ConversationHtml.ToString();

            // v1.14 feedback state for the messages actually on this page. C# is
            // the source of truth (NavigateToString throws the DOM away on every
            // update), so the reaction/feedback the engineer already gave is
            // re-painted from here instead of being lost. Filtered by what the
            // HTML references so a long session doesn't ship dead entries.
            string feedbackStateJson = BuildFeedbackStateJson(messages);

            // The welcome-icon (bot-avatar at 80px) was removed: the full
            // 'מהוד הנדסה · עוזר AI' banner now lives in the WPF header
            // (see LoadHeaderLogo / NewChatControl.xaml), so a second mascot
            // here read as a duplicate logo to engineers.
            string welcomeHtml = _vm.HasUserSentMessage ? "" : $@"
<div class='welcome-container'>
  <h1 class='welcome-title'>ברוכים הבאים ל-MahodAI</h1>
  <p class='welcome-subtitle'>מערכת AI חכמה להנדסת תחבורה ותשתיות</p>
  <div class='suggestions-container'>
    <button class='suggestion-chip' onclick=""window.chrome.webview.postMessage('מה הרדיוס האופקי המינימלי למהירות תכן 80 קמש?')"">מה הרדיוס האופקי המינימלי למהירות תכן 80 קמש?</button>
    <button class='suggestion-chip' onclick=""window.chrome.webview.postMessage('מה השיפוע המרבי לאורך בזרועות צומת?')"">מה השיפוע המרבי לאורך בזרועות צומת?</button>
    <button class='suggestion-chip' onclick=""window.chrome.webview.postMessage('מה אורך עקומת המעבר הנדרשת לרדיוס 300 מטר?')"">מה אורך עקומת המעבר הנדרשת לרדיוס 300 מטר?</button>
    <button class='suggestion-chip' onclick=""window.chrome.webview.postMessage('מהו מרחק הראות לעצירה למהירות 100 קמש?')"">מהו מרחק הראות לעצירה למהירות 100 קמש?</button>
    <button class='suggestion-chip' onclick=""window.chrome.webview.postMessage('מה הרדיוס המזערי לקיום צומת בעקום אופקי?')"">מה הרדיוס המזערי לקיום צומת בעקום אופקי?</button>
  </div>
</div>";

            string cssVars = _vm.IsDarkMode
                ? @":root {
      /* Tell Chromium the page is dark so native chrome (scrollbars, form
         controls, <select> popups) renders dark instead of glaring white. */
      color-scheme: dark;
      --bg-primary: #0F1A1F;
      --bg-secondary: #17242B;
      --bg-card: #1C2C33;
      /* Brightened text tier so prose, secondary copy and timestamps all read
         clearly on the dark slate panels — the previous primary was off-white
         enough but secondary/muted were slipping into the unreadable range. */
      --text-primary: #F8FAFC;
      --text-secondary: #DCE5E9;
      --text-muted: #B3BFC5;
      --border-color: #3A4D58;
      --accent-green: #4FBF65;
      --accent-green-dark: #A6D8B1;
      /* green-tint MUST be lighter than --bg-card (#1C2C33) so any
         green-tinted hover/selected surface reads as a BRIGHTER selection,
         not a darker one. Previous value #1B3A28 was darker than the card
         and made suggestion-chip hover look dimmer than the rest of the UI. */
      --green-tint: #2E6B3F;
      --green-tint-border: #3E7A4F;
      --user-grad-1: #33454C;
      --user-grad-2: #1B2A30;
      --accent-blue: #D4EEDA;
      --shadow-soft: 0 8px 24px rgba(0,0,0,0.30);
      --shadow-card: 0 14px 36px rgba(0,0,0,0.35);
    }"
                : @":root {
      color-scheme: light;
      --bg-primary: #EDF1F1;
      --bg-secondary: #F2F5F4;
      --bg-card: #FFFFFF;
      --text-primary: #15232B;
      --text-secondary: #6B7C82;
      --text-muted: #9AA8AD;
      --border-color: #E3E9E8;
      --accent-green: #2EA64C;
      --accent-green-dark: #1C7C38;
      --green-tint: #EAF7EE;
      --green-tint-border: #CFE9D6;
      --user-grad-1: #33454C;
      --user-grad-2: #1B2A30;
      --accent-blue: #1B2A30;
      --shadow-soft: 0 8px 24px rgba(20,40,45,0.06);
      --shadow-card: 0 14px 36px rgba(20,40,45,0.07);
    }";

            string tabStripHtml = ChatHtmlRenderer.BuildTabStripHtml(_vm.DrawingTabs, _vm.ActiveTabKey);

            // Faint centered brand-mark watermark behind the conversation (reference look).
            // Skipped when the logo data URI is unavailable so we never emit url('').
            string watermarkCss = string.IsNullOrEmpty(_botAvatarDataUri)
                ? ""
                : $@"body::before {{
      content: '';
      position: fixed;
      inset: 0;
      background: url('{_botAvatarDataUri}') no-repeat center 42%;
      background-size: min(46vw, 300px) auto;
      opacity: 0.05;
      pointer-events: none;
      z-index: 0;
    }}";

            // The company logo banner was moved out of the chat body and into the
            // WPF header (see LoadHeaderLogo / NewChatControl.xaml), so it is no
            // longer emitted here.

            string fullHtml = $@"<!DOCTYPE html>
<html dir='rtl' lang='he'>
<head>
  <meta charset='UTF-8'>
  <link href='https://fonts.googleapis.com/css2?family=Heebo:wght@400;500;600;700&family=JetBrains+Mono:wght@400;500&display=swap' rel='stylesheet'>
  <link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.css'>
  <script src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/katex.min.js'></script>
  <script src='https://cdn.jsdelivr.net/npm/katex@0.16.9/dist/contrib/auto-render.min.js'></script>
  <script>
    document.addEventListener('DOMContentLoaded', function() {{
      renderMathInElement(document.body, {{
        delimiters: [
          {{left: '$$', right: '$$', display: true}},
          {{left: '$', right: '$', display: false}},
          {{left: '\\\\[', right: '\\\\]', display: true}},
          {{left: '\\\\(', right: '\\\\)', display: false}}
        ],
        throwOnError: false
      }});
    }});
  </script>
  <style>
    {cssVars}
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}
    body {{
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      background-color: var(--bg-primary);
      color: var(--text-primary);
      /* No top padding — the tab-strip is the first element in body and
         is sticky at top; we don't want a 20px bg-primary gap above it.
         Side gutters are tight on the docked palette (typical 380–560 px)
         so the assistant bubble — and any table inside it — gets every
         spare pixel of horizontal room. */
      padding: 0 10px 20px 10px;
      line-height: 1.6;
      position: relative;
    }}
    @media (max-width: 420px) {{
      body {{ padding: 0 6px 16px 6px; }}
    }}
    {watermarkCss}

    /* Welcome */
    .welcome-container {{
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      min-height: 300px;
      text-align: center;
      padding: 40px 20px;
    }}
    .welcome-icon {{
      font-size: 64px;
      margin-bottom: 20px;
      animation: float 3s ease-in-out infinite;
    }}
    @keyframes float {{
      0%, 100% {{ transform: translateY(0); }}
      50% {{ transform: translateY(-10px); }}
    }}
    .welcome-title {{
      font-size: 28px;
      font-weight: 600;
      color: var(--accent-blue);
      margin-bottom: 10px;
    }}
    .welcome-subtitle {{
      font-size: 16px;
      color: var(--text-secondary);
      margin-bottom: 30px;
    }}

    /* Suggestions */
    .suggestions-container {{
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: 8px;
      margin-top: 10px;
    }}
    .suggestion-chip {{
      background: var(--bg-card);
      border: 1px solid var(--border-color);
      padding: 9px 16px;
      border-radius: 999px;
      font-size: 13px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      color: var(--text-secondary);
      cursor: pointer;
      box-shadow: 0 2px 8px rgba(20,40,45,0.04);
      transition: all 0.18s ease;
    }}
    .suggestion-chip:hover {{
      background: var(--green-tint);
      color: var(--accent-green-dark);
      border-color: var(--green-tint-border);
      transform: translateY(-2px);
      box-shadow: 0 6px 16px rgba(46,166,76,0.14);
    }}

    /* Messages — avatar sits beside the bubble; timestamp wraps to its own line.
       Assistant clusters on the right (RTL start), user on the left (row-reverse
       so the dark avatar lands on the outer/left edge), matching the reference. */
    #messages {{
      display: flex;
      flex-direction: column;
      gap: 18px;
      position: relative;
      z-index: 1;
      padding-top: 8px;
    }}
    .message {{
      display: flex;
      flex-wrap: wrap;
      align-items: flex-start;
      /* Tighter avatar↔bubble gap on the docked palette so the bubble
         (and any wide table inside) gets the spare pixels. */
      gap: 6px;
      max-width: 100%;
      animation: fadeIn 0.3s ease;
    }}
    @keyframes fadeIn {{
      from {{ opacity: 0; transform: translateY(10px); }}
      to {{ opacity: 1; transform: translateY(0); }}
    }}
    .message.assistant-message {{
      align-self: flex-start;
    }}
    /* Only answers that actually contain a table/report fill the panel width so the
       table has room. Plain answers — and ESPECIALLY the thinking/waiting bubble
       (just a small looping animation) — stay sized to their content instead of
       stretching into a huge empty card. :has() is already relied on by the
       responsive table rules below, so it is supported in this WebView2 runtime. */
    .message.assistant-message:has(table) {{
      width: 100%;
    }}
    .message.assistant-message:has(table) > .bubble {{
      flex: 1 1 auto;
      min-width: 0;
    }}
    .message.user-message {{
      align-self: flex-end;
      flex-direction: row-reverse;
      /* Keep user questions compact (sized to content), not stretched full width. */
      max-width: 88%;
    }}
    .message.system-message {{
      align-self: center;
      flex-direction: column;
      align-items: center;
      max-width: 100%;
    }}

    /* Avatar */
    .avatar {{
      width: 38px;
      height: 38px;
      border-radius: 12px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 18px;
      flex-shrink: 0;
      margin-top: 2px;
    }}
    .user-avatar {{
      background: linear-gradient(145deg, var(--user-grad-1), var(--user-grad-2));
      color: white;
      border-radius: 50%;
      box-shadow: 0 3px 10px rgba(27,42,48,0.25);
    }}
    .assistant-avatar {{
      background: var(--green-tint);
      border: 1px solid var(--green-tint-border);
      box-shadow: 0 2px 8px rgba(46,166,76,0.10);
    }}
    .assistant-avatar img {{
      width: 28px !important;
      height: 28px !important;
    }}

    /* Bubble */
    .bubble {{
      max-width: 100%;
      /* Tighter side padding so tables inside the bubble have more usable
         horizontal width on the narrow docked palette. */
      padding: 12px 12px;
      border-radius: 16px;
      position: relative;
      word-break: break-word;
    }}
    .user-bubble {{
      background: linear-gradient(145deg, var(--user-grad-1), var(--user-grad-2));
      color: white;
      box-shadow: 0 6px 18px rgba(27,42,48,0.20);
      border-top-left-radius: 5px;
    }}
    /* v1.16 attached-image thumbnails inside the user bubble. */
    .attached-images {{
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 8px;
    }}
    .attached-images img.attached-image {{
      max-width: 200px;
      max-height: 160px;
      border-radius: 8px;
      border: 1px solid rgba(255,255,255,0.35);
      display: block;
    }}
    /* v1.17 attached documents — no preview, so a named chip. */
    .attached-images .attached-doc {{
      display: inline-block;
      padding: 4px 10px;
      border-radius: 12px;
      background: rgba(255,255,255,0.18);
      border: 1px solid rgba(255,255,255,0.35);
      font-size: 12px;
      max-width: 220px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }}
    .assistant-bubble {{
      background: var(--bg-card);
      color: var(--text-primary);
      border: 1px solid var(--border-color);
      box-shadow: var(--shadow-soft);
      border-top-right-radius: 5px;
    }}

    /* Fix-plan / fix-result cards are authored with light inline colours (blue
       heading, grey text, light table header, WHITE inputs). Theme them to the
       active palette so in dark mode they read as a proper dark card (not a
       jarring white panel with white inputs). CSS variables + a few !important
       overrides beat the hard-coded inline styles; semantic colours (severity
       badges, accept/decline buttons) keep their own inline colours. */
    .fix-plan, .fix-result {{
      background: var(--bg-card);
      color: var(--text-primary);
      border: 1px solid var(--border-color);
      border-radius: 10px;
      padding: 12px 14px;
      margin: 6px 0;
    }}
    .fix-plan h3, .fix-result h3 {{ color: var(--accent-blue) !important; }}
    .fix-plan p, .fix-result p {{ color: var(--text-secondary) !important; }}
    /* header row (the only <tr> with an inline background) → themed strip */
    .fix-plan table tr[style*='background'],
    .fix-result table tr[style*='background'] {{ background: var(--bg-secondary) !important; }}
    .fix-plan td, .fix-plan th, .fix-result td, .fix-result th {{
      color: var(--text-primary) !important;
      border-color: var(--border-color) !important;
    }}
    /* editable value inputs/selects → dark field, not white */
    .fix-plan input:not([type='checkbox']), .fix-plan select,
    .fix-result input:not([type='checkbox']), .fix-result select {{
      background: var(--bg-secondary) !important;
      color: var(--text-primary) !important;
      border: 1px solid var(--border-color) !important;
    }}
    /* Residual light surfaces inside fix cards that the rules above miss:
       the inline-styled summary note box (#eef5ff), skipped-reason /
       engineer-input badges (#fffbeb) and the disabled / undo buttons. */
    .fix-plan div[style*='#eef5ff'], .fix-result div[style*='#eef5ff'] {{
      background: var(--bg-secondary) !important;
      color: var(--text-primary) !important;
    }}
    .fix-plan [style*='#fffbeb'], .fix-result [style*='#fffbeb'] {{
      background: var(--bg-secondary) !important;
      color: var(--text-secondary) !important;
    }}
    .fix-plan button[style*='#f3f4f6'], .fix-result button[style*='#f3f4f6'] {{
      background: var(--bg-secondary) !important;
    }}
    .fix-plan button[style*='background:#fff'], .fix-result button[style*='background:#fff'] {{
      background: var(--bg-card) !important;
    }}

    /* Scope-selection card (AnalysisScopeHtmlRenderer emits light inline
       colours — light headers, white selects, slate labels). Retint to the
       active palette exactly like .fix-plan above. */
    .scope-card {{
      background: var(--bg-card);
      color: var(--text-primary);
      border: 1px solid var(--border-color);
      border-radius: 10px;
      padding: 12px 14px;
      margin: 6px 0;
    }}
    .scope-card h3[style*='#1e40af'] {{ color: var(--accent-blue) !important; }}
    .scope-card [style*='color:#334155'] {{ color: var(--text-secondary) !important; }}
    .scope-card div[style*='#f8fafc'] {{
      background: var(--bg-secondary) !important;
      border-color: var(--border-color) !important;
    }}
    .scope-card tr[style*='background'] {{ background: var(--bg-secondary) !important; }}
    .scope-card th, .scope-card td {{ border-color: var(--border-color) !important; }}
    .scope-card select, .scope-card input:not([type='checkbox']) {{
      background: var(--bg-secondary) !important;
      color: var(--text-primary) !important;
      border: 1px solid var(--border-color) !important;
    }}

    /* Response Content */
    .response-content {{
      font-size: 14px;
      line-height: 1.7;
    }}
    .response-content h2, .response-content h3, .response-content h4, .response-content h5 {{
      color: var(--accent-blue);
      font-size: 15px;
      font-weight: 600;
      margin: 8px 0 4px 0;
    }}
    .response-content h2:first-child, .response-content h3:first-child,
    .response-content h4:first-child, .response-content h5:first-child {{
      margin-top: 0;
    }}
    .response-content p {{
      margin: 4px 0;
    }}
    .response-content p:empty {{
      display: none;
    }}
    .response-content ul, .response-content ol {{
      margin: 4px 0;
      /* Logical: the bubble carries dir='auto', so bullets indent from the
         start edge of the ANSWER's direction, not always from the right. */
      padding-inline-start: 20px;
    }}
    .response-content li {{
      margin: 2px 0;
    }}
    .response-content hr {{
      margin: 8px 0;
      border: none;
      border-top: 1px solid var(--border-color);
    }}
    .response-content strong {{
      color: var(--accent-green-dark);
      font-weight: 700;
    }}
    .response-content code {{
      background: var(--bg-secondary);
      padding: 2px 6px;
      border-radius: 5px;
      font-size: 13px;
      font-family: 'JetBrains Mono', 'Consolas', monospace;
    }}
    /* Collapsible detail fold — the road-design output keeps its heavy tables
       (PVI, cross-sections, volumes, routing warnings, the full report) behind
       one of these so the chat stays a short status log the engineer can drill
       into on demand. */
    .response-content details {{
      margin: 6px 0;
      border: 1px solid var(--border-color);
      border-radius: 9px;
      background: var(--bg-secondary);
      overflow: hidden;
    }}
    .response-content details > summary {{
      cursor: pointer;
      padding: 8px 12px;
      font-size: 13px;
      font-weight: 600;
      color: var(--accent-blue);
      list-style: none;
      user-select: none;
    }}
    .response-content details > summary::-webkit-details-marker {{ display: none; }}
    .response-content details > summary::before {{
      content: '◀';
      display: inline-block;
      /* inline-end, so the gap after the disclosure arrow lands on the correct
         side once dir='auto' resolves an English answer to LTR. */
      margin-inline-end: 6px;
      font-size: 10px;
      color: var(--text-muted);
      transition: transform 0.15s ease;
    }}
    .response-content details[open] > summary::before {{
      transform: rotate(-90deg);
    }}
    .response-content details > summary:hover {{
      background: var(--green-tint);
    }}
    .response-content details > *:not(summary) {{
      margin-right: 12px;
      margin-left: 12px;
    }}
    .response-content details > table {{
      width: calc(100% - 24px);
    }}
    .response-content details[open] {{
      padding-bottom: 8px;
    }}
    .response-content pre {{
      background: var(--bg-secondary);
      padding: 12px;
      border-radius: 9px;
      overflow-x: auto;
      margin: 12px 0;
      direction: ltr;
      text-align: left;
      font-family: 'JetBrains Mono', 'Consolas', monospace;
    }}
    .response-content pre code {{
      background: none;
      padding: 0;
    }}
    /* ── Findings / data table — polished + responsive ───────────────── */
    .table-wrapper {{
      position: relative;
      margin: 20px 0 16px;
    }}
    /* Floating copy chip — sits just above the table leading corner. It lives
       in .table-wrapper (a sibling of the clipped scroll box) so the
       rounded-corner overflow can never clip it. */
    .table-wrapper .copy-btn {{
      position: absolute;
      top: -13px;
      left: 10px;
      z-index: 10;
      background: var(--bg-card);
      box-shadow: 0 2px 8px rgba(20,40,45,0.14);
    }}
    /* The scroll box owns the rounded corners + shadow so the cell grid is
       clipped to a clean pill and can scroll sideways on tight panels. */
    .table-scroll {{
      overflow-x: auto;
      overflow-y: hidden;
      border-radius: 14px;
      background: var(--bg-card);
      box-shadow: var(--shadow-soft);
      -webkit-overflow-scrolling: touch;
    }}
    .table-scroll::-webkit-scrollbar {{ height: 7px; }}
    .table-scroll::-webkit-scrollbar-thumb {{
      background: var(--green-tint-border);
      border-radius: 7px;
    }}
    .table-scroll::-webkit-scrollbar-track {{ background: transparent; }}
    .response-content table {{
      width: 100%;
      border-collapse: collapse;
      font-size: 13px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      background: var(--bg-card);
      color: var(--text-primary);
    }}
    .response-content th, .response-content td {{
      border: 1px solid var(--border-color);
      padding: 9px 10px;
      /* 'start', not 'right': the bubble carries dir='auto', so a Hebrew answer
         still aligns right while an English/Russian one aligns left along with
         its column order. */
      text-align: start;
      vertical-align: top;
      line-height: 1.55;
      /* No per-column min-width: 4–5 column tables would demand 384+px and
         crush the layout on the docked palette. Long tokens wrap; if a row
         genuinely needs more room it scrolls inside .table-scroll. */
      overflow-wrap: anywhere;
      word-break: break-word;
    }}
    .response-content th {{
      background: linear-gradient(180deg, #2EA64C 0%, #1C8C40 100%);
      color: #fff;
      font-weight: 700;
      letter-spacing: .2px;
      border-color: #1C8C40;
      /* Allow long Hebrew header labels to wrap — nowrap forces the whole
         table wider than the panel and pushes content into horizontal scroll. */
      white-space: normal;
    }}
    .response-content tbody tr {{
      transition: background-color .15s ease;
    }}
    /* Zebra + hover use translucent green so they read correctly on both the
       light and dark themes (no hard-coded light-only colours). */
    .response-content tbody tr:nth-child(even) {{
      background-color: rgba(46,166,76,0.055);
    }}
    .response-content tbody tr:hover {{
      background-color: rgba(46,166,76,0.13);
    }}
    /* Clickable finding-location links — themed so they stay legible on both
       backgrounds (beats the inline colour the renderer emits). */
    .response-content .mahod-loc-link {{
      color: var(--accent-green-dark) !important;
      text-decoration-thickness: 1px;
      text-underline-offset: 2px;
      transition: color .15s ease;
    }}
    .response-content .mahod-loc-link:hover {{
      color: var(--accent-green) !important;
    }}

    /* ── Scope / fix-plan / fix-result cards ───────────────────────────
       These interactive cards hold short labels, numbers (e.g. an
       alignment length '12131m') and dropdowns. The aggressive
       word-break/overflow-wrap on the generic findings table breaks those
       numbers mid-token ('121'⏎'31m') and stacks header words letter by
       letter on a narrow palette. Here we break only at real spaces and
       never wrap a header — the card's .table-scroll already provides a
       clean horizontal scroll when the columns out-grow the panel, so the
       text columns stay readable and the values 'just fit'. */
    .response-content .scope-card th, .response-content .scope-card td,
    .response-content .fix-plan th,  .response-content .fix-plan td,
    .response-content .fix-result th, .response-content .fix-result td {{
      overflow-wrap: normal;
      word-break: normal;
    }}
    .response-content .scope-card th,
    .response-content .fix-plan th,
    .response-content .fix-result th {{
      white-space: nowrap;
    }}

    /* ── Findings / analysis-report tables ─────────────────────────────
       The markdown→HTML report tables (class .mahod-stackable, set when a
       <thead> is found) crushed their numeric columns to near-zero on the
       wide grid view — shattering values like '3500 מ׳' into '35'⏎'00'⏎'מ׳'
       and stacking the 'מהירות תכן' header letter by letter. Give every data
       cell a sensible minimum width and stop intra-number breaking so the
       short/numeric columns 'just fit'; the long prose columns still wrap and
       .table-scroll handles any overflow. The ≤420px card layout below resets
       this (its own td{{ min-width:0 }} wins by source order inside @media). */
    .response-content table.mahod-stackable th,
    .response-content table.mahod-stackable td {{
      overflow-wrap: break-word;
      word-break: normal;
    }}
    .response-content table.mahod-stackable td {{
      min-width: 64px;
    }}
    .response-content table.mahod-stackable th {{
      white-space: nowrap;
    }}

    /* ── Narrow panels ────────────────────────────────────────────────
       Two behaviours by table type:
       • Tables whose header row we captured (class .mahod-stackable, set by
         setupCopyButtons when it found <thead> cells) collapse each row into a
         readable labeled card.
       • Headerless tables (no labels to show) are left as-is so they keep their
         horizontal-scroll box — far more readable than a label-less card.
       Breakpoint is 420px (not 640px) because a Civil 3D PaletteSet is almost
       always 380–560px wide — at 640px every docked panel would flip to the
       card layout, which is far less compact than the row layout + horizontal
       scroll fallback that .table-scroll already provides. */
    @media (max-width: 420px) {{
      .table-wrapper:has(table.mahod-stackable) {{ margin: 16px 0; }}
      .table-wrapper:has(table.mahod-stackable) .copy-btn {{ top: -11px; }}
      .table-wrapper:has(table.mahod-stackable) .table-scroll {{
        overflow: visible;
        border-radius: 0;
        box-shadow: none;
        background: transparent;
      }}
      .response-content table.mahod-stackable {{
        width: 100%;
        background: transparent;
      }}
      /* Hide the header row visually but keep it in the DOM; each column title
         is surfaced per-cell through its data-label instead. */
      .response-content table.mahod-stackable thead {{
        position: absolute;
        width: 1px; height: 1px;
        padding: 0; margin: -1px;
        overflow: hidden;
        clip: rect(0 0 0 0);
        white-space: nowrap; border: 0;
      }}
      .response-content table.mahod-stackable tbody,
      .response-content table.mahod-stackable tr,
      .response-content table.mahod-stackable td {{
        display: block;
        width: 100%;
        min-width: 0;
      }}
      .response-content table.mahod-stackable tbody tr {{
        background: var(--bg-card) !important;
        border: 1px solid var(--green-tint-border);
        border-radius: 14px;
        margin: 0 0 12px;
        box-shadow: var(--shadow-soft);
        overflow: hidden;
      }}
      .response-content table.mahod-stackable td {{
        border: none;
        border-bottom: 1px solid var(--border-color);
        padding: 9px 14px 10px;
        text-align: start;
      }}
      .response-content table.mahod-stackable td:last-child {{ border-bottom: none; }}
      /* Column title shown above each value (definition-list style). */
      .response-content table.mahod-stackable td[data-label]::before {{
        content: attr(data-label);
        display: block;
        font-size: 11px;
        font-weight: 700;
        color: var(--accent-green-dark);
        margin-bottom: 3px;
        letter-spacing: .2px;
      }}
      /* First column (the location, in findings tables) reads as a card title. */
      .response-content table.mahod-stackable td:first-child {{
        background: var(--green-tint);
        font-weight: 600;
      }}
    }}
    .response-content blockquote {{
      /* Logical properties so the rule follows the bubble's resolved
         direction (dir='auto') instead of always hugging the right edge. */
      border-inline-start: 3px solid var(--accent-green);
      padding-inline-start: 12px;
      margin: 10px 0;
      color: var(--text-secondary);
    }}

    /* Timestamp — wraps to its own line under the bubble (flex-basis:100%),
       offset by the avatar column so it sits under the bubble, not the avatar. */
    .timestamp {{
      flex-basis: 100%;
      font-size: 11px;
      color: var(--text-muted);
      margin-top: 5px;
    }}
    .assistant-message .timestamp {{
      text-align: right;
      padding-right: 48px;
    }}
    .user-message .timestamp {{
      text-align: left;
      padding-left: 48px;
    }}
    .assistant-message .timestamp::after {{
      content: ' · MahodAI';
      color: var(--accent-green);
      font-weight: 600;
    }}

    /* System Message */
    .system-content {{
      display: flex;
      align-items: center;
      gap: 8px;
      background: var(--green-tint);
      border: 1px solid var(--green-tint-border);
      padding: 8px 16px;
      border-radius: 999px;
      font-size: 12.5px;
      color: var(--accent-green-dark);
    }}
    .system-message .timestamp {{
      text-align: center;
      padding: 0;
    }}
    .system-icon {{
      font-size: 14px;
    }}

    /* Loader */
    .loader-message {{
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 12px 16px;
      background: var(--bg-secondary);
      border-radius: 12px;
      font-size: 13px;
      color: var(--text-secondary);
    }}
    .loader-spinner {{
      width: 20px;
      height: 20px;
      border: 2px solid var(--border-color);
      border-top-color: var(--accent-green);
      border-radius: 50%;
      animation: spin 1s linear infinite;
    }}
    @keyframes spin {{
      to {{ transform: rotate(360deg); }}
    }}

    /* Thinking video indicator (replaces the old pulse-dot + text) */
    .thinking-indicator {{
      display: flex;
      flex-direction: row-reverse; /* video right, label left in RTL context */
      justify-content: center;
      align-items: center;
      gap: 14px;
      padding: 6px 0;
    }}
    .thinking-video {{
      width: 96px;
      height: 96px;
      object-fit: contain;
      background: transparent;
      pointer-events: none;
      flex-shrink: 0;
    }}
    .thinking-label {{
      color: var(--text-secondary);
      font-size: 14px;
      font-weight: 500;
      white-space: nowrap;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
    }}
    /* Animated trailing dots — bouncing loading wave (thinking label + active step line) */
    .thinking-label .td,
    .thinking-steps .td {{
      display: inline-block;
      animation: td-bounce 1.2s infinite ease-in-out;
    }}
    .thinking-label .td.d1,
    .thinking-steps .td.d1 {{ animation-delay: 0s;    }}
    .thinking-label .td.d2,
    .thinking-steps .td.d2 {{ animation-delay: 0.15s; }}
    .thinking-label .td.d3,
    .thinking-steps .td.d3 {{ animation-delay: 0.3s;  }}
    @keyframes td-bounce {{
      0%, 80%, 100% {{ transform: translateY(0);    opacity: 0.45; }}
      40%           {{ transform: translateY(-4px); opacity: 1;    }}
    }}

    /* Live thinking-process log — small dim lines of what the agent is doing */
    .thinking-steps {{
      display: flex;
      flex-direction: column;
      gap: 2px;
      margin: 6px 2px 2px;
      max-height: 140px;
      overflow-y: auto;
    }}
    .thinking-steps::-webkit-scrollbar {{ width: 8px; }}
    .thinking-steps::-webkit-scrollbar-thumb {{
      background: var(--border-color);
      border-radius: 4px;
    }}
    .thinking-steps .step-line {{
      color: var(--text-secondary);
      font-size: 12px;
      line-height: 1.55;
      opacity: 0.78;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      white-space: pre-wrap;
      word-break: break-word;
      animation: step-in 0.25s ease both;
    }}
    .thinking-steps .step-line::before {{
      content: '· ';
      opacity: 0.6;
    }}
    @keyframes step-in {{
      from {{ opacity: 0; transform: translateY(2px); }}
      to   {{ opacity: 0.78; transform: none; }}
    }}
    /* Typewriter target — answer text revealed gradually before the markdown swap.
       Completed lines are markdown-styled in .tw-md as soon as they finish; only the
       line still being typed stays raw in .tw-raw (so styling appears progressively,
       not all-at-once at the end). */
    .tw-text {{
      white-space: pre-wrap;
      word-break: break-word;
      line-height: 1.7;
    }}
    .tw-text .tw-md {{ white-space: normal; }}
    .tw-text .tw-md > :first-child {{ margin-top: 0; }}
    .tw-text .tw-md > :last-child {{ margin-bottom: 0; }}
    .tw-text .tw-raw {{ white-space: pre-wrap; }}

    /* Typing Indicator */
    .typing-indicator {{
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 0;
    }}
    .typing-indicator .pulse-dot {{
      width: 10px;
      height: 10px;
      min-width: 10px;
      background: var(--accent-green);
      border-radius: 50%;
      animation: pulse 1.4s infinite ease-in-out;
    }}
    .typing-indicator .status-text {{
      color: var(--text-secondary);
      font-size: 14px;
      white-space: nowrap;
    }}
    /* Legacy three-dot indicator (for loader-message) */
    .loader-message .typing-indicator span {{
      width: 8px;
      height: 8px;
      background: var(--accent-green);
      border-radius: 50%;
      animation: pulse 1.4s infinite ease-in-out;
    }}
    .loader-message .typing-indicator span:nth-child(1) {{ animation-delay: 0s; }}
    .loader-message .typing-indicator span:nth-child(2) {{ animation-delay: 0.2s; }}
    .loader-message .typing-indicator span:nth-child(3) {{ animation-delay: 0.4s; }}
    @keyframes pulse {{
      0%, 80%, 100% {{ transform: scale(0.6); opacity: 0.4; }}
      40% {{ transform: scale(1); opacity: 1; }}
    }}

    /* Copy buttons */
    .copy-btn {{
      display: inline-flex;
      align-items: center;
      gap: 4px;
      padding: 4px 10px;
      background: var(--bg-secondary);
      border: 1px solid var(--border-color);
      border-radius: 6px;
      font-size: 12px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      color: var(--text-secondary);
      cursor: pointer;
      transition: all 0.2s ease;
    }}
    .copy-btn:hover {{
      background: var(--accent-green);
      color: white;
      border-color: var(--accent-green);
    }}
    .copy-btn.copied {{
      background: var(--accent-green);
      color: white;
      border-color: var(--accent-green);
    }}
    .copy-btn svg {{
      width: 14px;
      height: 14px;
    }}

    /* Message header with copy button */
    .message-header {{
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 8px;
    }}
    .message-copy-btn {{
      display: inline-flex;
    }}
    .message-buttons {{
      opacity: 0;
      transition: opacity 0.2s ease;
    }}
    .bubble:hover .message-buttons {{
      opacity: 1;
    }}

    /* Per-message feedback (v1.14). Deliberately ALWAYS visible, unlike the
       hover-only copy/download/share row: a reaction the engineer already gave
       has to read at a glance, and asking for feedback shouldn't require
       discovering a hover affordance. */
    .feedback-row {{
      display: flex;
      align-items: center;
      gap: 6px;
      margin-top: 10px;
      padding-top: 8px;
      border-top: 1px solid var(--border-color);
    }}
    .fb-btn {{
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 30px;
      padding: 4px 8px;
      font-size: 14px;
      line-height: 1;
      border: 1px solid var(--border-color);
      border-radius: 8px;
      background: var(--bg-secondary);
      color: var(--text-secondary);
      cursor: pointer;
      transition: all 0.15s ease;
    }}
    .fb-btn:hover {{ border-color: var(--accent-green); background: var(--green-tint); }}
    .fb-btn.rated-up {{ background: var(--green-tint); border-color: var(--accent-green); }}
    .fb-btn.rated-down {{ background: rgba(220,38,38,0.14); border-color: #dc2626; }}
    .fb-btn.has-feedback {{ background: rgba(37,99,235,0.14); border-color: #2563eb; }}
    .fb-status {{
      font-size: 11px;
      color: var(--text-muted);
      opacity: 0;
      transition: opacity 0.2s ease;
    }}
    .fb-status.error {{ color: #dc2626; }}

    .feedback-box {{
      margin-top: 8px;
      padding: 10px;
      border: 1px solid var(--border-color);
      border-radius: 10px;
      background: var(--bg-secondary);
    }}
    .feedback-box[hidden] {{ display: none; }}
    .feedback-box textarea {{
      width: 100%;
      resize: vertical;
      min-height: 58px;
      padding: 8px 10px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      font-size: 13px;
      line-height: 1.6;
      direction: rtl;
      border: 1px solid var(--border-color);
      border-radius: 8px;
      background: var(--bg-card);
      color: var(--text-primary);
    }}
    .feedback-box textarea:focus {{ border-color: var(--accent-green); }}
    .feedback-box-actions {{
      display: flex;
      align-items: center;
      gap: 8px;
      margin-top: 8px;
    }}
    .feedback-box-actions button {{
      padding: 5px 12px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      font-size: 12px;
      border: 1px solid var(--border-color);
      border-radius: 8px;
      background: var(--bg-card);
      color: var(--text-secondary);
      cursor: pointer;
      transition: all 0.15s ease;
    }}
    .feedback-box-actions button:hover {{ border-color: var(--accent-green); color: var(--accent-green); }}
    .feedback-box-actions .fb-send {{
      background: var(--accent-green);
      border-color: var(--accent-green);
      color: #fff;
    }}
    .feedback-box-actions .fb-send:hover {{ color: #fff; opacity: 0.9; }}
    .fb-hint {{ font-size: 10px; color: var(--text-muted); }}
    @media (max-width: 420px) {{
      .fb-hint {{ display: none; }}
    }}

    *:focus {{ outline: none !important; }}

    /* Tab strip for multi-drawing support — pinned to the top of the
       chat so the user can switch chats without scrolling back up.
       top: 0 keeps it flush with the WebView2 viewport edge so it stays
       fully visible when the chat scrolls. */
    .tab-strip {{
      display: flex;
      align-items: center;
      gap: 4px;
      background: var(--bg-card);
      border-bottom: 1px solid var(--border-color);
      overflow-x: auto;
      padding: 6px;
      margin: 0 -18px 16px -18px;
      scrollbar-width: thin;
      position: sticky;
      top: 0;
      z-index: 100;
    }}
    .tab-strip::-webkit-scrollbar {{ height: 3px; }}
    .tab-strip::-webkit-scrollbar-thumb {{ background: var(--border-color); border-radius: 3px; }}
    .tab-item {{
      display: flex;
      align-items: center;
      gap: 7px;
      padding: 7px 14px;
      font-size: 12.5px;
      font-family: 'Heebo', 'Segoe UI', sans-serif;
      color: var(--text-secondary);
      cursor: pointer;
      white-space: nowrap;
      border-radius: 999px;
      border: 1px solid transparent;
      transition: all 0.15s ease;
      flex-shrink: 0;
    }}
    .tab-item:hover {{
      color: var(--text-primary);
      background: var(--bg-secondary);
    }}
    .tab-item.active {{
      color: var(--accent-green-dark);
      background: var(--green-tint);
      border-color: var(--green-tint-border);
      font-weight: 600;
    }}
    .tab-item.active::before {{
      content: '';
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: var(--accent-green);
      flex-shrink: 0;
    }}
    .tab-name {{
      max-width: 140px;
      overflow: hidden;
      text-overflow: ellipsis;
    }}
    .tab-close {{
      font-size: 15px;
      line-height: 1;
      opacity: 0.4;
      cursor: pointer;
      padding: 0 2px;
    }}
    .tab-close:hover {{
      opacity: 1;
      color: var(--accent-green);
    }}
    .tab-add {{
      display: flex;
      align-items: center;
      justify-content: center;
      min-width: 28px;
      height: 28px;
      border-radius: 999px;
      font-size: 18px;
      line-height: 1;
      color: var(--text-secondary);
      cursor: pointer;
      user-select: none;
      transition: all 0.15s ease;
      flex-shrink: 0;
    }}
    .tab-add:hover {{
      color: var(--accent-green);
      background: var(--green-tint);
    }}
    /* Live step line for interactive pick tools — ONE line inside the current
       message, rewritten as the flow advances (never a bubble per step). */
    .mahod-step-hint {{
      margin-top: 10px;
      padding: 8px 10px;
      border-radius: 8px;
      background: var(--green-tint);
      border: 1px solid var(--accent-green);
      color: var(--text-primary);
      font-size: 0.95em;
    }}
  </style>
</head>
<body>
  {tabStripHtml}
  {welcomeHtml}
  <div id='messages'>{messages}</div>
  <div id='scroll-anchor' style='height:1px;'></div>
  <script>
    // v1.14 per-message feedback state, re-injected by C# on every render.
    window.MAHOD_FEEDBACK = {feedbackStateJson};

    const copyIcon = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2'><rect x='9' y='9' width='13' height='13' rx='2' ry='2'/><path d='M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1'/></svg>`;
    const checkIcon = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='currentColor' stroke-width='2'><polyline points='20 6 9 17 4 12'/></svg>`;

    function copyHtml(html, plainText) {{
      const scrollX = window.scrollX;
      const scrollY = window.scrollY;

      const container = document.createElement('div');
      container.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0;background:white;color:black;';
      container.innerHTML = html;
      document.body.appendChild(container);

      const range = document.createRange();
      range.selectNodeContents(container);
      const sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);

      let success = false;
      try {{
        success = document.execCommand('copy');
      }} catch (err) {{
        success = false;
      }}

      sel.removeAllRanges();
      document.body.removeChild(container);
      window.scrollTo(scrollX, scrollY);
      return success;
    }}

    function copyText(text) {{
      const scrollX = window.scrollX;
      const scrollY = window.scrollY;
      const textarea = document.createElement('textarea');
      textarea.value = text;
      textarea.style.cssText = 'position:fixed;left:-9999px;top:0;opacity:0;';
      document.body.appendChild(textarea);
      textarea.focus({{preventScroll: true}});
      textarea.select();
      let success = false;
      try {{
        success = document.execCommand('copy');
      }} catch (err) {{
        success = false;
      }}
      document.body.removeChild(textarea);
      window.scrollTo(scrollX, scrollY);
      return success;
    }}

    function showCopied(btn, originalHtml) {{
      btn.innerHTML = checkIcon + '<span>הועתק!</span>';
      btn.classList.add('copied');
      setTimeout(() => {{
        btn.innerHTML = originalHtml;
        btn.classList.remove('copied');
      }}, 2000);
    }}

    function getTableHtml(table) {{
      const clone = table.cloneNode(true);
      clone.style.cssText = 'border-collapse:collapse;width:100%;direction:rtl;background:white;';
      clone.querySelectorAll('th, td').forEach(cell => {{
        cell.style.cssText = 'border:1px solid #000;padding:6px 10px;text-align:right;background:white;color:black;';
      }});
      clone.querySelectorAll('th').forEach(cell => {{
        cell.style.cssText = 'border:1px solid #000;padding:6px 10px;text-align:right;background-color:#217346;color:white;font-weight:bold;';
      }});
      return clone.outerHTML;
    }}

    function tableToText(table) {{
      let text = '';
      const rows = table.querySelectorAll('tr');
      const colWidths = [];
      const data = [];

      rows.forEach(row => {{
        const cells = row.querySelectorAll('th, td');
        const rowData = [];
        cells.forEach((cell, i) => {{
          const cellText = cell.innerText.trim().replace(/\\s+/g, ' ');
          rowData.push(cellText);
          colWidths[i] = Math.max(colWidths[i] || 0, cellText.length);
        }});
        data.push(rowData);
      }});

      data.forEach((row, rowIdx) => {{
        text += '| ' + row.map((cell, i) => cell.padEnd(colWidths[i])).join(' | ') + ' |\\n';
        if (rowIdx === 0) {{
          text += '|' + colWidths.map(w => '-'.repeat(w + 2)).join('|') + '|\\n';
        }}
      }});
      return text;
    }}

    function copyTableToClipboard(e) {{
      e.preventDefault();
      e.stopPropagation();
      const btn = e.currentTarget;
      const wrapper = btn.closest('.table-wrapper');
      const table = wrapper ? wrapper.querySelector('table') : null;
      if (!table) return;

      const html = getTableHtml(table);
      const originalHtml = btn.innerHTML;
      if (copyHtml(html)) {{
        showCopied(btn, originalHtml);
      }}
    }}

    function copyMessageToClipboard(e) {{
      e.preventDefault();
      e.stopPropagation();
      const btn = e.currentTarget;
      const bubble = btn.closest('.assistant-bubble');
      const content = bubble ? bubble.querySelector('.response-content') : null;
      if (!content) return;

      const clone = content.cloneNode(true);
      clone.querySelectorAll('.copy-btn, style, script, .table-wrapper > .copy-btn').forEach(el => el.remove());

      clone.querySelectorAll('.table-wrapper').forEach(wrapper => {{
        const table = wrapper.querySelector('table');
        if (table) {{
          wrapper.parentNode.replaceChild(table, wrapper);
        }}
      }});

      clone.querySelectorAll('table').forEach(table => {{
        table.style.cssText = 'border-collapse:collapse;width:100%;direction:rtl;margin:10px 0;background:white;';
        table.querySelectorAll('th, td').forEach(cell => {{
          cell.style.cssText = 'border:1px solid #000;padding:6px 10px;text-align:right;background:white;color:black;';
        }});
        table.querySelectorAll('th').forEach(cell => {{
          cell.style.cssText = 'border:1px solid #000;padding:6px 10px;text-align:right;background-color:#217346;color:white;font-weight:bold;';
        }});
      }});

      const wrapper = '<div style=""background:white;color:black;font-family:Arial,sans-serif;"">' + clone.innerHTML + '</div>';

      const originalHtml = btn.innerHTML;
      if (copyHtml(wrapper)) {{
        showCopied(btn, originalHtml);
      }}
    }}

    function downloadMessage(e, bubble) {{
      e.preventDefault();
      e.stopPropagation();
      const content = bubble.querySelector('.response-content')?.innerHTML || '';
      window.chrome.webview.postMessage(JSON.stringify({{
        action: 'download',
        content: content
      }}));
    }}

    function shareMessage(e, bubble) {{
      e.preventDefault();
      e.stopPropagation();
      const content = bubble.querySelector('.response-content')?.innerText || '';
      window.chrome.webview.postMessage(JSON.stringify({{
        action: 'share',
        content: content
      }}));
    }}

    // ── Per-message feedback (protocol v1.14) ────────────────────────────
    // Reaction (👍/👎) + free text, mirroring the web chat. C# owns the state
    // (window.MAHOD_FEEDBACK); every click posts the FULL desired state and the
    // agent persists it. Nothing here writes to the drawing or the network
    // directly — it all goes through the WebView2 message channel.

    function fbState(id) {{
      if (!window.MAHOD_FEEDBACK) window.MAHOD_FEEDBACK = {{}};
      if (!window.MAHOD_FEEDBACK[id]) window.MAHOD_FEEDBACK[id] = {{ rating: null, feedback: null }};
      return window.MAHOD_FEEDBACK[id];
    }}

    function fbPost(id, rating, feedback) {{
      window.chrome.webview.postMessage(JSON.stringify({{
        action: 'message_feedback',
        message_id: id,
        rating: rating,
        feedback: feedback
      }}));
    }}

    function fbPaint(bubble) {{
      const id = bubble.getAttribute('data-mahod-msg');
      const st = fbState(id);
      const up = bubble.querySelector('.fb-up');
      const down = bubble.querySelector('.fb-down');
      const note = bubble.querySelector('.fb-note');
      if (up) up.classList.toggle('rated-up', st.rating === 1);
      if (down) down.classList.toggle('rated-down', st.rating === -1);
      if (note) {{
        note.classList.toggle('has-feedback', !!st.feedback);
        note.title = st.feedback ? 'עריכת המשוב' : 'כתיבת משוב';
      }}
    }}

    function fbFlash(bubble, text, isError) {{
      const badge = bubble.querySelector('.fb-status');
      if (!badge) return;
      badge.textContent = text;
      badge.classList.toggle('error', !!isError);
      badge.style.opacity = '1';
      clearTimeout(badge._t);
      badge._t = setTimeout(() => {{ badge.style.opacity = '0'; }}, 2500);
    }}

    function fbOpenBox(bubble, open) {{
      const box = bubble.querySelector('.feedback-box');
      if (!box) return;
      const id = bubble.getAttribute('data-mahod-msg');
      if (open) {{
        const ta = box.querySelector('textarea');
        // Reopening always starts from what is SAVED, so an abandoned draft is
        // really gone — closing without sending must cost nothing.
        if (ta) ta.value = fbState(id).feedback || '';
        box.hidden = false;
        if (ta) ta.focus();
      }} else {{
        box.hidden = true;
      }}
    }}

    function fbRate(bubble, value) {{
      const id = bubble.getAttribute('data-mahod-msg');
      const st = fbState(id);
      // Clicking the reaction you already gave removes it (same as the web).
      const next = st.rating === value ? null : value;
      st.rating = next;
      fbPaint(bubble);
      fbPost(id, next, st.feedback);
      // A 👎 the engineer just SET opens the box straight away — that is the
      // moment asking what went wrong is worth it. Removing one never re-opens it.
      if (next === -1) fbOpenBox(bubble, true);
    }}

    function fbSend(bubble) {{
      const id = bubble.getAttribute('data-mahod-msg');
      const st = fbState(id);
      const box = bubble.querySelector('.feedback-box');
      const ta = box ? box.querySelector('textarea') : null;
      const text = ta ? ta.value.trim() : '';
      st.feedback = text || null;
      fbOpenBox(bubble, false);
      fbPaint(bubble);
      fbPost(id, st.rating, st.feedback);
    }}

    // Called from C# when the agent acknowledges (or fails to save) feedback.
    window.mahodFeedbackAck = function(messageId, ok, state) {{
      const bubble = document.querySelector(""[data-mahod-msg='"" + messageId + ""']"");
      if (state) window.MAHOD_FEEDBACK[messageId] = state;
      if (!bubble) return;
      fbPaint(bubble);
      fbFlash(bubble, ok ? 'המשוב נשמר' : 'המשוב לא נשמר', !ok);
    }};

    function setupFeedbackButtons() {{
      // Only bubbles C# marked as rateable answers get the row — approval cards,
      // fix results and scope pickers are UI, not answers.
      document.querySelectorAll('.assistant-bubble[data-mahod-msg]').forEach((bubble) => {{
        if (bubble.querySelector('.feedback-row')) return;

        const row = document.createElement('div');
        row.className = 'feedback-row';
        row.innerHTML =
          ""<button class='fb-btn fb-up' title='תשובה טובה'>👍</button>"" +
          ""<button class='fb-btn fb-down' title='תשובה לא טובה'>👎</button>"" +
          ""<button class='fb-btn fb-note' title='כתיבת משוב'>💬</button>"" +
          ""<span class='fb-status'></span>"";

        const box = document.createElement('div');
        box.className = 'feedback-box';
        box.hidden = true;
        box.innerHTML =
          ""<textarea rows='3' placeholder='מה היה טוב או חסר בתשובה? (אופציונלי)'></textarea>"" +
          ""<div class='feedback-box-actions'>"" +
          ""<button class='fb-send'>שליחה</button>"" +
          ""<button class='fb-cancel'>ביטול</button>"" +
          ""<span class='fb-hint'>Ctrl+Enter לשליחה · Esc לסגירה</span>"" +
          ""</div>"";

        bubble.appendChild(row);
        bubble.appendChild(box);

        row.querySelector('.fb-up').addEventListener('click', (e) => {{ e.preventDefault(); fbRate(bubble, 1); }});
        row.querySelector('.fb-down').addEventListener('click', (e) => {{ e.preventDefault(); fbRate(bubble, -1); }});
        row.querySelector('.fb-note').addEventListener('click', (e) => {{
          e.preventDefault();
          fbOpenBox(bubble, box.hidden);
        }});
        box.querySelector('.fb-send').addEventListener('click', (e) => {{ e.preventDefault(); fbSend(bubble); }});
        box.querySelector('.fb-cancel').addEventListener('click', (e) => {{ e.preventDefault(); fbOpenBox(bubble, false); }});
        box.querySelector('textarea').addEventListener('keydown', (e) => {{
          if (e.key === 'Escape') {{ e.preventDefault(); fbOpenBox(bubble, false); }}
          else if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {{ e.preventDefault(); fbSend(bubble); }}
        }});

        fbPaint(bubble);
      }});
    }}

    function setupCopyButtons() {{
      document.querySelectorAll('.response-content table').forEach((table) => {{
        // Surface each column's header on its body cells so the responsive
        // card layout (narrow panels) can label every value. Idempotent.
        const heads = table.querySelectorAll('thead th');
        if (heads.length) {{
          const labels = Array.from(heads).map(h => (h.textContent || '').trim());
          table.querySelectorAll('tbody tr').forEach(tr => {{
            tr.querySelectorAll('td').forEach((td, i) => {{
              if (labels[i] && !td.hasAttribute('data-label')) td.setAttribute('data-label', labels[i]);
            }});
          }});
          // Only tables whose header we captured may collapse into labeled cards on
          // narrow panels; headerless ones stay scrollable (see the @media rules).
          table.classList.add('mahod-stackable');
        }}

        if (table.closest('.table-wrapper')) return;

        const wrapper = document.createElement('div');
        wrapper.className = 'table-wrapper';
        const scroll = document.createElement('div');
        scroll.className = 'table-scroll';
        table.parentNode.insertBefore(wrapper, table);
        scroll.appendChild(table);
        wrapper.appendChild(scroll);

        const btn = document.createElement('button');
        btn.className = 'copy-btn';
        btn.innerHTML = copyIcon + '<span>העתק טבלה</span>';
        btn.addEventListener('click', copyTableToClipboard);
        wrapper.insertBefore(btn, scroll);
      }});

      document.querySelectorAll('.assistant-bubble').forEach((bubble) => {{
        if (bubble.querySelector('.message-copy-btn')) return;
        if (bubble.querySelector('.typing-indicator')) return;
        if (bubble.querySelector('.loader-message')) return;

        const btnContainer = document.createElement('div');
        btnContainer.className = 'message-buttons';
        btnContainer.style.cssText = 'display:flex;gap:8px;margin-top:12px;opacity:0;transition:opacity 0.2s ease;flex-wrap:wrap;';

        const copyBtn = document.createElement('button');
        copyBtn.className = 'copy-btn message-copy-btn';
        copyBtn.innerHTML = copyIcon + '<span>העתק</span>';
        copyBtn.addEventListener('click', copyMessageToClipboard);
        btnContainer.appendChild(copyBtn);

        const dlBtn = document.createElement('button');
        dlBtn.className = 'copy-btn message-copy-btn';
        dlBtn.innerHTML = '⬇️ <span>הורד</span>';
        dlBtn.addEventListener('click', (e) => downloadMessage(e, bubble));
        btnContainer.appendChild(dlBtn);

        const shareBtn = document.createElement('button');
        shareBtn.className = 'copy-btn message-copy-btn';
        shareBtn.innerHTML = '✉️ <span>שתף</span>';
        shareBtn.addEventListener('click', (e) => shareMessage(e, bubble));
        btnContainer.appendChild(shareBtn);

        bubble.appendChild(btnContainer);

        bubble.addEventListener('mouseenter', () => {{ btnContainer.style.opacity = '1'; }});
        bubble.addEventListener('mouseleave', () => {{ btnContainer.style.opacity = '0'; }});
      }});

      // LAST, so the always-visible reaction row and its text box sit beneath
      // the hover-only copy/download/share row rather than splitting it.
      setupFeedbackButtons();
    }}

    document.addEventListener('DOMContentLoaded', setupCopyButtons);
    window.setupCopyButtons = setupCopyButtons;
  </script>
</body>
</html>";

            try
            {
                // NavigateToString has a ~2 MB limit on the passed string. With the
                // base64-embedded avatar replicated in every bubble, we can exceed this
                // silently — the browser then shows stale content or nothing.
                int htmlBytes = System.Text.Encoding.UTF8.GetByteCount(fullHtml);
                System.Diagnostics.Debug.WriteLine(
                    $"[UpdateBrowser] fullHtml chars={fullHtml.Length}, utf8 bytes={htmlBytes}, _vm.ConversationHtml chars={_vm.ConversationHtml.Length}, _vm.HasUserSentMessage={_vm.HasUserSentMessage}");
                if (htmlBytes > 1_800_000)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[UpdateBrowser] WARNING: HTML is {htmlBytes} bytes — near/over NavigateToString 2MB limit. Rendering may silently fail.");
                }
                ChatBrowser.NavigateToString(fullHtml);
                await Task.Delay(100);
                if (ChatBrowser.CoreWebView2 != null)
                {
                    await ChatBrowser.ExecuteScriptAsync("if(window.smartScroll) window.smartScroll(); else window.scrollTo(0, document.body.scrollHeight);");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("UpdateBrowser error: " + ex);
            }
        }

        #endregion

        #region Download/Share Handlers

        private async Task HandleDownloadAsync(string htmlContent)
        {
            try
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "PDF File (*.pdf)|*.pdf|Word Document (*.docx)|*.docx",
                    DefaultExt = ".pdf",
                    FileName = $"MahodAI_Report_{DateTime.Now:yyyyMMdd_HHmm}"
                };

                if (dialog.ShowDialog() == true)
                {
                    if (dialog.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ChatBrowser.CoreWebView2 != null)
                        {
                            await ChatBrowser.CoreWebView2.PrintToPdfAsync(dialog.FileName);
                            WpfMessageBox.Show($"הקובץ נשמר בהצלחה:\n{dialog.FileName}", "MahodAI", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    else // DOCX
                    {
                        GenerateDocx(dialog.FileName, htmlContent);
                        WpfMessageBox.Show($"הקובץ נשמר בהצלחה:\n{dialog.FileName}", "MahodAI", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HandleDownloadAsync error: {ex.Message}");
                WpfMessageBox.Show($"שגיאה בשמירת הקובץ:\n{ex.Message}", "MahodAI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Handles a click on a finding's location in the analysis report. Runs the
        /// <c>zoom_to_station_range</c> tool so the AutoCAD viewport focuses on the stretch
        /// of the alignment the finding refers to. The tool marshals to AutoCAD's main
        /// thread and locks the document itself (see <see cref="Tools.ToolExecutor"/>).
        /// </summary>
        private async Task HandleZoomToLocationAsync(JsonElement root)
        {
            try
            {
                string alignment = root.TryGetProperty("alignment", out var ap) ? ap.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(alignment))
                    return;

                var args = new Dictionary<string, object>
                {
                    ["alignment_name"] = alignment,
                    // Zoom in closer than the default so the clicked finding's pin is clearly
                    // visible (and separated from neighbours rather than collapsed in a cluster).
                    ["padding"] = 0.35,
                };
                if (root.TryGetProperty("station_start", out var s1p) && s1p.ValueKind == JsonValueKind.Number)
                    args["station_start"] = s1p.GetDouble();
                if (root.TryGetProperty("station_end", out var s2p) && s2p.ValueKind == JsonValueKind.Number)
                    args["station_end"] = s2p.GetDouble();

                if (!args.ContainsKey("station_start"))
                    return;

                // ToolExecutor is normally created once the agent connects; zoom is a
                // local read-only operation, so make one on demand for offline use.
                var executor = _vm.ToolExecutor ??= new Tools.ToolExecutor();

                // Keep the arguments document alive across the await — the tool reads it
                // synchronously on the main thread before ExecuteAsync completes.
                using var argsDoc = JsonDocument.Parse(JsonSerializer.Serialize(args));
                var result = await executor.ExecuteAsync(
                    "zoom_to_station_range", argsDoc.RootElement, CancellationToken.None);

                if (!result.Success)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[MahodAI] zoom_to_location failed: {result.Error?.Message}");
                    AddSystemMessage($"לא ניתן למקד את המיקום בשרטוט: {result.Error?.Message}");
                    return;
                }

                // Jump to the matching on-drawing pin. Pass the row's problem text so a curve
                // with several violations opens the RIGHT pin (not just whichever shares the
                // station). Marshal to the WPF thread — the popup is a WPF visual and the await
                // above may resume off it.
                double focusStation = args.TryGetValue("station_end", out var se) && se is double seVal
                    ? ((double)args["station_start"] + seVal) * 0.5
                    : (double)args["station_start"];
                string focusProblem = root.TryGetProperty("problem", out var pbp) ? pbp.GetString() ?? "" : "";
                bool pinOpened = Dispatcher.Invoke(
                    () => _problemOverlay.FocusMarker(alignment, focusStation, focusProblem));
                Utilities.MahodLogger.Info(
                    $"zoom_to_location: align='{alignment}', sta={focusStation:F1}, " +
                    $"problem.len={focusProblem.Length}, pinOpened={pinOpened}, " +
                    $"overlayVisible={_problemOverlay.IsVisible}, pins={_problemOverlay.Markers.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] HandleZoomToLocationAsync error: {ex.Message}");
                Utilities.MahodLogger.Error("HandleZoomToLocationAsync failed", ex);
            }
        }

        /// <summary>
        /// C# fallback: parses an analysis response's MARKDOWN findings table and shows pins.
        /// The primary path is JS-driven (<see cref="HandleProblemMarkers"/>), which works for
        /// HTML tables too; both converge on <see cref="ApplyProblemMarkers"/> (deduped).
        /// </summary>
        /// <summary>
        /// True when the streamed response is an analysis findings report (per-entity detail
        /// tables with a מיקום/בעיה header — HTML in the deterministic report, markdown as a
        /// fallback). Precise enough that ordinary chat replies mentioning those words don't trip it.
        /// </summary>
        private static bool LooksLikeFindingsReport(string content)
        {
            if (string.IsNullOrEmpty(content))
                return false;
            return content.Contains("מיקום</th>")
                || (content.Contains("| מיקום") && content.Contains("בעיה"));
        }

        private void TryShowProblemOverlay(string content)
        {
            try
            {
                var markers = Services.FindingsTableParser.Parse(HideInternalMarkers(content));
                if (markers.Count > 0)
                    ApplyProblemMarkers(markers, fromAnalysisStream: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] TryShowProblemOverlay error: {ex.Message}");
            }
        }

        /// <summary>
        /// Primary path: receives findings extracted from the RENDERED table DOM by JS
        /// (format-agnostic — markdown or HTML) and shows the on-drawing pins.
        /// </summary>
        private void HandleProblemMarkers(JsonElement root)
        {
            try
            {
                if (!root.TryGetProperty("markers", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return;

                var markers = new List<Models.ProblemMarker>();
                int i = 0;
                foreach (var el in arr.EnumerateArray())
                {
                    string align = el.TryGetProperty("alignment", out var ap) ? ap.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(align)) continue;
                    if (!el.TryGetProperty("station_start", out var s1p) || s1p.ValueKind != JsonValueKind.Number) continue;

                    i++;
                    var m = new Models.ProblemMarker
                    {
                        Index = i,
                        Alignment = align,
                        StationStart = s1p.GetDouble(),
                        LocationText = el.TryGetProperty("location", out var lp) ? lp.GetString() ?? "" : "",
                        Problem = el.TryGetProperty("problem", out var pp) ? pp.GetString() ?? "" : "",
                        ActualValue = el.TryGetProperty("value", out var vp) ? vp.GetString() ?? "" : "",
                        Required = el.TryGetProperty("required", out var rp) ? rp.GetString() ?? "" : "",
                        Source = el.TryGetProperty("source", out var sp) ? sp.GetString() ?? "" : "",
                    };
                    if (el.TryGetProperty("station_end", out var s2p) && s2p.ValueKind == JsonValueKind.Number)
                        m.StationEnd = s2p.GetDouble();
                    m.Severity = Services.FindingsTableParser.InferSeverity(m.Problem);
                    markers.Add(m);
                }

                if (markers.Count > 0)
                    ApplyProblemMarkers(markers, fromAnalysisStream: false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] HandleProblemMarkers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Caches an analysis's findings and shows the overlay, deduped by signature so the
        /// same set isn't re-shown on every DOM mutation. Deferred to Background priority so
        /// document access happens off the WebMessage/stream callback when AutoCAD is idle.
        ///
        /// Two lifecycle guards keep markers ephemeral (see <see cref="_suppressedProblemSignature"/>):
        /// during the <see cref="MarkerPhase.Fix"/> phase the fix flow owns the display, so
        /// JS reposts are ignored; and a set whose signature matches the last-cleared one is a
        /// stale re-collection (from the reload after a new message) and is ignored — UNLESS
        /// it comes straight off a finished analysis stream (<paramref name="fromAnalysisStream"/>),
        /// which is an authoritative "this is a fresh analysis" signal that overrides suppression.
        /// </summary>
        private void ApplyProblemMarkers(List<Models.ProblemMarker> markers, bool fromAnalysisStream)
        {
            if (markers == null || markers.Count == 0)
                return;

            // Fix phase: pins are driven by the fix plan/result, not the DOM. Ignore reposts.
            if (_markerPhase == MarkerPhase.Fix)
                return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Core.Application
                .DocumentManager.MdiActiveDocument;
            string drawing = doc?.Name ?? "";
            string sig = drawing + "||" + string.Join("|",
                markers.Select(m => $"{m.Alignment};{m.StationStart};{m.StationEnd};{m.Problem}"));

            // Stale re-collection of a table we already cleared this turn → stay cleared.
            // A finished analysis stream is exempt: re-analyzing an unchanged drawing yields
            // the same signature yet must still re-pin.
            if (!fromAnalysisStream && sig == _suppressedProblemSignature)
                return;

            if (sig == _lastProblemSignature && _problemOverlay.IsVisible)
                return;

            _lastProblemSignature = sig;
            _suppressedProblemSignature = null;
            _lastProblemDrawing = drawing;
            _lastProblemMarkers = markers;
            _markerPhase = MarkerPhase.Analysis;

            ShowOverlayDeferred(markers);
        }

        /// <summary>
        /// Shows the given markers on the overlay at Background priority, so the drawing
        /// read (LockDocument + world-point resolution) runs off the WebMessage/stream
        /// callback when AutoCAD is idle. An empty list tears the overlay down.
        /// </summary>
        private void ShowOverlayDeferred(List<Models.ProblemMarker> markers)
        {
            int gen = _overlayGeneration;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (gen != _overlayGeneration)
                    return;   // a clear/hide superseded this queued Show — drop it
                try { _problemOverlay.Show(markers); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MahodAI] overlay Show failed: {ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Clears the on-drawing pins for a new conversational turn (a sent message or a
        /// newly-started analysis). Remembers the cleared signature so the browser reload's
        /// DOM re-scan can't resurrect the same pins; a genuinely different analysis re-pins.
        /// </summary>
        private void ClearMarkersForNewTurn()
        {
            _overlayGeneration++;   // invalidate any Show queued for the outgoing set
            // Keep the suppression slot STICKY: only remember a real signature. A turn that
            // produced no findings table (a chat reply) leaves _lastProblemSignature null, and
            // overwriting the slot with null would forget the analysis we still need to suppress,
            // letting the reload's DOM re-scan resurrect it on the next turn.
            if (!string.IsNullOrEmpty(_lastProblemSignature))
                _suppressedProblemSignature = _lastProblemSignature;
            _lastProblemSignature = null;
            _lastProblemMarkers = new();
            _markerPhase = MarkerPhase.None;
            try { _problemOverlay.Hide(); } catch { /* best effort */ }
        }

        /// <summary>
        /// True when <paramref name="text"/> is a "remove the violation markers" request
        /// (Hebrew or English). Requires BOTH a clear verb and a marker noun. Deliberately
        /// conservative against collisions:
        /// <list type="bullet">
        /// <item>bails on a road-marking / drawing-element qualifier (דרך/נתיב/pipe/lane…), since
        ///   the Hebrew root סימון also means road striping — a real, agent-handled fix;</item>
        /// <item>English nouns are whole-word (so "piping"/"flagged" don't match "pin"/"flag").</item>
        /// </list>
        /// The caller ALSO gates on pins actually being present, which resolves the residual
        /// ambiguity of a bare "הסר את הסימון".
        /// </summary>
        private static bool IsClearMarkersIntent(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string t = text.Trim().ToLowerInvariant();

            // Domain qualifiers → this is a request about a real drawing element (road
            // markings, lanes, piping), not the overlay pins. Send it to the agent.
            string[] domainQualifiers =
            {
                "דרך", "נתיב", "הפרדה", "מעבר", "כביש", "צביעה", "צבע",
                "pipe", "piping", "lane", "road", "striping",
            };
            foreach (var q in domainQualifiers)
                if (t.Contains(q)) return false;

            bool hasVerb =
                t.Contains("הסר") || t.Contains("הסיר") || t.Contains("תסיר") ||
                t.Contains("מחק") || t.Contains("מחוק") ||
                t.Contains("נקה") || t.Contains("הורד") ||
                t.Contains("הסתר") || t.Contains("הסתיר") ||
                System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(remove|clear|hide|delete)\b");

            // "סימ" covers סימון/סימונים/סימוני/הסימונים (final- vs regular-nun forms);
            // English nouns are whole-word to dodge piping⊃pin / flagged⊃flag.
            bool hasNoun =
                t.Contains("סימ") || t.Contains("סמן") || t.Contains("סמני") ||
                t.Contains("פין") || t.Contains("דגל") ||
                System.Text.RegularExpressions.Regex.IsMatch(t, @"\b(markers?|pins?|flags?)\b");

            return hasVerb && hasNoun;
        }

        /// <summary>
        /// Enters the fix phase when a fix plan arrives: matches each fix-plan item to the
        /// analysis pin it addresses so the fixable pins turn yellow, then re-shows the FULL
        /// pin set (fixable = yellow, everything else keeps its severity color). No pin is ever
        /// hidden — the engineer keeps the whole finding map through the fix flow.
        /// </summary>
        private void EnterFixPhaseForPlan(WebSocket.FixPlanPayload? plan)
        {
            try
            {
                if (plan == null || _lastProblemMarkers.Count == 0)
                    return;

                int matched = Services.Overlay.MarkerFixMatcher.AssignFixItems(
                    _lastProblemMarkers, plan.Items);

                // Own the display for the fix flow (so async DOM reposts are ignored) and
                // re-render ALL pins with the new fixable=yellow coloring.
                _markerPhase = MarkerPhase.Fix;
                ShowOverlayDeferred(_lastProblemMarkers);
                System.Diagnostics.Debug.WriteLine(
                    $"[MahodAI] Fix phase: {matched}/{plan.Items.Count} plan items matched to pins; showing all {_lastProblemMarkers.Count}.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] EnterFixPhaseForPlan error: {ex.Message}");
            }
        }

        /// <summary>
        /// Applies a fix result to the shown pins: each applied item turns its pin green, each
        /// errored item red, each skipped / manual-required item yellow. Pins with no fix stay
        /// their severity color. ALL pins remain on the drawing. Only runs while the Fix phase
        /// is active (i.e. a plan was matched). Uses the shared <see cref="Services.FixResultClassifier"/>
        /// so the pin colors agree exactly with the result table's ✔/⚠/✖ buckets.
        /// </summary>
        private void ApplyFixResultToMarkers(WebSocket.FixResultPayload? result)
        {
            try
            {
                if (result == null || _markerPhase != MarkerPhase.Fix || _lastProblemMarkers.Count == 0)
                    return;

                var byId = _lastProblemMarkers
                    .Where(m => m.FixItemId != null)
                    .ToDictionary(m => m.FixItemId!, m => m);

                foreach (var item in result.Results)
                {
                    if (string.IsNullOrEmpty(item.ItemId) || !byId.TryGetValue(item.ItemId, out var marker))
                        continue;

                    marker.FixState = Services.FixResultClassifier.Classify(item) switch
                    {
                        Services.FixOutcome.Applied => Models.MarkerFixState.Fixed,
                        Services.FixOutcome.Manual => Models.MarkerFixState.ManualRequired,
                        _ => Models.MarkerFixState.Failed,
                    };
                }

                ShowOverlayDeferred(_lastProblemMarkers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyFixResultToMarkers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Demotes pins whose fix was applied but failed post-fix verification (read-back value
        /// wrong) from green to red <see cref="Models.MarkerFixState.VerifyFailed"/>. Keeps the
        /// drawing in step with the result card, where those rows read "בוצע — האימות נכשל"
        /// and are counted separately from the clean successes.
        /// </summary>
        private void ApplyFixVerifyToMarkers(WebSocket.FixVerifyResultPayload? verify)
        {
            try
            {
                if (verify?.Items == null || _markerPhase != MarkerPhase.Fix || _lastProblemMarkers.Count == 0)
                    return;

                var byId = _lastProblemMarkers
                    .Where(m => m.FixItemId != null)
                    .ToDictionary(m => m.FixItemId!, m => m);

                bool changed = false;
                foreach (var v in verify.Items)
                {
                    if (v.Verified != false || string.IsNullOrEmpty(v.ItemId)) continue;
                    if (!byId.TryGetValue(v.ItemId, out var marker)) continue;
                    // Only demote a pin we had marked applied — a manual/failed pin stays as is.
                    if (marker.FixState == Models.MarkerFixState.Fixed)
                    {
                        marker.FixState = Models.MarkerFixState.VerifyFailed;
                        changed = true;
                    }
                }

                if (changed)
                    ShowOverlayDeferred(_lastProblemMarkers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ApplyFixVerifyToMarkers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns to the post-analysis view (all pins, severity colors) — used when a fix
        /// plan is declined, so the engineer sees the full finding set again.
        /// </summary>
        private void RestoreAnalysisMarkers()
        {
            try
            {
                if (_lastProblemMarkers.Count == 0)
                    return;
                foreach (var m in _lastProblemMarkers)
                {
                    m.FixItemId = null;
                    m.FixState = Models.MarkerFixState.None;
                }
                _markerPhase = MarkerPhase.Analysis;
                ShowOverlayDeferred(_lastProblemMarkers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] RestoreAnalysisMarkers error: {ex.Message}");
            }
        }

        /// <summary>Show/hide the on-drawing problem pins from the most recent analysis.</summary>
        private void ToggleProblemOverlay()
        {
            try
            {
                if (_problemOverlay.IsVisible)
                {
                    _problemOverlay.Hide();
                    return;
                }
                if (_lastProblemMarkers.Count == 0)
                {
                    AddSystemMessage("אין בעיות מהניתוח להצגה על השרטוט. יש להריץ קריאת שרטוט תחילה.");
                    return;
                }
                // Always show the full pin set — the fix phase recolors pins, it never hides them.
                ShowOverlayDeferred(_lastProblemMarkers);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MahodAI] ToggleProblemOverlay error: {ex.Message}");
            }
        }

        private void ClearProblemOverlay()
        {
            _overlayGeneration++;   // invalidate any queued Show
            try { _problemOverlay.Hide(); } catch { /* best effort */ }
        }

        private void HandleShare(string textContent)
        {
            try
            {
                var subject = Uri.EscapeDataString($"MahodAI Report — {DateTime.Now:dd/MM/yyyy HH:mm}");
                var body = Uri.EscapeDataString(textContent.Length > 1500
                    ? textContent.Substring(0, 1500) + "..."
                    : textContent);

                Process.Start(new ProcessStartInfo($"mailto:?subject={subject}&body={body}")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HandleShare error: {ex.Message}");
                WpfMessageBox.Show($"שגיאה בפתיחת לקוח הדוא\"ל:\n{ex.Message}", "MahodAI", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void GenerateDocx(string filePath, string htmlContent)
        {
            using var doc = WordprocessingDocument.Create(filePath, WordprocessingDocumentType.Document);
            var mainPart = doc.AddMainDocumentPart();

            var plainText = ChatHtmlRenderer.StripHtmlToText(htmlContent);

            mainPart.Document = new Document(
                new Body(
                    new Paragraph(
                        new ParagraphProperties(
                            new Justification { Val = JustificationValues.Right },
                            new BiDi()
                        ),
                        new Run(
                            new RunProperties(new Bold(), new FontSize { Val = "32" }),
                            new Text("דוח ניתוח MahodAI")
                        )
                    ),
                    new Paragraph(
                        new ParagraphProperties(
                            new Justification { Val = JustificationValues.Right },
                            new BiDi()
                        ),
                        new Run(
                            new RunProperties(new FontSize { Val = "20" }, new DocumentFormat.OpenXml.Wordprocessing.Color { Val = "666666" }),
                            new Text($"נוצר ב-{DateTime.Now:dd/MM/yyyy HH:mm}")
                        )
                    ),
                    new Paragraph(),
                    new Paragraph(
                        new ParagraphProperties(
                            new Justification { Val = JustificationValues.Right },
                            new BiDi()
                        ),
                        new Run(new Text(plainText))
                    )
                )
            );
        }

        #endregion
    }
}
