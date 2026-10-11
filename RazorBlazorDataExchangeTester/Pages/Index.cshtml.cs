using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;

namespace RazorBlazorDataExchangeTester.Pages
{
    public class IndexModel : PageModel, IDisposable
    {
        private const string CounterProperty = "XCounter";
        private const string JsBridgeProperty = "JsDomBridgeEnabled";
        private const string SessionKey = "RazorBlazorDataExchangeSessionId";
        private const string ActorId = "Razor:/Index";

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly RazorChangeEventObserver _changeObserver;
        private int _disposed;
        // One DOM command channel per live Razor HTTP request.
        private readonly RazorDomBridge _dom;

        public IndexModel(
            RazorBlazorDataExchange dataExchange,
            IHttpContextAccessor httpContextAccessor,
            RazorChangeEventObserver changeObserver,
            RazorDomBridge domBridge)
        {
            DataExchange = dataExchange;
            _httpContextAccessor = httpContextAccessor;
            _changeObserver = changeObserver;
            _dom = domBridge;

            // As in the original Razor sample, the request-scoped PageModel handles
            // DataChanged directly while this HTTP request is still active.
            SessionId = GetSessionId();

            // The singleton broker must NEVER retain a completed Razor PageModel.
            // Register an explicit end-of-response cleanup rather than relying on
            // PageModel activation/disposal conventions.
            _httpContextAccessor.HttpContext!.Response.RegisterForDispose(this);
            DataExchange.DataChanged += OnDataChanged;
        }

        public RazorBlazorDataExchange DataExchange { get; }
        public int XCounter { get; set; }
        public string SessionId { get; set; } = string.Empty;

        // The demo enables DOM synchronization for both Blazor counters by default.
        // An explicit query flag disables only Counter2's JS bridge for E2E isolation tests.
        public bool EnableCounter2JsDomBridge { get; private set; } = true;
        public bool JsBridgeEnabled { get; private set; } = true;
        public string LastObservedChangeActor { get; private set; } = "none";
        public string LastObservedChangeProperty { get; private set; } = "none";
        public string LastObservedChangeValue { get; private set; } = "none";
        public long LastObservedChangeVersion { get; private set; }

        public void OnGet()
        {
            EnableCounter2JsDomBridge = !string.Equals(
                Request.Query["disableCounter2JsBridge"].ToString(),
                "true",
                StringComparison.OrdinalIgnoreCase);

            SessionId = GetSessionId();
            if (DataExchange.TryGet<bool>(SessionId, JsBridgeProperty, out var storedJsBridgeEnabled))
            {
                JsBridgeEnabled = storedJsBridgeEnabled;
            }
            else
            {
                JsBridgeEnabled = true;
                DataExchange.StoreValue(SessionId, JsBridgeProperty, true, ActorId);
            }

            if (DataExchange.TryGet<int>(SessionId, CounterProperty, out var current))
            {
                XCounter = current;
            }
            else
            {
                XCounter = 0;
                DataExchange.StoreValue(SessionId, CounterProperty, XCounter, ActorId);
            }

            LoadObservedChange();
        }

        public IActionResult OnPostIncrementa()
        {
            SessionId = GetSessionId();
            var message = DataExchange.Update<int>(
                SessionId,
                CounterProperty,
                current => current + 1,
                ActorId);

            XCounter = message?.Value ?? DataExchange.Get<int>(SessionId, CounterProperty);
            return Page();
        }

        public async Task<JsonResult> OnPostIncrementCounter(CancellationToken cancellationToken)
        {
            SessionId = GetSessionId();

            var message = await DataExchange.UpdateAsync<int>(
                SessionId,
                CounterProperty,
                current => current + 1,
                ActorId,
                cancellationToken: cancellationToken);

            XCounter = message?.Value ?? DataExchange.Get<int>(SessionId, CounterProperty);
            return new JsonResult(new { value = XCounter, version = message?.Version });
        }

        // Reads the event actually captured by the Razor-side server observer.
        // This GET does not publish or simulate any change.
        public JsonResult OnGetLastChange()
        {
            var sessionId = GetSessionId();
            Response.Headers["Cache-Control"] = "no-store";

            if (!_changeObserver.TryGetLast(sessionId, out var change) || change is null)
                return new JsonResult(new { hasChange = false });

            return new JsonResult(new
            {
                hasChange = true,
                actor = change.ActorId,
                property = change.PropertyName,
                value = change.Value?.ToString() ?? "<null>",
                version = change.Version,
                messageId = change.MessageId
            });
        }

