using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RazorBlazorDataExchangeTester.Pages
{
    public class IndexModel : PageModel
    {
        private const string CounterProperty = "XCounter";
        private const string SessionKey = "RazorBlazorDataExchangeSessionId";
        private const string ActorId = "Razor:/Index";

        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly RazorChangeEventObserver _changeObserver;

        public IndexModel(
            RazorBlazorDataExchange dataExchange,
            IHttpContextAccessor httpContextAccessor,
            RazorChangeEventObserver changeObserver)
        {
            DataExchange = dataExchange;
            _httpContextAccessor = httpContextAccessor;
            _changeObserver = changeObserver;
        }

        public RazorBlazorDataExchange DataExchange { get; }
        public int XCounter { get; set; }
        public string SessionId { get; set; } = string.Empty;

        // The demo enables DOM synchronization for both Blazor counters by default.
        // An explicit query flag disables only Counter2's JS bridge for E2E isolation tests.
        public bool EnableCounter2JsDomBridge { get; private set; } = true;
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