        // A native Razor checkbox posts its new state, which is published to the
        // singleton broker. All Blazor components receive the same DataChanged event.
        public async Task<IActionResult> OnPostSetJsBridgeAsync(
            [FromBody] JsBridgeSetting? setting,
            CancellationToken cancellationToken)
        {
            if (setting is null)
                return BadRequest(new { error = "Missing JS bridge setting." });

            var sessionId = GetSessionId();
            var message = await DataExchange.PublishAsync(
                sessionId,
                JsBridgeProperty,
                setting.Enabled,
                ActorId,
                cancellationToken: cancellationToken);

            return new JsonResult(new { enabled = message?.Value ?? setting.Enabled });
        }

        public sealed class JsBridgeSetting
        {
            public bool Enabled { get; set; }
        }

        /// <summary>
        /// Live event handler owned by the Razor PageModel. The browser's SSE request
        /// keeps this instance alive. No persistent observer or polling is used to
        /// deliver live DataChanged notifications.
        /// </summary>
        private void OnDataChanged(object? sender, RazorBlazorDataChangedEventArgs e)
        {
            if (Volatile.Read(ref _disposed) != 0 ||
                !string.Equals(SessionId, e.SessionId, StringComparison.Ordinal))
                return;

            if (e.PropertyName != CounterProperty && e.PropertyName != JsBridgeProperty)
                return;

            // Preserve the original actor filter for PageModel-local counter state.
            if (e.PropertyName == CounterProperty &&
                e.Value is int counter &&
                DataExchange.ShouldProcessChange(SessionId, ActorId, e, CounterProperty))
            {
                XCounter = counter;
            }

            // The PageModel decides what to change. The reusable C# bridge
            // serializes a typed batch, and the generic JS library applies it.
            // In particular, no Blazor JSInterop call is required here.
            var commands = new List<RazorDomBridge.Command>
            {
                RazorDomBridge.Command.Text("razor-observed-change-actor", e.ActorId),
                RazorDomBridge.Command.Text("razor-observed-change-property", e.PropertyName),
                RazorDomBridge.Command.Text("razor-observed-change-value", e.Value?.ToString() ?? "<null>"),
                RazorDomBridge.Command.Text("razor-observed-change-version", e.Version)
            };

            if (e.PropertyName == CounterProperty && e.Value is int current)
            {
                commands.Add(RazorDomBridge.Command.Text("counter-value", current));
                commands.Add(RazorDomBridge.Command.Text("last-update-source", e.ActorId));
                commands.Add(RazorDomBridge.Command.Text(
                    "razor-dom-transport", "Razor IndexModel.OnDataChanged → SSE"));

                // Generic DOM command examples; no element-specific JavaScript.
                commands.Add(RazorDomBridge.Command.InputValue("dom-counter-input", current));
                commands.Add(RazorDomBridge.Command.Checked("dom-counter-odd", current % 2 != 0));
                commands.Add(RazorDomBridge.Command.Visible("dom-event-indicator", true));
                commands.Add(RazorDomBridge.Command.CssClass("counter-value", "dom-synced", true));
                commands.Add(RazorDomBridge.Command.Attribute(
                    "counter-value", "title", $"XCounter, event version {e.Version}"));
            }

            _dom.Batch(commands.ToArray());
        }

        /// <summary>
        /// This Razor handler stays alive for the SSE request. The connection
        /// carries typed DOM commands created by this PageModel's .NET events.
        /// </summary>
        public async Task OnGetCounterEventsAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _dom.StreamAsync(Response, cancellationToken, onConnected: () =>
                {
                    // Reconcile on initial connection and EventSource reconnect.
                    // Reading broker state here does NOT simulate DataChanged.
                    var current = DataExchange.TryGet<int>(SessionId, CounterProperty, out var stored)
                        ? stored : 0;

                    _dom.Batch(
                        RazorDomBridge.Command.Text("counter-value", current),
                        RazorDomBridge.Command.InputValue("dom-counter-input", current),
                        RazorDomBridge.Command.Checked("dom-counter-odd", current % 2 != 0));
                });
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            DataExchange.DataChanged -= OnDataChanged;
            _dom.Dispose();
        }

        private void LoadObservedChange()
        {
            if (!_changeObserver.TryGetLast(SessionId, out var change) || change is null)
                return;

            LastObservedChangeActor = change.ActorId;
            LastObservedChangeProperty = change.PropertyName;
            LastObservedChangeValue = change.Value?.ToString() ?? "<null>";
            LastObservedChangeVersion = change.Version;
        }

        private string GetSessionId()
        {
            var session = _httpContextAccessor.HttpContext?.Session
                ?? throw new InvalidOperationException("ASP.NET Core session is not available for this request.");

            var sessionId = session.GetString(SessionKey);
            if (!string.IsNullOrWhiteSpace(sessionId))
                return sessionId;

            sessionId = Guid.NewGuid().ToString("N");
            session.SetString(SessionKey, sessionId);
            return sessionId;
        }
    }
}
